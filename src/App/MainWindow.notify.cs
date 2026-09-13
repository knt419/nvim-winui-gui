using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using System.Diagnostics;
using NvimCore;

namespace NvimWinUIGui;

public partial class MainWindow
{
    private void SetStatus(string s)
    {
        try { StatusText.Text = s; } catch { /* XAML already torn down (shutdown path); ignore */ }
    }

    private static string DiagVal(object? v)
    {
        if (v is null) return "null";
        if (v is string s) return "\"" + (s.Length > 12 ? s.Substring(0, 12) + "…" : s) + "\"";
        if (v is object?[] a)
        {
            var first = a.Length > 0 ? a[0] : null;
            return "[" + a.Length + "]" + (first != null ? first.GetType().Name + "[]" : "");
        }
        string t = v.GetType().Name;
        string s2 = v.ToString() ?? "?";
        if (s2.Length > 14) s2 = s2.Substring(0, 14) + "…";
        return t + ":" + s2;
    }

    // Round-8 diagnostics: event-order trace (first 60 redraw events), every grid_resize in full,
    // and the RAW shape of grid_line cells so we can see exactly what nvim sends vs how we read it.
    private int _evTraceCount;
    private void TraceEvent(string name, object?[] a)
    {
        if (_evTraceCount < 60)
        {
            _evTraceCount++;
            LogStartup("EVTRACE " + name + " len=" + a.Length);
        }
    }

    private static string StartupLogPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NvimWinUIGui", "startup.log");

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        LogStartup("OnLoadedAsync ENTER (UI thread alive)");
        SetStatus("spawning nvim...");
        try
        {
            int port = FindFreePort();
            // --headless: without it, nvim's console-UI init path hangs startup when all stdio is
            // redirected (no real console) and the --listen socket never opens. Verified empirically 2026-08-24.
            var psi = new ProcessStartInfo(NvimPath, $"--listen 127.0.0.1:{port} --headless")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            _nvimProc = System.Diagnostics.Process.Start(psi);
            if (_nvimProc == null) { SetStatus("failed to spawn nvim"); return; }
            LogStartup("spawned nvim pid=" + _nvimProc.Id + " port=" + port);
            _nvimProc.ErrorDataReceived += (s, e) => { if (e.Data != null) LogStartup("NVIM-ERR " + e.Data); };
            _nvimProc.BeginErrorReadLine();
            var client = await ConnectWithRetryAsync(port, 5);
            LogStartup("tcp connect OK");
            _client = client;
            object? info = await _client.CallAsync("nvim_get_api_info");
            LogStartup("api_info returned (" + (info is null ? "null" : info.ToString()!.Length) + " chars)");
            SetStatus($"connected (port {port}), API v{ExtractApiMajor(info)}. attaching ui...");

            // Load guifont/guifontwide from nvim's settings before attaching the UI.
            // Format: "FontName:Style:Size" — we only care about FontName and Size.
            string? guifont = null, guifontwide = null;
            try {
                object? gf = await _client.CallAsync("nvim_get_value", "guifont");
                if (gf is string s) guifont = s;
                object? gfw = await _client.CallAsync("nvim_get_value", "guifontwide");
                if (gfw is string s2) guifontwide = s2;
            } catch { /* non-fatal: fall back to defaults */ }
            LogStartup($"guifont={guifont ?? ""} guifontwide={guifontwide ?? ""}");
            ParseNvimFont(guifont, out _narrowFont, out _narrowSize);
            if (!string.IsNullOrEmpty(guifontwide))
                ParseNvimFont(guifontwide, out _wideFont, out _wideSize);
            else { _wideFont = _narrowFont; _wideSize = _narrowSize; }

            _client.OnNotification += OnNvimNotification;
            // ext_linegrid: switch nvim to line-based grid events (grid_line/grid_clear/
            // cursor_position/hl_attr_define). Without it nvim emits only the legacy
            // terminal protocol (put/cursor_goto/move_cursor), which this handler does not
            // consume -> empty screen. Verified against runtime/doc/api-ui-events.txt (0.12).
            LogStartup("ATTACH-PRE sending ui_attach (notification)");
            // nvim_ui_attach is a notification per the nvim 0.12 RPC API — it does not send a
            // response, so CallAsync would hang forever waiting for one that never arrives.
            // Use NotifyAsync instead: fire-and-forget, then rely on redraw notifications to
            // confirm the UI was attached (same pattern as tools/rpc-test/Program.cs line 153).
            await _client.NotifyAsync("nvim_ui_attach", _cols, _rows, new Dictionary<string, object?> { ["rgb"] = true, ["ext_linegrid"] = true }).ConfigureAwait(false);
            LogStartup("ATTACH-POST ui_attach sent (no response expected; watching for redraw)");
            EnsureScreen(_rows, _cols);
            ScheduleRender();
            SetStatus($"ui attached ({_cols}x{_rows}). typing forwards to nvim.");

            // CRITICAL: avoid Ex-mode commands that can error (:w on an unnamed buffer = E32) —
            // such errors make nvim block at the hit-enter prompt and STOP processing RPC, so every
            // later CallAsync would hang forever. Plain typing goes through insert mode only.
            await _client.CallAsync("nvim_input", "ihello from nvim-winui-gui");
            LogStartup("POST-INPUT input response received");

            // Self-test: create a NEW buffer to avoid depending on nvim's initial state (scratch buffer, etc.).
            // This ensures the typed text goes into a known buffer and can be read back reliably.
            LogStartup("STEP creating test buffer");
            try
            {
                // nvim_create_buf takes TWO boolean args: {listed}, {scratch} — not a buffer name.
                // Buffers in nvim are identified by numeric ID only; there is no "name" parameter.
                object? testBuf = await _client.CallAsync("nvim_create_buf", false, false).ConfigureAwait(false);
                int testBufNum;
                if (testBuf is MsgpackStreamDecoder.MsgpackExt extHandle)
                {
                    string dataHex = BitConverter.ToString(extHandle.Data).Replace("-", "").ToLowerInvariant();
                    LogStartup($"FIXEXT TypeId={extHandle.TypeId} DataLen={extHandle.Data.Length} DataHex={dataHex}");
                    switch (extHandle.Data.Length)
                    {
                        case 1: testBufNum = extHandle.Data[0]; break;
                        case 2: testBufNum = (int)(extHandle.Data[0] << 8 | extHandle.Data[1]); break;
                        case 4: testBufNum = (int)System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(extHandle.Data.AsSpan()); break;
                        case 8: testBufNum = (int)System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(extHandle.Data.AsSpan()); break;
                        default: throw new InvalidOperationException($"Unexpected buffer handle data length: {extHandle.Data.Length}");
                    }
                }
                else
                    testBufNum = (int)testBuf!;

                LogStartup($"STEP got test buffer #{testBufNum}");
                // Switch to the test buffer so typing goes there
                await _client.CallAsync("nvim_set_current_buf", testBufNum).ConfigureAwait(false);
                LogStartup($"STEP switched to test buffer, current={await _client.CallAsync("nvim_get_current_buf").ConfigureAwait(false)}");
                // Type into it (using set_lines for deterministic self-test; nvim_input can be mode-dependent)
                // nvim_buf_set_lines(bufid, startline, endline, replace:bool, lines:[string]) — order matters.
                object?[] putArgs = { testBufNum, 0, 1, true, new string[] { "hello from nvim-winui-gui" } };
                await _client.CallAsync("nvim_buf_set_lines", putArgs).ConfigureAwait(false);
                LogStartup("POST-SET lines written to test buffer");
                // Wait for redraw
                await Task.Delay(1000).ConfigureAwait(false);
                // Read from the test buffer
                var lines = await _client.CallAsync("nvim_buf_get_lines", testBufNum, 0, 1, true).ConfigureAwait(false);
                LogStartup("STEP got lines (test buffer)");
                string line1 = (lines is object?[] la && la.Length > 0 && la[0] is string s) ? s : "<none>";
                // Count cells with a NON-SPACE glyph: every cell starts as " ", so Length>0 always
                // passed and masked an empty grid. Non-space count only rises when real text lands.
                int populated = 0;
                for (int i = 0; i < _cells.Length; i++)
                    if (_cells[i].Text.Trim().Length > 0) populated++;
                string row0 = "";
                for (int c2 = 0; c2 < Math.Min(_cols, 80); c2++) row0 += _cells[c2].Text;
                LogStartup($"SELFTEST line1=\"{line1}\" cells_total={_cells.Length} nonblank_cells={populated} " +
                            $"row0=[{row0}] EXPECT line1=hello from nvim-winui-gui, nonblank>25");
            }
            catch (Exception ex) { LogStartup("SELFTEST FAILED: " + ex.Message); }
        }
        catch (Exception ex)
        {
            LogStartup($"\nSTARTUP FAILED: {ex}");
            SetStatus($"STARTUP FAILED: {ex.Message}");
        }
    }

    private static void LogStartup(string s)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(StartupLogPath)!;
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(StartupLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {s}{System.Environment.NewLine}");
        }
        catch { /* best effort */ }
    }

    private void OnNvimNotification(string method, object?[]? args)
    {
        // Notifications arrive on the IO thread; XAML must only be touched from the UI thread.
        var ctx = _uiSyncCtx;
        if (ctx is not null && !ReferenceEquals(ctx, System.Threading.SynchronizationContext.Current))
        {
            try { ctx.Post(_ => HandleNotification(method, args), null); return; } catch { /* fall through */ }
        }
        HandleNotification(method, args);
    }

    private int _hnTrace;
    private int _hbCount;
    private void HandleNotification(string method, object?[]? args)
    {
        if (args is null) return;
        bool trc = Interlocked.Increment(ref _hnTrace) <= 40;
        if (trc) LogStartup($"HN ENTER #{_hnTrace} thread={System.Threading.Thread.CurrentThread.ManagedThreadId}");
        int hb = Interlocked.Increment(ref _hbCount);
        if (_hbCount % 50 == 0) LogStartup($"HB handle_notification count={_hbCount}");
        try
        {
            // nvim batches ALL redraw events into ONE notification: [2,"redraw",[[name,...],...]].
            // With ext_linegrid each inner event carries a leading grid_id: e.g. ["grid_line", grid, row, col_start, cells, wrap].
            if (method == "redraw")
            {
                foreach (var ev in args)
                {
                    if (ev is not object?[] ea || ea.Length < 1) continue;
                    // nvim sends redraw events as arrays. With ext_linegrid, each inner event is
                    // [grid_id, 0, "event_name", args...]; without it, just ["event_name", args...].
                    string ename;
                    object?[] eargs;
                    if (ea[0] is string s)
                    {
                        ename = s;
                        eargs = new object?[ea.Length - 1];
                        Array.Copy(ea, 1, eargs, 0, ea.Length - 1);
                    }
                    else if (ea.Length > 2 && ea[2] is string s2)
                    {
                        ename = s2;
                        eargs = new object?[ea.Length - 3];
                        Array.Copy(ea, 3, eargs, 0, ea.Length - 3);
                    }
                    else continue;
                    DispatchRedrawEvent(ename, eargs);
                }
            }
            else
            {
                // Defensive: some transports forward individual events directly.
                DispatchRedrawEvent(method, args);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"notify error: {ex.Message}");
            LogStartup("NOTIFY EXCEPTION in " + method + ": " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);
        }
    }

    private void DispatchRedrawEvent(string name, object?[] a)
    {
        LogStartup($"DISPATCH event={name} args_len={a.Length}");
        switch (name)
        {
            case "grid_resize":
                // Each tuple: [grid_id, width, height] — per api-ui-events.txt the order is cols then rows.
                foreach (var tuple in a)
                {
                    if (tuple is not object?[] t || t.Length < 3) continue;
                    _cols = ToInt(t[1]); _rows = ToInt(t[2]);
                    EnsureScreen(_rows, _cols);
                    ScheduleRender();
                }
                break;
            case "grid_line":
            {
                // Each tuple: [grid_id, row_idx, col_start, cells_array, wrap]. Multiple tuples (one per line) may be sent.
                foreach (var tuple in a)
                {
                    if (tuple is not object?[] t || t.Length < 4) continue;
                    int gridId = ToInt(t[0]);
                    int rowIdx = ToInt(t[1]);
                    int colStart = ToInt(t[2]);
                    var cellArray = t[3] as object?[] ?? Array.Empty<object?>();

                    if (rowIdx < 0 || rowIdx >= _rows) continue;
                    int col = colStart;
                    foreach (var cellRaw in cellArray)
                    {
                        string txt;
                        int hl = -1;
                        int repeatCount = 1;

                        if (cellRaw is string s2)
                            txt = s2;
                        else if (cellRaw is object?[] ce && ce.Length > 0 && ce[0] is string cs)
                        {
                            txt = cs;
                            if (ce.Length > 1 && ToInt(ce[1]) >= 0) hl = ToInt(ce[1]); // highlight ID
                            if (ce.Length > 2) { int rc = ToInt(ce[2]); if (rc > 1) repeatCount = rc; }
                        }
                        else continue;

                        for (int r = 0; r < repeatCount && col < _cols; r++, col++)
                        {
                            if (col < 0) continue;
                            var cell = _cells[rowIdx * _cols + col];
                            cell.Text = Widen(txt);
                            cell.Hl = hl >= 0 ? hl : -1; // only apply valid highlight IDs
                        }
                    }
                    ScheduleRender();
                }
                break;
            }
            case "grid_clear":
                for (int i = 0; i < _cells.Length; i++) { _cells[i].Text = " "; _cells[i].Hl = -1; }
                ScheduleRender();
                break;
            case "cursor_position":
            case "grid_cursor_goto":
                // Each tuple: [grid_id, row, col]
                foreach (var tuple in a)
                {
                    if (tuple is not object?[] t || t.Length < 3) continue;
                    _curRow = ToInt(t[1]); _curCol = ToInt(t[2]);
                    ScheduleRender();
                }
                break;
            case "hl_attr_define":
            {
                // Each tuple: [id, rgb_attr, cterm_attr, info?] — id is a plain int
                foreach (var tuple in a)
                {
                    if (tuple is not object?[] t || t.Length < 3) continue;
                    _hlDefs[ToInt(t[0])] = ParseHl(t[1]);
                    ScheduleRender();
                }
                break;
            }
            case "default_colors_set":
                foreach (var tuple in a)
                {
                    if (tuple is not object?[] t || t.Length < 2) continue;
                    _defFg = HintColor(1, ToInt(t[0])); _defBg = HintColor(2, ToInt(t[1]));
                    ScheduleRender();
                }
                break;
        }
    }
}
