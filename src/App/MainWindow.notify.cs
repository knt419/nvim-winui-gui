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
            await _client.NotifyAsync("nvim_ui_attach", _cols, _rows, new Dictionary<string, object?> { ["rgb"] = true, ["ext_linegrid"] = true }).ConfigureAwait(false);
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
                    _cols = ToInt(t[1]); _rows = ToInt(t[2]);
                    EnsureScreen(_rows, _cols);
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

                    if (rowIdx < 0 || rowIdx >= _rows) continue;
                    int col = colStart;
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
                    int top   = ToInt(t[1]);
                    int bot   = ToInt(t[2]);
                    int left  = ToInt(t[3]);
                    int right = ToInt(t[4]);
                    int rows  = ToInt(t[5]); // signed: + up, - down
                    if (rows == 0) continue;

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
                // Each tuple: [grid_id, row, col]
                foreach (var tuple in a)
                {
                    if (tuple is not object?[] t || t.Length < 3) continue;
                    int cGridId = ToInt(t[0]);
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