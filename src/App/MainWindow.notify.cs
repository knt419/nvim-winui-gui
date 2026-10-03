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
         // Hidden by default (NVIM_WINUI_STATUSBAR=1 to show).
         try { UiPostAsync(() => { StatusText.Text = s; StatusText.Visibility = StatusBarVisible ? Visibility.Visible : Visibility.Collapsed; }); } catch { /* XAML already torn down (shutdown path); ignore */ }
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
            // --embed (implies --headless): the documented GUI-backend mode, same recipe as
            // Neovide (piped stdio + CREATE_NO_WINDOW). Verified empirically 2026-09-27
            // (nvim 0.12.5, Win11 25H2): with plain --headless, :terminal children silently
            // miss the ConPTY console (nushell REPL exits at once with "STDIN is not a TTY",
            // blank screen, dead input); with --embed the terminal works end to end (shell
            // lives, prompt/echo/output render, input executes). Our RPC stays on the TCP
            // --listen socket; the embed stdio channel simply idles (stdin pipe held open,
            // never written; stdout never read because nvim only writes when spoken to).
            string extraArgs = Environment.GetEnvironmentVariable("NVIM_WINUI_ARGS") ?? "";
            var psi = new ProcessStartInfo(nvimPath, $"--embed --listen 127.0.0.1:{port} {extraArgs}")
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
            // Self-identify so nvim_get_chan_info().client names this UI — useful in :checkhealth,
            // in channel dumps, and for plugins that branch on the frontend. Five args (verified
            // against 0.12.5): name, version, type, methods, attributes. type must be one of
            // embedder|host|msgpack-rpc|plugin|remote|ui; "ui" is correct for a GUI frontend.
            // Best-effort: a failure here must not block attaching the UI.
            try
            {
                await _client.CallAsync("nvim_set_client_info", "nvim-winui-gui",
                    new Dictionary<string, object?> { ["major"] = 0, ["minor"] = 1, ["patch"] = 0, ["prerelease"] = "dev" },
                    "ui",
                    new Dictionary<string, object?> { ["ext_linegrid"] = true, ["ext_multigrid"] = true, ["rgb"] = true },
                    new Dictionary<string, object?> { ["platform"] = "win32", ["info"] = "WinUI 3 + Direct2D" });
                LogStartup("CLIENT-INFO announced (name=nvim-winui-gui type=ui)");
            }
            catch (Exception ex) { LogCritical("nvim_set_client_info failed: " + ex.Message); }
            // ext_linegrid: switch nvim to line-based grid events (grid_resize/grid_clear/
            // grid_line/grid_scroll/grid_cursor_goto/hl_attr_define). Without it nvim emits only the
            // legacy terminal protocol (put/cursor_goto/move_cursor), which this handler does not
            // consume -> empty screen. Verified against runtime/doc/api-ui-events.txt (0.12).
            LogStartup("ATTACH-PRE sending ui_attach (notification)");
            // nvim_ui_attach is sent as a NOTIFICATION ([2, method, params]) and we deliberately do
            // NOT wait for a reply: the UI is confirmed attached by the redraw traffic that follows.
            // (Corrected 2026-09-29: the earlier comment here claimed ui_attach "does not send a
            // response, so CallAsync would hang forever". That was wrong — nvim 0.12.5 declares NO
            // function with return type void in --api-info, and ui_attach sent as a REQUEST [0,id,..]
            // returns error=null/result=null and attaches identically (verified: grid_line=17,
            // grid_resize=10, mode_info_set=3 either way). Notification is kept because it avoids
            // blocking startup on a round-trip, but CallAsync would work if you prefer the sync path.)
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

                    // nvim_create_buf replies with an EXT handle; decode it to an int buffer id.
                    static int BufHandle(object? o) => o switch
                    {
                        MsgpackStreamDecoder.MsgpackExt ext => ext.Data.Length switch
                        {
                            1 => ext.Data[0],
                            2 => (int)(ext.Data[0] << 8 | ext.Data[1]),
                            4 => (int)System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(ext.Data.AsSpan()),
                            8 => (int)System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(ext.Data.AsSpan()),
                            _ => throw new InvalidOperationException("Unexpected buffer handle data length")
                        },
                        _ => (int)o!
                    };

                    LogStartup($"STEP got test buffer #{testBufNum}");
                    // Switch to the test buffer so typing goes there
                    await _client.CallAsync("nvim_set_current_buf", testBufNum).ConfigureAwait(false);
                    LogStartup($"STEP switched to test buffer, current={await _client.CallAsync("nvim_get_current_buf").ConfigureAwait(false)}");
                    // Type into it (using set_lines for deterministic self-test; nvim_input can be mode-dependent)
                    // nvim_buf_set_lines(bufid, startline, endline, replace:bool, lines:[string]) — order matters.
                    object?[] putArgs = { testBufNum, 0, 1, true, new string[] { "hello from nvim-winui-gui", "skew check: --> ★ ◆ ✔ ▶ ─ long tail to prove alignment" } };
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
                    // Floats get their OWN buffer with mostly blank lines so most cells are empty and
                    // the COMPOSITE must preserve + DIM the parent glyph underneath -> layer separation.
                    int fbuf1 = BufHandle(await _client.CallAsync("nvim_create_buf", true, false));
                    await _client.CallAsync("nvim_buf_set_lines", fbuf1, 0, -1, false, new[] { "FLOAT-A", "", "", "", "", "", "", "" });
                    object? fwin = await _client.CallAsync("nvim_open_win", fbuf1, true, fopts);
                    LogStartup("FLOAT opened (20x8 FLOAT-A @ row=11 col=30 relative editor)");
                    if (fwin != null) await _client.CallAsync("nvim_win_set_option", fwin, "winblend", 30);
                    // Second float: winblend=100 -> fully translucent. Its cells carry blend=100,
                    // so every cell reduces to the parent's exact color (parent must show through).
                    var foptsT = new Dictionary<string, object?> { ["relative"] = "editor", ["width"] = 20, ["height"] = 4, ["row"] = 19, ["col"] = 30 };
                    int fbuf2 = BufHandle(await _client.CallAsync("nvim_create_buf", true, false));
                    await _client.CallAsync("nvim_buf_set_lines", fbuf2, 0, -1, false, new[] { "FLOAT-B", "", "", "" });
                    object? fwin2 = await _client.CallAsync("nvim_open_win", fbuf2, true, foptsT);
                    LogStartup("FLOAT-TRANSPARENT opened (20x4 FLOAT-B @ row=19 col=30 winblend=100)");
                    if (fwin2 != null) await _client.CallAsync("nvim_win_set_option", fwin2, "winblend", 100);
                    // nvim holds the redraw batch until the next input event; force a flush so the
                    // win_float_pos/grid events arrive while we are not typing.
                    await _client.CallAsync("nvim_command", "redraw!");
                    await Task.Delay(600).ConfigureAwait(false);
                    var comp = BuildRenderCells();
                    if (comp != null)
                    {
                        // 1) float content must appear inside the expected regions: float1 rows[11..18], cols[30..49].
                        //    float2 (winblend=100) is EXPECTED invisible-by-design: its own text is dimmed
                        //    to the parent color, so only the preserved parent glyphs are readable there.
                        var (fRow1, fCol1) = FindText(comp, _cols, "FLOAT-A", 11, 18, 30);
                        var (fRow2, fCol2) = FindText(comp, _cols, "FLOAT-B", 19, 22, 30);
                        // 2) main window content (grid2 starts at frame row 1 in multigrid) must still be
                        //    visible, not covered/darkened by the floats
                        var m0 = new System.Text.StringBuilder();
                        for (int k = 0; k < Math.Min(5, _cols); k++) m0.Append(comp[1 * _cols + k].Text);
                        LogStartup($"FLOAT-CHECK float1 'FLOAT-A' at row={fRow1} col={fCol1} EXPECT row in [11..18] col in [30..49]; " +
                                   $"float2 'FLOAT-B' text visible={fRow2 != -1} (winblend=100 -> invisible is CORRECT); main row1=[{m0}]");
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
                        // 4) DIM-CHECK: where a float cell is blank the compositor must keep the parent
                        //    glyph but drawn FAINTER than the float's own text, scaled by the float's
                        //    blend (keepPct=blend) so the layers read separately. Scan the winblend=30
                        //    float region (rows 11-18 cols 30-49) for any blank float cell that preserved
                        //    a NON-BLANK parent glyph, and verify its fg is dimmed between parent colors.
                        var dimOk = false;
                        if (comp.Length >= _cols * 19)
                        {
                            Color parentBg = CellBg(11, 25, -1);
                            Color parentFg = ResolveCellFgColor(comp[11 * _cols + 25]);
                            // A preserved parent glyph is dimmed BELOW the midpoint of the parent colors
                            // (keepPct=30 of defFg over parentBg ~= 0xFF3338..); the float's own text stays
                            // above it (~70%). This rejects the "FLOAT-A" labels as a false positive.
                            Color mid = Color.FromArgb(0xFF,
                                (byte)((parentFg.R + parentBg.R) / 2), (byte)((parentFg.G + parentBg.G) / 2), (byte)((parentFg.B + parentBg.B) / 2));
                            for (int r = 11; r <= 18 && !dimOk; r++)
                                for (int c = 30; c <= 49 && !dimOk; c++)
                                {
                                    var dcell = comp[r * _cols + c];
                                    if (dcell.Text.Length == 0 || dcell.Text == " ") continue; // blank float cell only
                                    var cbg = CellBg(r, c, -1);
                                    if (cbg == parentBg) continue; // outside the blend float (parent bg untouched)
                                    var dfg = ResolveCellFgColor(dcell);
                                    dimOk = PackColor(dfg) > PackColor(cbg) && PackColor(dfg) < PackColor(mid);
                                    if (dimOk)
                                        LogStartup($"DIM-CHECK ({r},{c}) '{dcell.Text}' fg=0x{PackColor(dfg):X8} bg=0x{PackColor(cbg):X8} " +
                                                   $"parentFg=0x{PackColor(parentFg):X8} mid=0x{PackColor(mid):X8} -> PASS");
                                }
                            if (!dimOk) LogStartup("DIM-CHECK no preserved+dimmed parent glyph found in float1 region");
                        }
                        LogStartup("SELFTEST FLOAT RESULT: " + (dimOk ? "PASS" : "FAIL"));
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

    // Self-test helper: scan the composited cells for `needle` on a single row, within col>=minCol,
    // rows in [rowLo..rowHi]. Returns (row, col) of the first hit or (-1, -1).
    private static (int, int) FindText(Cell[] comp, int cols, string needle, int rowLo, int rowHi, int minCol)
    {
        for (int r = rowLo; r <= rowHi && comp.Length >= cols * (r + 1); r++)
            for (int c = minCol; c + needle.Length - 1 < cols; c++)
            {
                var t = new System.Text.StringBuilder();
                for (int k = 0; k < needle.Length; k++) t.Append(comp[r * cols + c + k].Text);
                if (t.ToString().Contains(needle)) return (r, c);
            }
        return (-1, -1);
    }

    // Read guifont/guifontwide from nvim, parse them into the font fields, and re-render only if
    // they changed. Safe to call repeatedly (startup + ~1s later): when nothing changed it is a
    // cheap no-op that skips the render. Runs on whatever thread calls it; ScheduleRender marshals
    // the actual XAML update onto the UI thread via _uiSyncCtx.
    private async Task RefreshGuifontAsync()
    {
        try
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
        }
        catch (Exception ex) { LogCritical("RefreshGuifont failed: " + ex); }
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

    // Always-on, rare one-shots (no CRITICAL prefix): grid lifecycle events (COLORS / win_* placement).
    // Unlike LogStartup this does NOT require NVIM_WINUI_DIAG=1, so a normal user session still
    // leaves enough state to diagnose placement reports. Frequency stays tiny (one line per
    // win_pos/msg_set_pos), so IO is fine.
    private static void LogImportant(string s)
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
        _sawFlush = false;
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
                // Defensive: some transports forward individual events directly. A notification that
                // is NOT a "redraw" batch has no flush marker of its own, so it must not be gated —
                // it IS the final state, not an intermediate one.
                DispatchRedrawEvent(method, args);
                _sawFlush = true;
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
            // One render per batch, gated on nvim's `flush` marker.
            //
            // api-ui-events.txt: multiple "redraw" batches may be sent before the whole screen is
            // redrawn, with "flush" only at the end of the LAST one; the user should only ever see
            // the final, consistent state. We honour that: a batch that did not end with flush
            // marks the render dirty but does NOT paint. Never painting is safe (the next flush
            // repaints), whereas painting early shows a half-updated screen.
            //
            // Watchdog: if a flush-gated render never follows, the screen would stay stale forever,
            // so _flushWatchdogMs later forces a paint. Blanked screens from a wedged render path
            // have bitten this app before (see the BUG HISTORY in NvimClient), hence the backstop.
            // (FlushRender itself stays unconditional: it is also called off the notification path,
            // e.g. the ui_attach sequence and SizeChanged, where there is no flush to wait for.)
            if (_sawFlush) { _flushWatchdogDue = 0; FlushRender(); }
            else { ArmFlushWatchdog(); }
        }
    }

    // ---- flush-gated rendering ---------------------------------------------------------------
    // Per api-ui-events.txt only the state at `flush` is meant to be shown, so HandleNotification
    // paints on flush rather than after every batch. Two safety nets keep that from ever going
    // stale: non-redraw notifications (any notification that is not "redraw" arrives via the
    // defensive DispatchRedrawEvent path) and a timer if a flush never comes.
    private bool _sawFlush;
    private long _flushWatchdogDue;   // Stopwatch ticks; 0 = not armed
    private const double _flushWatchdogMs = 250;

    /// <summary>Force a paint if the flush-gated render never arrived. UI thread.</summary>
    private void ArmFlushWatchdog()
    {
        if (_flushWatchdogDue != 0) return; // already armed; the first due wins
        long freq = System.Diagnostics.Stopwatch.Frequency;
        _flushWatchdogDue = System.Diagnostics.Stopwatch.GetTimestamp()
                            + (long)(_flushWatchdogMs / 1000.0 * freq);
        // ONE timer for the app's lifetime, re-armed with Change() — allocating a fresh Timer per
        // flush cycle would leak one handle per redraw. The callback runs on the thread pool and
        // hops to the UI thread, because it touches _renderQueued and calls RenderNow.
        var t = _flushWatchdogTimer;
        if (t == null)
        {
            t = new System.Threading.Timer(_ => UiPostAsync(FlushWatchdogTick), null,
                System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            _flushWatchdogTimer = t;
        }
        try { t.Change((int)_flushWatchdogMs, System.Threading.Timeout.Infinite); }
        catch (ObjectDisposedException) { /* window closed */ }
    }

    private System.Threading.Timer? _flushWatchdogTimer;

    private void FlushWatchdogTick()
    {
        long due = Interlocked.Exchange(ref _flushWatchdogDue, 0);
        if (due == 0) return;                 // a flush landed first; nothing to rescue
        if (System.Diagnostics.Stopwatch.GetTimestamp() < due) return;
        LogImportant("FLUSH-WATCHDOG fired — no flush seen, painting anyway");
        FlushRender();
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
                    bool absorbNext = false; // fold the next selector-only entry into the preceding emoji's span
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

                        // nvim splits VS16/ZWJ off a grapheme into its own grid cell (⚠️ -> ['⚠','\uFE0F']).
                        // The emoji head already reserves EmojiCells cells, so fold the selector in
                        // instead of letting it claim a column of its own — that would push every
                        // later glyph one cell right.
                        if (absorbNext && IsSelectorOnly(txt)) continue;
                        absorbNext = false;

                        // Place character-by-character so wide glyphs advance the column by their display width
                        // and their tail cells are marked covered-blank: emoji-presentation glyphs get a uniform
                        // EmojiCells allocation, CJK/wide 2. The old code advanced col by 1 per entry, which desynced
                        // every column after a wide char from nvim's grid -> misalignment + tofu in the tail cells.
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
                                // A VS16/ZWJ code point riding ALONE inside a cell entry (nvim packs
                                // "⚠️" = U+26A0 U+FE0F into one cell) is not a glyph: it must claim no
                                // column, or it pushes every later cell one column right. IsEmojiPresentation
                                // returns TRUE for it (it tests for FE0F), so the wide width would be applied
                                // here and the line would be 2 cells wider than the same line using "✅".
                                if (IsSelectorOnly(g)) continue;
                                var head = _cells[rowIdx * _cols + col];
                                head.Text = g;                            // the glyph (never empty here)
                                head.Hl = hl >= 0 ? hl : -1;              // only apply valid highlight IDs
                                int w = AppGlyphWidth(g);                 // display width in cells (emoji=EmojiCells, wide=2, else 1)
                                if (IsEmojiPresentation(g)) absorbNext = true;
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
            case "flush":
                // ["flush", []] — end-of-screen marker. Nvim may send several redraw batches before
                // the whole screen is consistent; flush closes the last one. Recorded here and acted
                // on in HandleNotification's finally block (where the render actually happens).
                _sawFlush = true;
                break;
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
            case "grid_cursor_goto":
                // Each tuple: [grid_id, row, col] — store grid + local coords; resolve to outer-frame at render.
                // ("cursor_position" was handled here too but is dead code: it is not in nvim's
                // ui_events (verified against --api-info on 0.12.5) and never sent. grid_cursor_goto
                // is the ext_linegrid event; cursor_position belonged to the legacy cell-based grid.)
                foreach (var tuple in a)
                {
                    if (tuple is not object?[] t || t.Length < 3) continue;
                    _curGridId = ToInt(t[0]);
                    _curLocalRow = ToInt(t[1]); _curLocalCol = ToInt(t[2]);
                    ImeTrackCursor(); // keep the IME candidate list on the cursor
                    ScheduleRender();
                }
                break;
            case "mode_change":
            {
                // nvim sends ["mode_change", [name, idx]] — after dispatch strips the name,
                // a = [[name, idx]] (length 1; a[0] is the [name, idx] array). Verified against
                // live nvim raw data. Tolerate a flat [name, idx] too in case framing changes.
                object?[] t = null;
                if (a.Length == 1 && a[0] is object?[] mcInner) t = mcInner;   // wrapped form (actual nvim)
                else if (a.Length >= 2) t = a;                              // flat fallback
                if (t != null && t.Length >= 2)
                {
                    _modeName = t[0]?.ToString() ?? "normal";
                    int mi = ToInt(t[1]);
                    _curModeIdx = mi;
                    if (mi >= 0 && mi < _modeInfos.Count)
                    {
                        var info = _modeInfos[mi];
                        _cursorShape = string.IsNullOrEmpty(info.Shape) ? "block" : info.Shape;
                        _cursorCellPct = info.Pct > 0 ? Math.Clamp(info.Pct, 1, 100) : 100;
                    }
                    else { _cursorShape = "block"; _cursorCellPct = 100; } // no info yet: default block
                    LogImportant($"MODECHANGE name={_modeName} idx={mi} shape={_cursorShape}/{_cursorCellPct}%");
                    // The IME is only allowed to own the keyboard in text-input modes; re-apply that
                    // decision on every mode change (see MainWindow.tsfhost.cs).
                    TsfImeApplyModePolicy();
                    ApplyCursorBlink(mi);
                    ScheduleRender();
                }
                break;
            }
            case "mode_info_set":
                // nvim sends ["mode_info_set", [enabled, [{...}, ...]]] — params wrapped in ONE array.
                // After dispatch strips the name: a = [[true, [...]]] (length 1). Unwrap it.
                var miParams = (a.Length == 1 && a[0] is object?[] inner) ? inner : a;
                if (miParams.Length >= 2 && miParams[1] is object?[] infos)
                {
                    _modeInfos.Clear();
                    for (int i = 0; i < infos.Length; i++)
                    {
                        string mname = "", shape = "block"; int pct = 100, bw = 0, bon = 0, boff = 0; bool bstart = false;
                        if (infos[i] is Dictionary<string, object?> d)
                        {
                            mname = d.TryGetValue("name", out var nm) ? nm?.ToString() ?? "" : "";
                            shape = d.TryGetValue("cursor_shape", out var cs) ? cs?.ToString() ?? "block" : "block";
                            pct = ToInt(d.TryGetValue("cell_percentage", out var cp) ? cp : null);
                            bw = ToInt(d.TryGetValue("blinkwait", out var bwt) ? bwt : null);
                            bon = ToInt(d.TryGetValue("blinkon", out var bo) ? bo : null);
                            boff = ToInt(d.TryGetValue("blinkoff", out var bf) ? bf : null);
                            bstart = d.TryGetValue("blinkstart", out var bs) && bs is bool bsb && bsb;
                        }
                        _modeInfos.Add((mname, shape, pct, bw, bon, boff, bstart));
                    }
                    string insShape = _modeInfos.Count > 2 ? _modeInfos[2].Shape : "?";
                    int insPct = _modeInfos.Count > 2 ? _modeInfos[2].Pct : 0;
                    LogImportant($"MODEINFO entries={_modeInfos.Count} normal={_modeInfos[0].Shape}/{_modeInfos[0].Pct}% insert={insShape}/{insPct}%");
                    if (_curModeIdx >= 0 && _curModeIdx < _modeInfos.Count) ApplyCursorBlink(_curModeIdx); // active mode: refresh shape/blink
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
                    LogImportant($"COLORS fg=0x{PackColor(_defFg):X8} bg=0x{PackColor(_defBg):X8}");
                    var bgBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(_defBg);
                    _root.Background = bgBrush;
                    // Keep the canvas's opaque XAML background in sync so any swap-chain gap after a
                    // resize (floating window) shows the theme color, not the black window base.
                    GlyphCanvas.Background = bgBrush;
                    ScheduleRender();
                }
                break;
            case "option_set":
            {
                // Each tuple: [name, value] — UI-relevant options only ('guifont', 'guifontwide',
                // 'linespace', 'arabicshape', 'ambiwidth', 'emoji', 'mousefocus', 'mousehide',
                // 'mousemoveevent', 'pumblend', 'showtabline', 'termguicolors', and every ext_*).
                // Fired at attach time AND on every later :set / plugin change, so this is the live
                // path for ':set guifont' (the startup + 1s re-read only covers the launch window).
                // guifont/guifontwide/linespace affect layout, so they need a metric re-measure, not
                // just a repaint — RefreshGuifontAsync already no-ops when the parsed font is unchanged.
                bool fontChanged = false;
                foreach (var tuple in a)
                {
                    if (tuple is not object?[] t || t.Length < 2) continue;
                    string oname = t[0]?.ToString() ?? "";
                    switch (oname)
                    {
                        case "guifont":
                        case "guifontwide":
                            fontChanged = true;
                            LogImportant($"OPTION-SET {oname}={t[1]} -> refresh fonts");
                            break;
                        case "linespace":
                            // nvim's 'linespace' pads each ROW in nvim's own layout, so the grid
                            // height change already arrives as grid_resize/grid_line — nothing to do.
                            // This app's vertical pitch trim is NVIM_WINUI_LINESPACE (an app-level
                            // knob read once at startup), so a repaint is all that applies here.
                            LogImportant($"OPTION-SET linespace={t[1]} (nvim-side row padding)");
                            ScheduleRender();
                            break;
                        case "showtabline":
                            // Status row visibility: re-sync the nvim grid so the screen height
                            // accounts for the tabline row nvim just started/stopped drawing.
                            ScheduleNvimResize();
                            break;
                        default:
                            // Emoji width / arabicshape / ambiwidth change how grid_line cells must
                            // be measured; a repaint alone would keep stale column math.
                            if (oname is "emoji" or "arabicshape" or "ambiwidth")
                                LogImportant($"OPTION-SET {oname}={t[1]} (affects cell metrics)");
                            break;
                    }
                }
                if (fontChanged)
                {
                    _ = RefreshGuifontAsync();
                    ScheduleRender();
                }
                break;
            }
            case "win_pos":
                // [grid_id, win_handle, start_row, start_col, width, height] — place a window grid.
                foreach (var tuple in a)
                {
                    if (tuple is not object?[] t || t.Length < 6) continue;
                    int g0 = ToInt(t[0]);
                    MWinPos(g0, ToInt(t[1]), ToInt(t[2]), ToInt(t[3]), ToInt(t[4]), ToInt(t[5]));
                    LogImportant($"WIN-POS g={g0} row={ToInt(t[2])} col={ToInt(t[3])} {ToInt(t[4])}x{ToInt(t[5])}");
                    ScheduleRender();
                }
                break;
            case "msg_set_pos":
                // [grid_id, row, scrolled, sep_char, zindex, compindex] — place the message grid.
                foreach (var tuple in a)
                {
                    if (tuple is not object?[] t || t.Length < 2) continue;
                    int gm = ToInt(t[0]);
                    MMsgSetPos(gm, ToInt(t[1]), t.Length > 4 ? ToInt(t[4]) : 0);
                    LogImportant($"MSG-POS g={gm} row={ToInt(t[1])} z={(t.Length > 4 ? ToInt(t[4]) : 0)}");
                    ScheduleRender();
                }
                break;
            case "win_float_pos":
                // [grid_id, win_handle, anchor, anchor_grid, anchor_row, anchor_col, focusable/mouse_enabled, zindex, compindex, screen_row, screen_col]
                foreach (var tuple in a)
                {
                    if (tuple is not object?[] t || t.Length < 11) continue;
                    int gf = ToInt(t[0]);
                    MWinFloatPos(gf, ToInt(t[1]), t[2], ToInt(t[3]),
                        ToDouble(t[4]), ToDouble(t[5]), ToBool(t[6]), ToInt(t[7]), ToInt(t[8]),
                        ToDouble(t[9]), ToDouble(t[10]));
                    LogImportant($"WIN-FLOAT g={gf} anchor={t[2]} z={ToInt(t[7])} focus={ToBool(t[6])} screen=({ToDouble(t[9]):F0},{ToDouble(t[10]):F0})");
                    ScheduleRender();
                }
                break;
            case "win_hide":
                foreach (var tuple in a)
                {
                    if (tuple is not object?[] t || t.Length < 1) continue;
                    int gh = ToInt(t[0]);
                    MWinHide(gh);
                    LogImportant($"WIN-HIDE g={gh}");
                    ScheduleRender();
                }
                break;
            case "win_close":
                foreach (var tuple in a)
                {
                    if (tuple is not object?[] t || t.Length < 1) continue;
                    int gc = ToInt(t[0]);
                    MWinClose(gc);
                    LogImportant($"WIN-CLOSE g={gc}");
                    ScheduleRender();
                }
                break;
            case "grid_destroy":
                // ["grid_destroy", [grid]]. Sent for good when nvim frees a per-window/message grid;
                // it is the only event that lets the placement state go (see MGridDestroy). The buffer
                // count is logged because id reuse makes "did the state actually go away" invisible
                // from the screen alone.
                foreach (var tuple in a)
                {
                    if (tuple is not object?[] t || t.Length < 1) continue;
                    int gd = ToInt(t[0]);
                    // Record what was still held for this grid: WIN-CLOSE usually runs first and drops
                    // the entry, so "no live buffer" means grid_destroy had nothing left to free there,
                    // while a parked grid (pos=2147483647) or a message grid is exactly the state that
                    // only this event clears.
                    string state = _mgrid.TryGetValue(gd, out var dying)
                        ? $"pos={dying.PosRow} z={dying.ZIndex} msg={dying.IsMessageGrid} focus={dying.Focusable}"
                        : "no live buffer";
                    MGridDestroy(gd);
                    LogImportant($"GRID-DESTROY g={gd} [{state}] (grid buffers now {_mgrid.Count})");
                    ScheduleRender();
                }
                break;
        }
    }
}