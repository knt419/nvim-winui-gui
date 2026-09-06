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

    // One-shot diagnostics: dump the real grid_line arg layout from the live code path.
    private int _gridlineDiagCount;
    private static string DiagVal(object? v)
    {
        if (v is null) return "null";
        if (v is string s) return "\"" + (s.Length > 12 ? s.Substring(0, 12) + "…" : s) + "\"";
        if (v is object?[] a)
            return "[" + a.Length + "]" + (a.Length > 0 && a[0] != null ? a[0].GetType().Name + "[]" : "");
        string t = v.GetType().Name;
        string s2 = v.ToString() ?? "?";
        if (s2.Length > 14) s2 = s2.Substring(0, 14) + "…";
        return t + ":" + s2;
    }

    // Round-8 diagnostics: event-order trace (first 60 redraw events), every grid_resize in full,
    // and the RAW shape of grid_line cells so we can see exactly what nvim sends vs how we read it.
    private int _evTraceCount;
    private string[]? _lastCellsDumped = null;
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
            LogStartup("ATTACH-PRE sending ui_attach");
            await _client.CallAsync("nvim_ui_attach", _cols, _rows, new Dictionary<string, object?> { ["rgb"] = true, ["ext_linegrid"] = true });
            LogStartup("ATTACH-POST ui_attach response received");
            EnsureScreen(_rows, _cols);
            ScheduleRender();
            SetStatus($"ui attached ({_cols}x{_rows}). typing forwards to nvim.");

            // CRITICAL: avoid Ex-mode commands that can error (:w on an unnamed buffer = E32) —
            // such errors make nvim block at the hit-enter prompt and STOP processing RPC, so every
            // later CallAsync would hang forever. Plain typing goes through insert mode only.
            await _client.CallAsync("nvim_input", "ihello from nvim-winui-gui");
            LogStartup("POST-INPUT input response received");

            // Self-test: wait for redraw notifications to drain, then read back the buffer line
            // and count populated cells -> objective proof that (a) key input reached nvim
            // and executed, and (b) grid_line notifications decoded into the cell model.
            LogStartup("STEP pre-delay");
            await Task.Delay(1000).ConfigureAwait(false);
            LogStartup("STEP post-delay, calling get_current_buf");
            try
            {
                // ConfigureAwait(false): the self-test RPC continuations must NOT queue behind
                // redraw-notification floods on the UI dispatcher — verified 2026-09-06 that a
                // response arriving at :55.1 was only resumed ~20s later while the dispatcher chewed
                // through posted HandleNotification work. Cell model reads are plain fields, safe off-thread.
                object? buf = await _client.CallAsync("nvim_get_current_buf").ConfigureAwait(false);
                    LogStartup($"STEP got buf={buf} ({(buf is MsgpackStreamDecoder.MsgpackExt ? "MsgpackExt" : "plain")})");

                    // nvim RPC request params must be standard types (int, string, bool, array, object).
                    // Typed API handles come back as MsgpackExt (fixext) in responses — passing them
                    // back into another request makes nvim hang. Extract the integer value first.
                    int bufNum;
                    if (buf is MsgpackStreamDecoder.MsgpackExt extHandle)
                    {
                        string dataHex = BitConverter.ToString(extHandle.Data).Replace("-", "").ToLowerInvariant();
                        LogStartup($"FIXEXT TypeId={extHandle.TypeId} DataLen={extHandle.Data.Length} DataHex={dataHex}");
                        switch (extHandle.Data.Length)
                        {
                            case 1: bufNum = extHandle.Data[0]; break;
                            case 2: bufNum = (int)(extHandle.Data[0] << 8 | extHandle.Data[1]); break;
                            case 4: bufNum = (int)System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(extHandle.Data.AsSpan()); break;
                            case 8: bufNum = (int)System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(extHandle.Data.AsSpan()); break;
                            default: throw new InvalidOperationException($"Unexpected buffer handle data length: {extHandle.Data.Length}");
                        }
                    }
                    else
                        bufNum = (int)buf!;

                    var ext2 = buf as MsgpackStreamDecoder.MsgpackExt;
                    LogStartup($"BUFNUM extracted={bufNum} data_len={(ext2 != null ? ext2.Data.Length : -1)}");
                    var lines = await _client.CallAsync("nvim_buf_get_lines", bufNum, 0, 1, true);
                LogStartup("STEP got lines");
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
                    if (ev is not object?[] ea || ea.Length < 1 || ea[0] is not string ename) continue;
                    var eargs = new object?[ea.Length - 1];
                    Array.Copy(ea, 1, eargs, 0, ea.Length - 1);
                    DispatchRedrawEvent(ename, eargs);
                }
                return;
            }

            // Defensive: some transports forward individual events directly.
            DispatchRedrawEvent(method, args);
        }
        catch (Exception ex)
        {
            SetStatus($"notify error: {ex.Message}");
        }
    }

    private void DispatchRedrawEvent(string name, object?[] a)
    {
        switch (name)
        {
            case "grid_resize":
                // [grid, rows, cols]
                if (a.Length >= 3)
                {
                    _rows = ToInt(a[1]); _cols = ToInt(a[2]);
                    EnsureScreen(_rows, _cols);
                    ScheduleRender();
                }
                break;
            case "grid_line":
            {
                // LIVE WIRESHAPE (frame_7.json, 2026-08-28): a is an ARRAY OF ROW OBJECTS.
                // Each row object: [grid_id:int, row_idx:int, col_start:int, cells_array:[], wrap_bool:bool]
                // Multiple rows are batched in one grid_line notification.
                foreach (var rowObjRaw in a)
                {
                    if (rowObjRaw is not object?[] rowObj || rowObj.Length < 5) continue;
                    
                    int gridId = ToInt(rowObj[0]);
                    int rowIdx = ToInt(rowObj[1]);
                    int colStart = ToInt(rowObj[2]);
                    var cellArray = rowObj[3] as object?[] ?? Array.Empty<object?>();

                    // Process each cell in this row. Per api-ui-events.txt, a cell is either
                    // "text" or ["text", hl_id?, repeat_count?]. The repeat count means the
                    // entry occupies N consecutive columns; following entries continue AFTER
                    // those, so track `col` incrementally instead of colStart+k indexing.
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
                // [grid, row, col]
                if (a.Length >= 3) { _curRow = ToInt(a[1]); _curCol = ToInt(a[2]); ScheduleRender(); }
                break;
            case "hl_attr_define":
                // [id, rgb_attr, cterm_attr, info?] — id is a plain int; ONE definition per event
                // (nvim 0.12 api-ui-events.txt). There is no batched "highlight_define" event.
                if (a.Length >= 3)
                    _hlDefs[ToInt(a[0])] = ParseHl(a[1]);
                ScheduleRender();
                break;
            case "default_colors_set":
                if (a.Length >= 2) { _defFg = HintColor(1, ToInt(a[0])); _defBg = HintColor(2, ToInt(a[1])); }
                ScheduleRender();
                break;
        }
    }
}
