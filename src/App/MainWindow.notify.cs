using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using System.Diagnostics;
using System.IO;
using NvimCore;

namespace NvimWinUIGui;

public partial class MainWindow
{
    private void SetStatus(string s)
    {
        // May be called from the IO thread (notification error path); StatusText is XAML, so
        // marshal to the UI thread. UiPostAsync runs inline when already on the UI thread.
        try { UiPostAsync(() => StatusText.Text = s); } catch { /* XAML already torn down (shutdown path); ignore */ }
    }

    private static string StartupLogPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NvimWinUIGui", "startup.log");

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        LogStartup("OnLoadedAsync ENTER (UI thread alive)");
        SetStatus("spawning nvim...");
        try
        {
            string nvimPath = ResolveNvimPath();
            if (!File.Exists(nvimPath))
            {
                LogCritical($"nvim not found (tried NVIM_WINUI_NVIM, PATH, default install dir); got '{nvimPath}'");
                SetStatus("nvim.exe not found — set NVIM_WINUI_NVIM or add nvim to PATH");
                return;
            }
            int port = FindFreePort();
            // --headless: without it, nvim's console-UI init path hangs startup when all stdio is
            // redirected (no real console) and the --listen socket never opens. Verified empirically 2026-08-24.
            var psi = new ProcessStartInfo(nvimPath, $"--listen 127.0.0.1:{port} --headless")
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
            // When nvim exits (user quit, crash, killed), close the GUI window and terminate.
            // Exited fires on a thread-pool thread — UiPostAsync marshals Close() to the UI thread.
            _nvimProc.EnableRaisingEvents = true;
            _nvimProc.Exited += (s, e) =>
            {
                LogStartup("NVIM-EXIT detected — closing window");
                UiPostAsync(() => { try { Close(); } catch { } });
            };
            _nvimProc.ErrorDataReceived += (s, e) => { if (e.Data != null) LogCritical("NVIM-ERR " + e.Data); };
            _nvimProc.BeginErrorReadLine();
            var client = await ConnectWithRetryAsync(port, 5);
            LogStartup("tcp connect OK");
            _client = client;
            object? info = await _client.CallAsync("nvim_get_api_info");
            LogStartup("api_info returned (" + (info is null ? "null" : info.ToString()!.Length) + " chars)");
            SetStatus($"connected (port {port}), API v{ExtractApiMajor(info)}. attaching ui...");

            // Load guifont/guifontwide from nvim's settings before attaching the UI.
            // Format: "FontName:Style:Size" — we only care about FontName and Size.
            await RefreshGuifontAsync();

            // Some configs set guifont in a plugin that loads lazily, so the value read right
            // after connect can be empty/stale. Re-read ~1s after launch and re-apply if it
            // changed (RefreshGuifontAsync is a no-op when unchanged). Fired fire-and-forget:
            // NvimClient matches responses by request id, so this concurrent call is safe.
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                try { await RefreshGuifontAsync(); }
                catch (Exception ex) { LogCritical("guifont re-read failed: " + ex.Message); }
            });

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
            // ext_multigrid: nvim splits the screen into per-window grids positioned via win_pos
            // (implies ext_linegrid). Grid 1 is the outer frame; window/message grids are routed
            // to their own buffers in DispatchRedrawEvent and drawn on top in RenderCore.
            await _client.NotifyAsync("nvim_ui_attach", _cols, _rows, new Dictionary<string, object?> { ["rgb"] = true, ["ext_linegrid"] = true, ["ext_multigrid"] = true }).ConfigureAwait(false);
            LogStartup("ATTACH-POST ui_attach sent (no response expected; watching for redraw)");
            EnsureScreen(_rows, _cols);
            ScheduleRender();
            FlushRender(); // ui_attach path is not inside HandleNotification — flush here
            SetStatus($"ui attached ({_cols}x{_rows}). typing forwards to nvim.");

            // Self-test (DIAGNOSTICS ONLY): types text into nvim and creates/switches a test
            // buffer, so it pollutes the user's real session. OFF by default — enable with
            // NVIM_WINUI_SELFTEST=1 when verifying the RPC round-trip end to end.
            if (Environment.GetEnvironmentVariable("NVIM_WINUI_SELFTEST") == "1")
            {
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

                    // Self-test part 2: open floating windows over the test buffer and verify
                    // win_float_pos placement + alpha compositing in the composite (nvim 0.12 sends
                    // win_float_pos, not win_pos). Transparency comes from the per-window 'winblend'
                    // option (0..100), which nvim reflects as a `blend` attr in the float's highlights.
                    var fopts = new Dictionary<string, object?> { ["relative"] = "editor", ["width"] = 20, ["height"] = 8, ["row"] = 11, ["col"] = 30 };
                    object? fwin = await _client.CallAsync("nvim_open_win", testBufNum, true, fopts);
                    LogStartup("FLOAT opened (20x8 @ row=11 col=30 relative editor)");
                    if (fwin != null) await _client.CallAsync("nvim_win_set_option", fwin, "winblend", 30);
                    // Second float: winblend=100 -> fully translucent. Its cells carry blend=100,
                    // so every cell reduces to the parent's exact color (parent must show through).
                    var foptsT = new Dictionary<string, object?> { ["relative"] = "editor", ["width"] = 20, ["height"] = 4, ["row"] = 19, ["col"] = 30 };
                    object? fwin2 = await _client.CallAsync("nvim_open_win", testBufNum, true, foptsT);
                    LogStartup("FLOAT-TRANSPARENT opened (20x4 @ row=19 col=30 winblend=100)");
                    if (fwin2 != null) await _client.CallAsync("nvim_win_set_option", fwin2, "winblend", 100);
                    // nvim holds the redraw batch until the next input event; force a flush so the
                    // win_float_pos/grid events arrive while we are not typing.
                    await _client.CallAsync("nvim_command", "redraw!");
                    // Re-arm the one-shot color DIAG so the NEXT frame logs resolved colors in the
                    // post-float state (the bug only appears after a float is opened).
                    _colorDiagLogged = false;
                    await Task.Delay(600).ConfigureAwait(false);
                    var comp = BuildRenderCells();
                    if (comp != null)
                    {
                        // 1) float content must appear inside the expected region rows[11..18] cols[30..49]
                        int fRow = -1, fCol = -1;
                        for (int r = 11; r <= 18 && comp.Length >= _cols * (r + 1); r++)
                            for (int c = 30; c + 5 < _cols; c++)
                            {
                                var t = new System.Text.StringBuilder();
                                for (int k = 0; k < 5; k++) t.Append(comp[r * _cols + c + k].Text);
                                if (t.ToString().Contains("hello")) { fRow = r; fCol = c; break; }
                            }
                        // 2) main window content must still be visible at row 0 (not covered/darkened)
                        var m0 = new System.Text.StringBuilder();
                        for (int k = 0; k < Math.Min(5, _cols); k++) m0.Append(comp[k].Text);
                        LogStartup($"FLOAT-CHECK float 'hello' at row={fRow} col={fCol} EXPECT row in [11..18] col in [30..49]; main row0=[{m0}] EXPECT contains hello");
                        // Dump the composite region around the expected float for visual verification.
                        var sb = new System.Text.StringBuilder();
                        for (int r = 9; r <= 20 && comp.Length >= _cols * (r + 1); r++)
                        {
                            sb.Append($"r{r,3}:|");
                            for (int c = 25; c < 56; c++) sb.Append(comp[r * _cols + c].Text == " " ? "." : comp[r * _cols + c].Text);
                            sb.AppendLine("|");
                        }
                        LogStartup("FLOAT-MAP\n" + sb.ToString());
                        // 3) COMPOSITE-COLOR DIAG: log the RESOLVED bg/fg of composited cells inside and
                        //    outside the float region. A winblend float must resolve to the PARENT's bg
                        //    color mixed at (100-blend)%, not _defBg — that is what makes the parent show
                        //    through. winblend=100 must equal the parent color EXACTLY.
                        var sb2 = new System.Text.StringBuilder();
                        _activeRenderCells = comp; // make CellBg() resolve against THIS composite, not the last frame's
                        int[] probeRows = { 13, 14, 20 }; // 13/14 inside winblend=30 float (rows 11-18), 20 inside winblend=100 float (rows 19-22)
                        foreach (var pr in probeRows)
                            if (comp.Length >= _cols * (pr + 1))
                            {
                                sb2.Append($"r{pr}: ");
                                // parent cell left of the float (col 25), then float cells cols 30..49 step 4
                                int[] probeCols = { 25, 30, 34, 38, 42, 46 };
                                foreach (var pc in probeCols)
                                    if (pc < _cols)
                                    {
                                        var cc = comp[pr * _cols + pc];
                                        Color cbg = CellBg(pr, pc, -1);
                                        Color cfg = ResolveCellFgColor(cc);
                                        sb2.Append($"[{pc}]hl={cc.Hl},bg=0x{PackColor(cbg):X8},fg=0x{PackColor(cfg):X8} '{cc.Text}' ");
                                    }
                                sb2.AppendLine();
                            }
                        LogStartup("COMPOSITE-COLOR\n" + sb2.ToString());
                    }
                }
                catch (Exception ex) { LogStartup("SELFTEST FAILED: " + ex.Message); }
            }
        }
        catch (Exception ex)
        {
            LogCritical($"\nSTARTUP FAILED: {ex}");
            SetStatus($"STARTUP FAILED: {ex.Message}");
        }
    }

    // Read guifont/guifontwide from nvim, parse them into the font fields, and re-render only if
    // they changed. Safe to call repeatedly (startup + ~1s later): when nothing changed it is a
    // cheap no-op that skips the render. Runs on whatever thread calls it; ScheduleRender marshals
    // the actual XAML update onto the UI thread via _uiSyncCtx.
    private async Task RefreshGuifontAsync()
    {
        string? guifont = null, guifontwide = null;
        try
        {
            // guifont/guifontwide are OPTIONS (&guifont), not Vimscript variables — read them with
            // nvim_get_option_value. (nvim_get_value reads g:guifont, which is nil unless the user
            // happens to set a variable of that name.) Empty opts {} = global scope.
            object? gf = await _client!.CallAsync("nvim_get_option_value", "guifont", new Dictionary<string, object?>());
            if (gf is string s) guifont = s;
            object? gfw = await _client.CallAsync("nvim_get_option_value", "guifontwide", new Dictionary<string, object?>());
            if (gfw is string s2) guifontwide = s2;
        }
        catch { /* non-fatal: keep current fonts */ }

        LogStartup($"guifont={guifont ?? ""} guifontwide={guifontwide ?? ""}");

        string newNarrow, newWide; double newNSize, newWSize;
        ParseNvimFont(guifont, NarrowFallback, out newNarrow, out newNSize);
        if (!string.IsNullOrEmpty(guifontwide))
            ParseNvimFont(guifontwide, WideFallback, out newWide, out newWSize);
        else { newWide = newNarrow; newWSize = newNSize; }

        // Apply + re-render only on change so the 1s re-read is a no-op when fonts are stable.
        if (newNarrow == _narrowFont && newNSize == _narrowSize &&
            newWide == _wideFont && newWSize == _wideSize) return;

        LogStartup($"guifont APPLIED: narrow={newNarrow}@{newNSize} wide={newWide}@{newWSize}");
        _narrowFont = newNarrow; _narrowSize = newNSize;
        _wideFont = newWide; _wideSize = newWSize;
        // Derive the reference cell size from the real font metrics so window<->grid conversions
        // track the guifont (UI thread: MeasureRefCell creates a XAML TextBlock).
        UiPostAsync(MeasureRefCell);
        ScheduleRender();
        FlushRender(); // runs on the UI thread (async continuation) — not inside HandleNotification
    }

    // Diagnostic logging (file-based). OFF by default — set NVIM_WINUI_DIAG=1 to enable. The hot
    // path logs every redraw event / resize, so leaving it on spams startup.log and adds file IO
    // per frame. LogCritical() is the always-on exception: rare fatal errors are recorded even
    // when diagnostics are off, so a crash stays diagnosable without the env var.
    private static readonly bool _diagEnabled = Environment.GetEnvironmentVariable("NVIM_WINUI_DIAG") == "1";

    private static void LogStartup(string s)
    {
        if (!_diagEnabled) return; // off by default (see _diagEnabled above)
        try
        {
            var dir = System.IO.Path.GetDirectoryName(StartupLogPath)!;
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(StartupLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {s}{System.Environment.NewLine}");
        }
        catch { /* best effort */ }
    }

    private static void LogCritical(string s)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(StartupLogPath)!;
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(StartupLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] CRITICAL {s}{System.Environment.NewLine}");
        }
        catch { /* best effort */ }
    }

    private void OnNvimNotification(string method, object?[]? args)
    {
        // Notifications arrive on the IO thread; XAML must only be touched from the UI thread.
        // State mutation (_cells) is done HERE too (on the UI thread) — moving it to the IO thread
        // caused a render-queue wedge that blanked the screen, so we keep the proven single-thread
        // model: every notification posts one HandleNotification onto the UI thread.
        var ctx = _uiSyncCtx;
        if (ctx is not null && !ReferenceEquals(ctx, System.Threading.SynchronizationContext.Current))
        {
            try { ctx.Post(_ => HandleNotification(method, args), null); return; } catch { /* fall through */ }
        }
        HandleNotification(method, args);
    }

    private int _hnTrace;
    private int _hbCount;
    private double _handleMsTotal; private int _handleCount;
    private void HandleNotification(string method, object?[]? args)
    {
        if (args is null) return;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
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
            LogCritical("NOTIFY EXCEPTION in " + method + ": " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);
        }
        finally
        {
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _handleMsTotal += ms; int hc = Interlocked.Increment(ref _handleCount);
            if (_diagEnabled && (hc % 25 == 0 || ms > 8)) LogStartup($"HANDLE #{hc} {ms:F1}ms avg={_handleMsTotal/hc:F1}ms events={(args?.Length ?? 0)}");
            // One render per notification batch: ScheduleRender only marked dirty during the loop.
            FlushRender();
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
                    int gId = ToInt(t[0]);
                    if (gId != 1)
                        MGridResize(gId, ToInt(t[1]), ToInt(t[2])); // per-window/message grid buffer
                    else
                    { _cols = ToInt(t[1]); _rows = ToInt(t[2]); EnsureScreen(_rows, _cols); }
                    ScheduleRender();
                }
                break;
            case "grid_line":
            {
                // Each tuple: [grid_id, row_idx, col_start, cells_array, wrap]. Multiple tuples (one per line) may be sent.
                int lastHl = -1; // per grid_line event: a cell with no hl_id inherits the most recently seen one
                foreach (var tuple in a)
                {
                    if (tuple is not object?[] t || t.Length < 4) continue;
                    int gridId = ToInt(t[0]);
                    int rowIdx = ToInt(t[1]);
                    int colStart = ToInt(t[2]);
                    var cellArray = t[3] as object?[] ?? Array.Empty<object?>();

                    if (gridId != 1)
                    {
                        MGridLine(gridId, rowIdx, colStart, cellArray); // per-window/message grid buffer
                        ScheduleRender();
                        continue;
                    }

                    if (rowIdx < 0 || rowIdx >= _rows) continue;
                    int col = colStart;
                    if (_diagEnabled)
                    {
                        var sbr = new System.Text.StringBuilder();
                        foreach (var cr in cellArray)
                        {
                            if (cr is string ss) sbr.Append($"'{ss}' ");
                            else if (cr is object?[] ce2)
                                sbr.Append("[" + string.Join(",", ce2.Select(x => x?.ToString() ?? "null")) + "] ");
                            else sbr.Append($"[{System.Convert.ToString(cr)}] ");
                        }
                        LogStartup($"GRIDLINE-RAW row={rowIdx} colstart={colStart} cells=[{sbr}]");
                    }
                    foreach (var cellRaw in cellArray)
                    {
                        string txt;
                        int hl = lastHl; // inherit most-recently-seen hl_id when this cell omits it
                        int repeatCount = 1;

                        if (cellRaw is string s2)
                            txt = s2;
                        else if (cellRaw is object?[] ce && ce.Length > 0 && ce[0] is string cs)
                        {
                            txt = cs;
                            if (ce.Length > 1 && ToInt(ce[1]) >= 0) { hl = ToInt(ce[1]); lastHl = hl; } // highlight ID
                            if (ce.Length > 2) { int rc = ToInt(ce[2]); if (rc > 1) repeatCount = rc; }
                        }
                        else continue;

                        // Place character-by-character so wide glyphs (CJK/emoji, display width 2)
                        // advance the column by 2 and their tail cell is marked covered-blank. The
                        // old code advanced col by 1 per entry, which desynced every column after a
                        // wide char from nvim's grid -> misalignment + tofu in the tail cells.
                        for (int r = 0; r < repeatCount && col < _cols; r++)
                        {
                            int p = 0;
                            while (p < txt.Length)
                            {
                                string g;
                                if (char.IsHighSurrogate(txt[p]) && p + 1 < txt.Length && char.IsLowSurrogate(txt[p + 1]))
                                    { g = txt.Substring(p, 2); p += 2; }   // full surrogate pair (emoji/astral)
                                else
                                    { g = txt[p].ToString(); p += 1; }    // BMP code point
                                if (col >= _cols) break;
                                var head = _cells[rowIdx * _cols + col];
                                head.Text = g;                            // the glyph (never empty here)
                                head.Hl = hl >= 0 ? hl : -1;              // only apply valid highlight IDs
                                int w = IsWideGlyph(g) ? 2 : 1;           // display width in cells
                                col++;
                                for (int k = 1; k < w && col < _cols; k++)
                                {
                                    var tail = _cells[rowIdx * _cols + col];
                                    tail.Text = "";                       // covered by the wide glyph -> render blank, no tofu
                                    tail.Hl = hl >= 0 ? hl : -1;
                                    col++;
                                }
                            }
                        }
                    }
                    ScheduleRender();
                }
                break;
            }
            case "grid_clear":
                // linegrid: no args (clear outer frame). multigrid: [grid_id] clears one window/message grid.
                if (a.Length >= 1 && ToInt(a[0]) != 1)
                    MGridClear(ToInt(a[0]));
                else
                    for (int i = 0; i < _cells.Length; i++) { _cells[i].Text = " "; _cells[i].Hl = -1; }
                ScheduleRender();
                break;
            case "grid_scroll":
            {
                // Each tuple: [grid_id, top_row, bot_row, left_col, right_col, rows, cols].
                // Shift content in region [top,bot) x [left,right) by `rows` lines (positive = up).
                // nvim sends this when the screen scrolls instead of resending every grid_line.
                foreach (var tuple in a)
                {
                    if (tuple is not object?[] t || t.Length < 7) continue;
                    int sGridId = ToInt(t[0]);
                    int top   = ToInt(t[1]);
                    int bot   = ToInt(t[2]);
                    int left  = ToInt(t[3]);
                    int right = ToInt(t[4]);
                    int rows  = ToInt(t[5]); // signed: + up, - down
                    if (rows == 0) continue;

                    if (sGridId != 1)
                    { MGridScroll(sGridId, top, bot, left, right, rows); ScheduleRender(); continue; }

                    top   = Math.Max(0, Math.Min(top, _rows));
                    bot   = Math.Max(0, Math.Min(bot, _rows));
                    left  = Math.Max(0, Math.Min(left, _cols));
                    right = Math.Max(0, Math.Min(right, _cols));
                    if (top >= bot || left >= right) continue;

                    int regionW = right - left;
                    // Snapshot the region's VALUES (Text/Hl) so src is independent of _cells and
                    // no two destination positions ever alias one Cell object.
                    var srcTxt = new string[(bot - top) * regionW];
                    var srcHl  = new int[(bot - top) * regionW];
                    for (int r = top; r < bot; r++)
                        for (int c = left; c < right; c++)
                        {
                            var cc = _cells[r * _cols + c];
                            srcTxt[(r - top) * regionW + (c - left)] = cc.Text;
                            srcHl[(r - top) * regionW + (c - left)]  = cc.Hl;
                        }

                    // Write back shifted: dest row dr takes source row sr = dr + rows.
                    for (int dr = top; dr < bot; dr++)
                    {
                        int sr = dr + rows;
                        for (int c = left; c < right; c++)
                        {
                            var cell = new Cell();
                            if (sr >= top && sr < bot)
                            {
                                int si = (sr - top) * regionW + (c - left);
                                cell.Text = srcTxt[si];
                                cell.Hl   = srcHl[si];
                            } // else stays blank (" ", Hl=-1) where content scrolled out
                            _cells[dr * _cols + c] = cell;
                        }
                    }
                    ScheduleRender();
                }
                break;
            }
            case "cursor_position":
            case "grid_cursor_goto":
                // Each tuple: [grid_id, row, col] — store grid + local coords; resolve to outer-frame at render.
                foreach (var tuple in a)
                {
                    if (tuple is not object?[] t || t.Length < 3) continue;
                    _curGridId = ToInt(t[0]);
                    _curLocalRow = ToInt(t[1]); _curLocalCol = ToInt(t[2]);
                    ScheduleRender();
                }
                break;
            case "hl_attr_define":
            {
                // Each tuple: [id, rgb_attr, cterm_attr, info?] — id is a plain int.
                // A (re)definition batch may change colors our synthetic blends captured -> drop them.
                InvalidateBlendCache();
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
                    InvalidateBlendCache(); // base color for blends changed
                    LogStartup($"COLORS fg=0x{PackColor(_defFg):X8} bg=0x{PackColor(_defBg):X8}");
                    var bgBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(_defBg);
                    _root.Background = bgBrush;
                    // Keep the canvas's opaque XAML background in sync so any swap-chain gap after a
                    // resize (floating window) shows the theme color, not the black window base.
                    GlyphCanvas.Background = bgBrush;
                    ScheduleRender();
                }
                break;
            case "win_pos":
                // [grid_id, win_handle, start_row, start_col, width, height] — place a window grid.
                foreach (var tuple in a)
                {
                    if (tuple is not object?[] t || t.Length < 6) continue;
                    MWinPos(ToInt(t[0]), ToInt(t[1]), ToInt(t[2]), ToInt(t[3]), ToInt(t[4]), ToInt(t[5]));
                    ScheduleRender();
                }
                break;
            case "msg_set_pos":
                // [grid_id, row, scrolled, sep_char, zindex, compindex] — place the message grid.
                foreach (var tuple in a)
                {
                    if (tuple is not object?[] t || t.Length < 2) continue;
                    MMsgSetPos(ToInt(t[0]), ToInt(t[1]), t.Length > 4 ? ToInt(t[4]) : 0);
                    ScheduleRender();
                }
                break;
            case "win_float_pos":
                // [grid_id, win_handle, anchor, anchor_grid, anchor_row, anchor_col, mouse_enabled, zindex, compindex, screen_row, screen_col]
                foreach (var tuple in a)
                {
                    if (tuple is not object?[] t || t.Length < 11) continue;
                    MWinFloatPos(ToInt(t[0]), ToInt(t[1]), t[2], ToInt(t[3]),
                        ToDouble(t[4]), ToDouble(t[5]), ToBool(t[6]), ToInt(t[7]), ToInt(t[8]),
                        ToDouble(t[9]), ToDouble(t[10]));
                    ScheduleRender();
                }
                break;
            case "win_hide":
                foreach (var tuple in a) { if (tuple is not object?[] t || t.Length < 1) continue; MWinHide(ToInt(t[0])); ScheduleRender(); }
                break;
            case "win_close":
                foreach (var tuple in a) { if (tuple is not object?[] t || t.Length < 1) continue; MWinClose(ToInt(t[0])); ScheduleRender(); }
                break;
        }
    }
}