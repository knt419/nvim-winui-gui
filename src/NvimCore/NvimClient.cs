using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NvimCore;

/// <summary>
/// Neovim msgpack-RPC client over TCP. Connects to an nvim instance started with:
///   nvim --listen 127.0.0.1:&lt;port&gt;
///
/// Wire format (verified against nvim 0.12, src/nvim/msgpack_rpc/channel.c):
///   Request from client:  [0, id:int, method:str, params:[...]]
///   Response to client:   [1, response_id:int, error|null, result]
///   Notification (nvim):  [2, method_name:str, args:[...]]   e.g. redraw batches
///
/// Architecture: single reader task pumps the TCP stream through MsgpackStreamDecoder;
/// responses are correlated to pending calls by request id; notifications are raised
/// via events. Writes are serialized with a semaphore.
/// </summary>
public sealed class NvimClient : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly MsgpackStreamDecoder _decoder = new();
    private readonly SemaphoreSlim _writeSemaphore = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<object?>> _pending = new();
    private int _nextId = 1;
    private bool _disposed;

    // Connection-loss latch: set once when the read loop ends. CallAsync awaits this alongside its
    // own TCS so a call registered AFTER the connection dies fails fast instead of hanging forever
    // (the fail-all-pending pass at loop end only sees calls that were pending AT THAT MOMENT).
    private readonly TaskCompletionSource<Exception> _connLost = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Diagnostics-only raw byte logger (guarded by NVIM_LOG_BYTES=1).
    private static System.IO.Stream? s_fileLog;

    /// <summary>Raised for every [2, method, args] notification from nvim (e.g. "redraw").</summary>
    public event Action<string, object?[]?>? OnNotification;

    private sealed record PendingCall(int Id);

    private NvimClient(TcpClient tcp)
    {
        _tcp = tcp;
        try { tcp.NoDelay = true; } catch (SocketException) { /* non-fatal */ }
        _stream = tcp.GetStream();
        Task.Run(ReadLoopAsync); // fire-and-forget reader task
    }

    /// <summary>Connect to an nvim instance listening on host:port.</summary>
    public static async Task<NvimClient> ConnectAsync(string host, int port)
    {
        var tcp = new TcpClient();

        // BUG HISTORY (verify-core): the api_info response is ~31.6 KB and arrives in one burst.
        // Windows' DEFAULT socket receive buffer is only ~8 KB — the kernel fills it, the TCP
        // window collapses to zero mid-frame (~28.7 KB received), nvim's send blocks until its
        // linger timeout and then RSTs the connection ("forcibly closed by remote host").
        // The Python e2e worked because its recv() drains 64 KB per call. Enlarge before connect.
        try { tcp.ReceiveBufferSize = 131072; } catch (SocketException) { /* best effort */ }

        await tcp.ConnectAsync(host, port).ConfigureAwait(false);
        return new NvimClient(tcp);
    }

    /// <summary>Make an RPC call and await its result (the 4th element of the response array).</summary>
    public async Task<object?> CallAsync(string method, params object?[] args)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NvimClient));

        long id = _nextId++;
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            byte[] frame = MsgPackEncoder.EncodeRequest((int)id, method, args);
            await SendFrameAsync(frame).ConfigureAwait(false);

            // Race the response against connection loss so we fail fast instead of hanging.
            var winner = await Task.WhenAny(tcs.Task, _connLost.Task).ConfigureAwait(false);
            if (winner == _connLost.Task)
                throw new IOException($"nvim connection lost while awaiting '{method}'");
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Fire-and-forget notification: [2, method, args].</summary>
    public Task NotifyAsync(string method, params object?[] args)
    {
        byte[] frame = MsgPackEncoder.EncodeNotification(method, args);
        return SendFrameAsync(frame);
    }

    private async Task SendFrameAsync(byte[] frame)
    {
        await _writeSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, 0, frame.Length).ConfigureAwait(false);
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        var buf = new byte[8192];
        try
        {
            while (true)
            {
                int n;
                try
                {
                    n = await _stream.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
                }
                catch (IOException) when (_disposed) { return; }

                if (n <= 0) break; // EOF: nvim closed the connection.

                _decoder.Append(buf, 0, n);

                // DEBUG: Log raw bytes received for diagnostics (file-based to avoid stdout truncation).
                try {
                    if (Environment.GetEnvironmentVariable("NVIM_LOG_BYTES") == "1" && s_fileLog == null) {
                        var logPath = Environment.GetEnvironmentVariable("NVIM_LOG_FILE");
                        s_fileLog = File.Open(logPath ?? Path.Combine(Path.GetTempPath(), "nvim_bytes.hex"), FileMode.Create, FileAccess.Write);
                    }
                    if (s_fileLog != null) { await s_fileLog.WriteAsync(buf.AsMemory(0, n)); }
                } catch { /* diagnostics must never break the protocol */ }

                object? msg;
                while (_decoder.TryDecodeMessage(out msg))
                {
                    try { Dispatch(msg); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[NvimClient] dispatch error: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or NotSupportedException)
        {
            if (!_disposed)
            {
                Console.Error.WriteLine($"[NvimClient] read loop ended: {ex.Message}");
            }
        }

        // Connection lost: fail all pending calls so callers don't hang forever.
        foreach (var kv in _pending)
        {
            if (_pending.TryRemove(kv.Key, out var tcs))
                tcs.TrySetException(new IOException("nvim connection lost"));
        }
        _connLost.TrySetResult(new IOException("read loop ended"));
    }

    private void Dispatch(object? msg)
    {
        if (msg is not object?[] rpc || rpc.Length == 0 || rpc[0] is not long type) return;

        switch (type)
        {
            case MsgPackEncoder.TypeResponse: // [1, response_id:int, error|null, result]
                {
                    if (rpc.Length < 2 || rpc[1] is not long rid) break;

                    object? err = rpc.Length >= 3 ? rpc[2] : null!;
                    object? result = rpc.Length >= 4 ? rpc[3] : null!;

                    // nvim error shape: [error_type, msg] (msgpack-rpc standard is more elaborate).
                    string? errMsg = null;
                    if (err != null)
                    {
                        if (err is object?[] earr && earr.Length > 1 && earr[1] is string m) errMsg = m;
                        else if (err is string s) errMsg = s;
                        else errMsg = "<unknown error>";
                    }

                    if (_pending.TryRemove(rid, out var tcs))
                    {
                        if (errMsg != null)
                            tcs.TrySetException(new InvalidOperationException($"nvim RPC error: {errMsg}"));
                        else
                            tcs.TrySetResult(result);
                    }
                    break;
                }

            case MsgPackEncoder.TypeNotification: // [2, method:str, args:[...]]
                if (rpc.Length >= 2 && rpc[1] is string mthod)
                {
                    object?[]? a = null!;
                    if (rpc.Length >= 3 && rpc[2] is object?[] arr) a = arr;
                    OnNotification?.Invoke(mthod, a);
                }
                break;

            case MsgPackEncoder.TypeRequest: // [0, id, method, params] — nvim→client request (rare).
            default:
                Console.Error.WriteLine($"[NvimClient] unhandled frame type {type}");
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { s_fileLog?.Flush(); } catch { /* diagnostics only */ }
        try { s_fileLog?.Close(); } catch { /* diagnostics only */ }
        s_fileLog = null;
        try { _stream.Dispose(); } catch { }
        try { _tcp.Close(); } catch { }
    }
}
