using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
// Console validation harness for NvimCore against REAL nvim (nvim 0.12.x).
// Steps:
//   1) Spawn: nvim --clean --headless --listen 127.0.0.1:<PORT>
//   2) TCP connect with retries (nvim opens the listener asynchronously).
//   3) Call nvim_get_api_info and nvim_eval — verifies request/response correlation.
//   4) Send ui_attach notification, expect at least one redraw notification.

class Program
{
    private const int Port = 41281;

    private static async Task<int> Main(string[] args)
    {
        Console.WriteLine($"[rpc-test] port={Port}");

        // Isolated probe: spawn + connect + IDLE (no RPC traffic). Checks whether nvim dies on its own.
        if (args.Length > 0 && args[0] == "--idle") return await IdleProbe();

        // Spawn semantics = Python e2e_test.py (the PROVEN-WORKING reference config):
        //   Popen(stdout=PIPE, stderr=PIPE) with stdin inherited.
        // Any deviation fails: fully-inherited stdio -> connection EOF/reset mid-response;
        // redirected stdin (closed pipe) -> headless nvim sees stdin EOF and quits.
        var psi = new ProcessStartInfo("nvim")
        {
            Arguments = $"--clean --headless --listen 127.0.0.1:{Port}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        Process? proc = null;
        int code = 99;
        string outLog = "", errLog = "";
        try
        {
            proc = Process.Start(psi);
            if (proc == null) throw new InvalidOperationException("ProcessStart returned null");
            Console.WriteLine($"[rpc-test] nvim pid={proc.Id} (stdout+stderr piped, stdin inherited)");

            // Drain the pipes in background (blocking ReadToEnd would deadlock before tests run;
            // a full buffer can also stall the child — same reason Python's Popen works with PIPE).
            var outTask = Task.Run(() => { try { return proc.StandardOutput.ReadToEnd(); } catch { return string.Empty; } });
            var errTask = Task.Run(() => { try { return proc.StandardError.ReadToEnd(); } catch { return string.Empty; } });

            code = RunTestAsync().GetAwaiter().GetResult();
            if (code == 0)
            {
                Console.WriteLine("[rpc-test] DONE: SUCCESS");
            }
            else
            {
                Console.WriteLine($"[rpc-test] DONE: FAILED ({code})");
                try
                {
                    int? rc = proc.HasExited ? (int?)proc.ExitCode : null;
                    bool alive = !proc.HasExited;
                    Console.WriteLine($"[rpc-test] nvim state after failure: alive={alive} exitcode={(rc ?? -1)}");
                    proc.WaitForExit(2000);
                    if (!outTask.Wait(TimeSpan.FromSeconds(1))) outLog = "<stdout drain pending>";
                    else if (outTask.IsCompletedSuccessfully) outLog = outTask.Result;
                    if (!errTask.Wait(TimeSpan.FromSeconds(1))) errLog = "<stderr drain pending>";
                    else if (errTask.IsCompletedSuccessfully) errLog = errTask.Result;
                    if (outLog.Length > 0) Console.WriteLine($"[rpc-test] nvim STDOUT: {outLog.Trim()}");
                    if (errLog.Length > 0) Console.WriteLine($"[rpc-test] nvim STDERR: {errLog.Trim()}");
                }
                catch (Exception ex2) { Console.WriteLine($"[rpc-test] state read error: {ex2.Message}"); }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[rpc-test] FATAL: {ex.Message}");
            code = 6;
        }

        // Clean up: kill nvim if it's still running.
        try
        {
            if (proc != null && !proc.HasExited) proc.Kill(entireProcessTree: true);
            else Console.WriteLine("[rpc-test] nvim already exited");
        }
        catch (Exception ex) { Console.WriteLine($"[rpc-test] cleanup error: {ex.Message}"); }

        return code;
    }

    private static async Task<int> RunTestAsync()
    {
        NvimCore.NvimClient? client = null;

        // --- Connect with retries. ---
        for (int attempt = 1; ; attempt++)
        {
            if (attempt > 40) throw new TimeoutException($"nvim never opened its TCP listener after {attempt} attempts");
            try
            {
                client = await NvimCore.NvimClient.ConnectAsync("127.0.0.1", Port);
                break;
            }
            catch (SocketException ex)
            {
                Console.WriteLine($"[rpc-test] connect attempt {attempt} failed ({ex.Message}), retrying in 250ms...");
                await Task.Delay(250);
            }
        }

        using var nvim = client!;
        Console.WriteLine("[rpc-test] connected to nvim.");

        int errors = 0;

        // --- Test 1: request/response (nvim_get_api_info). ---
        try
        {
            var sw = Stopwatch.StartNew();
            object? apiInfo = await nvim.CallAsync("nvim_get_api_info");
            Console.WriteLine($"[rpc-test] nvim_get_api_info => {FormatBrief(apiInfo)}  ({sw.ElapsedMilliseconds}ms)");
        }
        catch (Exception ex) { errors++; Console.WriteLine($"[rpc-test] TEST1 FAIL: {ex.Message}"); }

        // --- Test 2: request/response with argument (nvim_eval). ---
        try
        {
            object? evaled = await nvim.CallAsync("nvim_eval", "1 + 40");
            Console.WriteLine($"[rpc-test] nvim_eval('1 + 40') => {FormatBrief(evaled)}");
        }
        catch (Exception ex) { errors++; Console.WriteLine($"[rpc-test] TEST2 FAIL: {ex.Message}"); }

        // --- Test 3: ui_attach notification, expect redraw notifications. ---
        var gotRedraw = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int redrawCount = 0;

        nvim.OnNotification += (method, args) =>
        {
            if (method != "redraw") return;
            redrawCount++;
            Console.WriteLine($"[rpc-test] redraw #{redrawCount}: {FormatBrief(args)}");
            gotRedraw.TrySetResult(true);
        };

        try
        {
            // This nvim build's nvim_ui_attach takes THREE arguments (width, height, options-map);
            // two args produces "Wrong number of arguments: expecting 3 but got 2".
            var uiOptions = new Dictionary<string, object?>();
            await nvim.NotifyAsync("nvim_ui_attach", 80, 24, uiOptions).ConfigureAwait(false);
            Console.WriteLine("[rpc-test] sent nvim_ui_attach(80, 24), waiting for redraw...");

            var winner = await Task.WhenAny(gotRedraw.Task, Task.Delay(5000));
            if (winner == gotRedraw.Task && await gotRedraw.Task)
            {
                await Task.Delay(300); // let a couple more batches trickle in.
                Console.WriteLine($"[rpc-test] redraw notifications received: OK ({redrawCount} batch(es))");
            }
            else
            {
                errors++;
                Console.WriteLine("[rpc-test] TEST3 FAIL: TIMEOUT waiting for redraw notification.");
            }
        }
        catch (Exception ex) { errors++; Console.WriteLine($"[rpc-test] TEST3 FAIL: {ex.Message}"); }

        return errors == 0 ? 0 : errors;
    }

    // Spawn + connect + IDLE (no RPC traffic). Reports whether nvim dies on its own under the C# parent.
    private static async Task<int> IdleProbe()
    {
        try
        {
            var psi = new ProcessStartInfo("nvim")
            {
                Arguments = $"--clean --headless --listen 127.0.0.1:{Port}",
                UseShellExecute = false,
            };
            var proc = Process.Start(psi)!;
            Console.WriteLine($"[idle] spawned pid={proc.Id}");

            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            bool ok = false;
            for (int i = 0; i < 15 && !ok; i++)
            {
                try { await Task.Run(() => sock.Connect("127.0.0.1", Port)).ConfigureAwait(false); ok = true; }
                catch { Thread.Sleep(400); }
            }
            Console.WriteLine(ok ? "[idle] connected" : "[idle] connect failed");

            for (int i = 0; i < 30 && !proc.HasExited; i++)
            {
                Thread.Sleep(1000);
                if (!proc.HasExited)
                    Console.WriteLine($"[idle] t+{i + 1}s: still alive");
            }

            try { proc.WaitForExit(2000); } catch { }
            if (proc.HasExited)
            {
                var err = string.Empty;
                try { err = proc.StandardError.ReadToEnd(); } catch { }
                Console.WriteLine($"[idle] EXITED code={proc.ExitCode} stderr=<{err}>");
                return 0;
            }
            Console.WriteLine("[idle] still alive after 30s idle — spawn is fine, death needs traffic");
            proc.Kill(entireProcessTree: true);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[idle] FAIL: {ex.Message}");
            return 2;
        }
    }

    private static string FormatBrief(object? o)
    {
        switch (o)
        {
            case null: return "nil";
            case bool b: return b ? "true" : "false";
            case long l when l is >= -1024 and <= 65536: return l.ToString();
            case string s: return "\"" + (s.Length > 48 ? s[..48] + "…" : s) + "\"";
            case object?[] arr:
                {
                    if (arr.Length > 6) return $"[array len={arr.Length}]";
                    var parts = new List<string>();
                    foreach (var x in arr) parts.Add(FormatBrief(x));
                    return "[" + string.Join(", ", parts) + "]";
                }
            default: return o.GetType().Name;
        }
    }
}
