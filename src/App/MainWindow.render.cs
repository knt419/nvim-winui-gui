using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using NvimCore;

namespace NvimWinUIGui;

public partial class MainWindow
{

    private int _screenRows, _screenCols;
    private bool _renderQueued;
    // Dynamic per-cell pixel size. Calculated from the ScrollViewer's actual dimensions so the
    // grid fills the window when resized. Recalculated every render; no separate size-change
    // detection needed since RenderNow already runs whenever content changes or fonts update.
    private double _cellW = 9, _cellH = 18;
    // Reference cell size (px) — the unit for window<->grid conversion in BOTH directions:
    // UpdateWindowSize sizes the window to cols*_refCellW x rows*_refCellH (+status row), and the
    // resize sync computes a new grid as floor(avail / _refCell). Derived from the ACTUAL narrow
    // guifont metrics (see MeasureRefCell) instead of a hardcoded constant, so changing guifont size
    // (h12/h20/...) resizes cells to match and glyphs stop clipping. Integer on purpose: fit sizes are
    // then exact pixel values, so after nvim's grid_resize reply the window sits exactly on a cell
    // boundary and no further sync fires (no feedback loop). Defaults 9x18 = old constant; replaced
    // by real measurement once the font is known.
    private double _refCellW = 9, _refCellH = 18;

    // Measure the reference cell from the narrow guifont using an offscreen TextBlock — the SAME
    // DirectWrite layout engine that renders the grid, so the numbers match what's actually drawn
    // (a GDI+ measurement would drift from WinUI and re-introduce clipping). Width = advance of "0"
    // (monospace cell width); height = line height. Rounded UP to whole px to keep fit sizes exact.
    // Must run on the UI thread (creates a XAML TextBlock) — RefreshGuifontAsync calls it via UiPostAsync.
    private void MeasureRefCell()
    {
        try
        {
            var probe = new TextBlock
            {
                FontFamily = new FontFamily(_narrowFont),
                FontSize = _narrowSize,
                Text = "0"
            };
            probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = Math.Ceiling(probe.DesiredSize.Width);
            double h = Math.Ceiling(probe.DesiredSize.Height);
            if (w >= 1 && h >= 1) { _refCellW = w; _refCellH = h; }
            LogStartup($"CELL-METRICS narrow={_narrowFont}@{_narrowSize} -> refcell {_refCellW}x{_refCellH}");
        }
        catch (Exception ex) { LogCritical("MeasureRefCell failed: " + ex.Message); }
    }
    // Natural line height of a font/size via an offscreen TextBlock — used to vertically center the
    // Win2D text inside each cell (DirectWrite draws from the top of its natural line box). UI thread.
    private double MeasureNatLineH(string fontFamily, double size)
    {
        try
        {
            var probe = new TextBlock
            {
                FontFamily = new FontFamily(fontFamily),
                FontSize = size,
                Text = "Hg"
            };
            probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Max(1.0, probe.DesiredSize.Height);
        }
        catch { return _cellH; } // fallback: assume natural height == cell (no offset)
    }
    // Fixed height of the status row under the grid (see MainWindow.cs visual tree). Must match
    // the XAML so window<->grid conversions are exact.
    private const double StatusTextHeight = 25.0;
    // Sanity caps for drag-resize (a maximized ultrawide would otherwise request hundreds of cols).
    private const int MaxGridCols = 1000;
    private const int MaxGridRows = 400;
    private static readonly Color TransparentColor = default;

// ---- Win2D (Direct2D GPU) grid rendering ----------------------------------------------------
// The whole grid is drawn onto ONE CanvasControl each frame: the CPU encodes draw commands
// (~8ms for 1920 cells, measured in a spike) and the GPU rasterizes. This replaces the old
// 1920-element XAML cell grid whose per-cell layout pass was the real latency bottleneck.
private readonly Dictionary<int, Microsoft.Graphics.Canvas.Brushes.ICanvasBrush> _w2dBrushCache = new();
private string _tfKeyNarrow = "", _tfKeyWide = "";
private Microsoft.Graphics.Canvas.Text.CanvasTextFormat? _tfNarrow, _tfWide;
private double _natLineHNarrow = -1, _natLineHWide = -1; // natural line heights (for vertical centering)
    private double _liftNarrow = -1, _liftWide = -1;         // glyph INK center offset inside each line box
private int _invalidateCount; // DIAG: count Invalidate() calls (verify Draw keeps firing)
private double _dpiScale = 0; // device px per DIP, measured once from the window handle (0 = not yet)
private int _rowLogCount;     // DIAG: throttle ROWTOP logging
private bool _advDiagLogged;  // DIAG: one-shot per-glyph advance probe (column-alignment analysis)

private static string? MapKey(VirtualKey vk) => vk switch
    {
        VirtualKey.Escape => "<Esc>",
        VirtualKey.Enter => "<CR>",
        VirtualKey.Back => "<BS>",
        VirtualKey.Tab => "<Tab>",
        VirtualKey.Space => " ",
        VirtualKey.Left => "<Left>",
        VirtualKey.Right => "<Right>",
        VirtualKey.Up => "<Up>",
        VirtualKey.Down => "<Down>",
        VirtualKey.Home => "<Home>",
        VirtualKey.End => "<End>",
        VirtualKey.PageUp => "<PageUp>",
        VirtualKey.PageDown => "<PageDown>",
        VirtualKey.Delete => "<Del>",
        VirtualKey.Insert => "<Insert>",
        _ => ToChar(vk)
    };
private static string? ToChar(VirtualKey vk)
    {
        var state = new byte[256];
        if (!GetKeyboardState(state)) return null;
        IntPtr hkl = GetKeyboardLayout(0);
        uint scan = MapVirtualKey((uint)vk, 0u); // MAPVK_VK_TO_VSC
        var buf = new char[8];
        int n = ToUnicodeEx((uint)vk, scan, state, buf, buf.Length, 0u, hkl);
        if (n <= 0) return null;
        string str = new string(buf, 0, n);
        foreach (char c in str) if (c < 0x20) return null; // dead keys: PoC skip
        return str.Length > 0 ? EscapeChar(str[0]) : null;
    }
private static string EscapeChar(char ch) => ch < 0x20 ? $"<{((int)ch).ToString("X")}>" : ch.ToString();
private static int FindFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
private static async Task<NvimClient> ConnectWithRetryAsync(int port, int attempts)
    {
        Exception? last = null;
        for (int i = 0; i < attempts * 10; i++)
        {
            try { return await NvimClient.ConnectAsync("127.0.0.1", port); }
            catch (Exception ex) { last = ex; await Task.Delay(50); }
        }
        throw new InvalidOperationException($"cannot connect to nvim on 127.0.0.1:{port}: {last?.Message}");
    }
private static int ExtractApiMajor(object? info)
    {
        if (info is object[] arr && arr.Length > 0 && arr[0] is Dictionary<string, object?> m)
            return ToInt(m.TryGetValue("version", out var v) ? v : null);
        return -1;
    }
private void EnsureScreen(int rows, int cols)
{
    if (_cells.Length == rows * cols && _screenRows == rows && _screenCols == cols) return;
    var c = new Cell[rows * cols];
    for (int i = 0; i < c.Length; i++) c[i] = new Cell();
    _cells = c;
    _screenRows = rows;
    _screenCols = cols;
    // Resize the window to exactly fit the grid + status text + title bar.
    UpdateWindowSize(cols, rows);
}

private void UpdateWindowSize(int cols, int rows)
{
    // AppWindow.Resize sets the OUTER window size (content + non-client resize border). With
    // ExtendsContentIntoTitleBar=true there's no title bar in the client area, but the frame
    // still eats _chromeW x _chromeH pixels (measured at runtime; 16x9 on this box). Add it
    // back so the content area is exactly grid + status row.
    int width = (int)(cols * _refCellW) + _chromeW;
    int height = (int)(rows * _refCellH + StatusTextHeight) + _chromeH;
    try { AppWindow.Resize(new SizeInt32(width, height)); } catch { /* ignore */ }
    LogStartup($"RESIZE-DBG UpdateWindowSize req={width}x{height} actual={(AppWindow.Size.Width)}x{(AppWindow.Size.Height)}");
}

// Measure the non-client frame once via Win32 (synchronous + timing-independent, unlike reading a
// XAML ActualWidth that lags one layout pass). border = outer window rect - client rect, in DIP.
private bool _chromeMeasured;
private int _chromeW, _chromeH;
[System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr FindWindow(string? c, string? t);
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
private struct RECT { public int L, T, R, B; }
[System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
[System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
[System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
private void MeasureChromeAndSnap(int rows, int cols)
{
    if (_chromeMeasured) return;
    IntPtr h = FindWindow(null, Title);
    if (h == IntPtr.Zero) return;
    if (!GetWindowRect(h, out RECT w) || !GetClientRect(h, out RECT c)) return;
    double scale = GetDpiForWindow(h) / 96.0; // Win32 rects are physical px; AppWindow.Resize takes DIPs
    _chromeW = (int)Math.Round(((w.R - w.L) - (c.R - c.L)) / scale);   // total left+right border
    _chromeH = (int)Math.Round(((w.B - w.T) - (c.B - c.T)) / scale);   // top+bottom border (title bar is content, so just frame)
    _chromeMeasured = true;
    LogStartup($"RESIZE-DBG chrome={_chromeW}x{_chromeH}");
    UpdateWindowSize(cols, rows); // snap to exact fit now that the frame size is known
}

// ---- Window -> nvim grid resize sync -------------------------------------------------------
// The user drags the window edge; we debounce and ask nvim to reflow its grid to fit.
private System.Threading.Timer? _resizeTimer;
private int _lastSentCols = -1, _lastSentRows = -1;

private void ScheduleNvimResize()
{
    if (_client == null) return;
    // Reset the timer on every size change: only the last one (after dragging stops) is sent.
    _resizeTimer?.Dispose();
    var t = new System.Threading.Timer(_ => UiPostAsync(SendNvimResize), null, 120, System.Threading.Timeout.Infinite);
    _resizeTimer = t;
}

private void SendNvimResize()
{
    if (_client == null) return;
    // Derive cols/rows from AppWindow.Size (synchronous, always current), NOT XAML Actual* values —
    // those lag one layout pass behind a programmatic window resize, which made this read a stale
    // (smaller) height and shrink nvim by a row every cycle.
    int outerW = AppWindow.Size.Width;
    int outerH = AppWindow.Size.Height;
    int cols = Math.Clamp((int)((outerW - _chromeW) / _refCellW), 2, MaxGridCols);
    // Content height = outer - frame border - fixed status row; that's the grid area.
    int rows = Math.Clamp((int)((outerH - _chromeH - StatusTextHeight) / _refCellH), 1, MaxGridRows);
    LogStartup($"RESIZE-DBG appwin={outerW}x{outerH} chrome={_chromeW}x{_chromeH} -> {cols}x{rows}");
    if (cols == _lastSentCols && rows == _lastSentRows) return; // no change since last send
    _lastSentCols = cols; _lastSentRows = rows;
    LogStartup($"RESIZE-REQ {cols}x{rows} root={_root.ActualWidth:F0}x{_root.ActualHeight:F0} appwin={(AppWindow.Size.Width)}x{(AppWindow.Size.Height)}");
    try
    {
        // nvim_ui_try_resize is a notification (no response). On success nvim answers with
        // grid_resize + grid_line events, which flow through the normal redraw path and
        // UpdateWindowSize snaps the window to the exact fit size.
        _client.NotifyAsync("nvim_ui_try_resize", cols, rows);
    }
    catch (Exception ex) { SetStatus($"resize error: {ex.Message}"); }
}

private void UiPostAsync(Action a)
{
    var ctx = _uiSyncCtx;
    if (ctx is not null && !ReferenceEquals(ctx, System.Threading.SynchronizationContext.Current))
        ctx.Post(_ => a(), null);
    else a();
}
private int _schedCount;
private void ScheduleRender()
{
    if (Interlocked.Increment(ref _schedCount) % 50 == 1) LogStartup($"SCHED render count={_schedCount}");
    // Coalesce: mark dirty only. On the UI thread, HandleNotification flushes at its end (one
    // render per batch, not one per event). Off-thread, post to UI context for immediate render.
    if (_renderQueued) return;
    _renderQueued = true;
    var ctx = _uiSyncCtx;
    if (ctx is not null && !ReferenceEquals(ctx, System.Threading.SynchronizationContext.Current))
    {
        try { ctx.Post(_ => FlushRender(), null); return; }
        catch { _renderQueued = false; LogCritical("ScheduleRender Post failed — reset flag"); return; } // never wedge the queue
    }
    // On UI thread: do NOT render inline — the caller (HandleNotification) will flush at its end.
}

/// <summary>Flush a pending render. Call at the end of HandleNotification (UI thread).</summary>
private void FlushRender()
{
    if (!_renderQueued) return;
    _renderQueued = false;
    RenderNow();
}
private double _renderMsTotal; private int _renderCount;
private void RenderNow()
{
    var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
    _renderQueued = false; // clear FIRST so a mutation arriving mid-render posts a fresh pass (no drop)
    int rows = _screenRows, cols = _screenCols;
    if (rows <= 0 || cols <= 0) return;

    // Cell pixel size from the root's actual dimensions minus the fixed status row: the grid fills
    // the window whenever it resizes (manual or programmatic). Falls back to defaults until layout
    // has completed.
    double availW = _root.ActualWidth, availH = Math.Max(0, _root.ActualHeight - StatusTextHeight);
    if (availW > 0 && cols > 0) _cellW = Math.Max(1.0, availW / cols);
    if (availH > 0 && rows > 0) _cellH = Math.Max(1.0, availH / rows);

    // Size the canvas to exactly the grid; the ScrollViewer centers it in the content area. Use
    // whole pixels (same rounding as RenderCore's rowTop/colLeft) so the last cell edge lands on
    // the canvas boundary — no sliver of clear color beyond the final row/column.
    GlyphCanvas.Width = Math.Round(cols * _cellW);
    GlyphCanvas.Height = Math.Round(rows * _cellH);
    EnsureTextFormats();
    if (_diagEnabled && Interlocked.Increment(ref _invalidateCount) % 25 == 1)
        LogStartup($"INVALIDATE #{_invalidateCount} canvas={GlyphCanvas.Width:F0}x{GlyphCanvas.Height:F0} root={_root.ActualWidth:F0}x{_root.ActualHeight:F0}");
    GlyphCanvas.Invalidate(); // the Draw handler does the real (GPU) render this frame

    // Measure the non-client frame (first layout pass) and snap to exact fit. No-op once stable.
    MeasureChromeAndSnap(rows, cols);

    double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    if (_diagEnabled && ms > 8) LogStartup($"RENDER-SCHED {ms:F1}ms cells={rows*cols}");
}

// Win2D text formats are expensive to create — build them once per font/size change and cache.
private void EnsureTextFormats()
{
    string nk = _narrowFont + "@" + _narrowSize;
    if (nk != _tfKeyNarrow) { _tfNarrow = MakeTf(nk); _tfKeyNarrow = nk; _natLineHNarrow = -1; _liftNarrow = -1; LogStartup($"TF narrow={_narrowFont.Split(',')[0]}@{_narrowSize}"); }
    string wk = _wideFont + "@" + _wideSize;
    if (wk != _tfKeyWide) { _tfWide = MakeTf(wk); _tfKeyWide = wk; _natLineHWide = -1; _liftWide = -1; LogStartup($"TF wide={_wideFont.Split(',')[0]}@{_wideSize}"); }
}

private static Microsoft.Graphics.Canvas.Text.CanvasTextFormat MakeTf(string key)
{
    var tf = new Microsoft.Graphics.Canvas.Text.CanvasTextFormat();
    // DirectWrite takes a single family name (no comma lists); its automatic font fallback covers
    // CJK/emoji/symbols the primary lacks, so use just the first family of the parsed chain.
    int at = key.IndexOf('@');
    string fam = (at >= 0 ? key.Substring(0, at) : key).Split(',')[0].Trim();
    if (!string.IsNullOrEmpty(fam)) tf.FontFamily = fam;
    double size = 14;
    if (at >= 0 && double.TryParse(key.Substring(at + 1), out var s)) size = s;
    tf.FontSize = (float)size;
    return tf;
}

// The GPU render: clear, then one pass for backgrounds (merged rects) and one for text
// (consecutive same-color cells batched into single DrawText runs). Runs on the UI thread.
private void OnGlyphCanvasDraw(Microsoft.Graphics.Canvas.UI.Xaml.CanvasControl sender, Microsoft.Graphics.Canvas.UI.Xaml.CanvasDrawEventArgs args)
{
    try { RenderCore(args.DrawingSession, sender); }
    catch (Exception ex) { LogCritical("DRAW EXCEPTION: " + ex.GetType().Name + ": " + ex.Message); }
}

// Shared render body — also used by the DIAG snapshot path (offscreen CanvasRenderTarget).
private void RenderCore(Microsoft.Graphics.Canvas.CanvasDrawingSession ds, Microsoft.Graphics.Canvas.ICanvasResourceCreator rc)
{
    try
    {
    var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
    int rows = _screenRows, cols = _screenCols;
    if (rows <= 0 || cols <= 0) return;
    // Measure the window's DPI scale once — needed to snap cell boundaries to whole DEVICE pixels.
    if (_dpiScale < 1.0) { try { IntPtr dh = FindWindow(null, Title); uint d = GetDpiForWindow(dh); if (d > 0) _dpiScale = d / 96.0; } catch { } }
    ds.Clear(_defBg);

    // One-shot resolved-color DIAG: log the EXACT packed ARGB that is about to be drawn, so we can
    // tell whether "pure black" comes from _defBg/_defFg themselves or from compositing/canvas bg.
    if (_diagEnabled && !_colorDiagLogged)
    {
        _colorDiagLogged = true;
        var sbrC = new System.Text.StringBuilder();
        sbrC.Append($"COLORDIAG defBg=0x{PackColor(_defBg):X8} defFg=0x{PackColor(_defFg):X8} " +
                    $"multigrid={_multigridActive} hlDefs={_hlDefs.Count} rows={rows} cols={cols}\n");
        // Sample a handful of cells across the buffer: their Hl id and resolved bg/fg.
        var buf0 = BuildRenderCells();
        int[] sampleIdx = { 0, cols / 2, (rows / 2) * cols + cols / 2, rows * cols - 1 };
        foreach (var si in sampleIdx)
            if (si >= 0 && si < buf0.Length)
            {
                var cc = buf0[si];
                int sr = si / cols, sc = si % cols;
                Color cbg = CellBg(sr, sc, -1); // curIdx=-1: never the cursor block
                sbrC.Append($"  cell[{sr},{sc}] hl={cc.Hl} bg=0x{PackColor(cbg):X8}\n");
            }
        LogStartup(sbrC.ToString());
    }

    // Multigrid: composite outer frame (grid 1) + window grids into the draw buffer, and resolve
    // the per-grid cursor to outer-frame coordinates. In linegrid mode this is a no-op passthrough.
    var cells = BuildRenderCells();
    _activeRenderCells = cells;

    int curRow = _curLocalRow, curCol = _curLocalCol;
    if (_multigridActive) MGridResolveCursor(_curGridId, _curLocalRow, _curLocalCol, out curRow, out curCol);
    // Clamp: a stale cursor row beyond the current grid (e.g. after a shrink before nvim's next
    // cursor_position) would make curIdx land outside _cells and the block silently vanish.
    int curIdx = (curRow >= 0 && curRow < rows) ? curRow * cols + Math.Clamp(curCol, 0, cols - 1) : -1;

    // Integer-pixel row/column boundaries: _cellW/_cellH are fractional (avail/cols), so r*_cellH
    // lands on subpixel y values and Direct2D rasterization leaves a 1-2px gap between adjacent
    // rows. Invisible over the clear color, but when a dimmed background highlight is drawn the
    // default bg shows through the seams as thin lines (visible behind floating windows). Snapping
    // every boundary to whole pixels makes cells share exact edges — no gaps, no overlaps.
    // Snap in DEVICE-pixel space: Win2D draws in DIPs and scales by DPI internally, so at 125%/150%
    // an integer-DIP boundary is fractional device px and the seam returns. Round to whole device
    // pixels, then convert back to DIPs — exact on screen at any scale. Fall back to 1.0 if the
    // DPI probe failed (a zero divisor would produce NaN coordinates).
    double dpi = _dpiScale > 0 ? _dpiScale : 1.0;
    var rowTop = new double[rows + 1];
    for (int r = 0; r <= rows; r++) rowTop[r] = Math.Round(r * _cellH * dpi) / dpi;
    if (_diagEnabled && Interlocked.Increment(ref _rowLogCount) % 50 == 1)
    {
        var sbr2 = new System.Text.StringBuilder();
        for (int r = 0; r < Math.Min(6, rows); r++) sbr2.Append($"r{r}:[{rowTop[r]:F2},{rowTop[r+1]-rowTop[r]:F2}] ");
        LogStartup($"ROWTOP cellH={_cellH:F4} dpi={dpi:F3} {sbr2}");
    }
    var colLeft = new double[cols + 1];
    for (int c = 0; c <= cols; c++) colLeft[c] = Math.Round(c * _cellW * dpi) / dpi;

    // LineSpacing: leave at the font's natural value (-1). Pinning it to _cellH made DirectWrite
    // place the glyph at the TOP of a taller line box, so text sat high in each cell and the cursor
    // block (full cell height) appeared shifted down relative to the characters. Instead we center
    // the natural line box inside the cell via an explicit y offset below.

    // Natural line heights (ascent+descent) for vertical centering — measured once per font/size via
    // an offscreen TextBlock (same technique as MeasureRefCell). Draw runs on the UI thread.
    if (_natLineHNarrow < 0) _natLineHNarrow = MeasureNatLineH(_narrowFont, _narrowSize);
    if (_natLineHWide < 0) _natLineHWide = MeasureNatLineH(_wideFont, _wideSize);

    // Vertical alignment: centering each font's LINE BOX (natH) in the cell does not align the two
    // fonts' INK, because ascent/descent asymmetry differs per font — e.g. narrow '0' ink center
    // sits ~0.4px above the cell center while wide '日' sits ~1.0px below (measured ~1.38px apart)
    // → mixed rows / cursor row look vertically shifted. Instead record each font's ink-center
    // offset inside its line box (LayoutBounds.Y + H/2) so both fonts draw their ink to the SAME
    // point: yDraw = rowTop + rh/2 - lift.
    if (_liftNarrow < 0 || _liftWide < 0)
    {
        try
        {
            using var p0 = new Microsoft.Graphics.Canvas.Text.CanvasTextLayout(rc, "0", _tfNarrow!, 0, 0);
            using var pW = new Microsoft.Graphics.Canvas.Text.CanvasTextLayout(rc, "日", _tfWide!, 0, 0);
            var b0 = p0.LayoutBounds; var bW = pW.LayoutBounds;
            _liftNarrow = b0.Y + b0.Height / 2;
            _liftWide = bW.Y + bW.Height / 2;
            if (_diagEnabled)
            {
                double rh = rowTop.Length > 1 ? rowTop[1] - rowTop[0] : 0;
                LogStartup($"INK-LIFT narrow={_liftNarrow:F2} wide={_liftWide:F2} | yNarrow={rh / 2 - _liftNarrow:F2} yWide={rh / 2 - _liftWide:F2} " +
                           $"-> inkCenterNarrow={rh / 2:F2} inkCenterWide={rh / 2:F2} (cell center = shared)");
            }
        }
        catch (Exception ex)
        {
            _liftNarrow = 0; _liftWide = 0;
            LogStartup("INK-LIFT measure failed: " + ex.Message);
        }
    }

    // One-shot DIAG probe: measure the rendered advance of representative glyphs with BOTH text
    // formats and report how far each strays from the cell grid. A glyph whose advance != cellW
    // (narrow) or != 2*cellW (wide) misaligns every column AFTER it in the SAME DrawText run —
    // rows containing such a glyph drift while pure-ASCII rows stay put, which is the "行によって
    // 幅が異なる" effect. The data tells us exactly which codepoints overflow the grid.
    if (_diagEnabled && !_advDiagLogged)
    {
        _advDiagLogged = true;
        try
        {
            string[] sample = { "0", "A", "a", "W", " ", "・", "…", "─", "│", "┌", "┐", "└", "┘", "→", "←", "↑", "↓", "↲", "•", "≈", "±", "°", "★", "✔", "✘", "◆", "日", "あ", "ア", "中", "ㅎ", "─", "”", "’", "‑", ".", ",", "!", "?", "%", "&", "(", ")", "[", "]", "{", "}", "-", "_", "+", "=", "<", ">", "/", "\\", "\"", "'", ":", ";", "#", "@", "$" };
            foreach (var ch in sample)
            {
                using var lN = new Microsoft.Graphics.Canvas.Text.CanvasTextLayout(rc, ch, _tfNarrow!, 0, 0);
                using var lW = new Microsoft.Graphics.Canvas.Text.CanvasTextLayout(rc, ch, _tfWide!, 0, 0);
                var bN = lN.LayoutBounds;
                double an = Math.Round(bN.Width * 1000) / 1000.0;
                double aw = Math.Round(lW.LayoutBounds.Width * 1000) / 1000.0;
                double liftN = bN.Y + bN.Height / 2;
                bool wide = IsWideGlyph(ch);
                bool ok = wide
                    ? Math.Abs(aw - 2 * _cellW) < _cellW * 0.1
                    : Math.Abs(an - _cellW) < _cellW * 0.1;
                LogStartup($"GLYPH-ADV '{ch}' U+{char.ConvertToUtf32(ch, 0):X4} class={(wide ? "WIDE" : "narrow")} " +
                           $"narrowAdv={an:F3} wideAdv={aw:F3} cellW={_cellW:F2} " +
                           $"narrowCells={an / _cellW:F2} liftN={liftN:F2} {(ok ? "OK" : ">> MISALIGNED <<")}");
            }
            // Vertical: this block reported the OLD line-box centering misalignment and is now moot —
            // yNarrow/yWide compensate via _liftNarrow/_liftWide (INK-LIFT logs the actual centers).
        }
        catch (Exception ex) { LogStartup("GLYPH-ADV probe failed: " + ex.Message); }
    }

    // Pass 1: backgrounds (highlight + inverted cursor). Horizontal runs per row; consecutive rows
    // whose run structure is IDENTICAL extend the previous rects' height instead of drawing new ones,
    // so a uniform region becomes ONE big rect with no interior edges. Win2D exposes no AA toggle, and
    // two rects sharing an edge are rasterized independently — they blend ~50% at the seam and the
    // clear color (default bg) shows through as 1-2px lines between rows (visible behind floating
    // windows). Merging eliminates every interior boundary; only true color changes keep an edge,
    // where anti-aliasing is wanted. Runs are keyed by Color value directly — NOT PackColor: with
    // A=255 the packed int has its sign bit set and a `key < 0` transparency test silently drops
    // every opaque cell (this was why fills never appeared).
    var prevRuns = new List<(int s, int e, Color key)>();
    int blockTop = 0; // top row of the open merged block
    for (int r = 0; r <= rows; r++)
    {
        var curRuns = new List<(int s, int e, Color key)>();
        if (r < rows)
        {
            int c = 0;
            while (c < cols)
            {
                Color bg = CellBg(r, c, curIdx);
                if (bg == TransparentColor) { c++; continue; } // no fill: clear color shows through
                int s = c;
                do { c++; } while (c < cols && CellBg(r, c, curIdx) == bg);
                curRuns.Add((s, c, bg));
            }
        }
        bool same = prevRuns.Count == curRuns.Count;
        for (int i = 0; same && i < curRuns.Count; i++) if (prevRuns[i] != curRuns[i]) same = false;
        if (!same) // structure changed: flush the open block, start a new one at this row
        {
            foreach (var run in prevRuns)
                ds.FillRectangle(new Windows.Foundation.Rect(colLeft[run.s], rowTop[blockTop], colLeft[run.e] - colLeft[run.s], rowTop[r] - rowTop[blockTop]), GetW2dBrush(rc, run.key));
            prevRuns = curRuns;
            blockTop = r;
        } // else: identical structure — the open rects simply extend one more row (no new draw)
    }

    // Pass 2: text. A "run" is consecutive cells sharing the same foreground color and narrow
    // font — drawn as ONE DrawText call (the big win over per-cell XAML). Wide glyphs break the
    // run and are drawn individually with the wide format; covered tails ("") add no ink.
    for (int r = 0; r < rows; r++)
    {
        double rh = rowTop[r + 1] - rowTop[r]; // this row's pixel height (device-px snapped)
        float yNarrow = (float)(rowTop[r] + rh / 2 - _liftNarrow); // both fonts aligned on ink center
        float yWide   = (float)(rowTop[r] + rh / 2 - _liftWide);
        if (_diagEnabled && r == curRow)
        {
            var sbd = new System.Text.StringBuilder();
            for (int cc = 0; cc < Math.Min(8, cols); cc++)
                sbd.Append($"[{cc}:'{cells[r * cols + cc].Text}'{(IsWideGlyph(cells[r * cols + cc].Text) ? "W" : "")}]");
            LogStartup($"CURROW-CELLS row={r} {sbd} yNarrow={yNarrow:F2} yWide={yWide:F2} rh={rh:F2}");
        }
        int c = 0;
        while (c < cols)
        {
            var cell = cells[r * cols + c];
            string txt = cell.Text;
            if (txt.Length == 0) { c++; continue; } // covered tail of a wide glyph

            bool isCur = r * cols + c == curIdx && curIdx >= 0;
            Color fg;
            if (isCur) fg = _defBg; // inverted cursor: default bg as glyph color
            else if (cell.Hl >= 0 && _hlDefs.TryGetValue(cell.Hl, out var h)) fg = h.Fg;
            else fg = _defFg;
            int fgi = PackColor(fg);

            if (IsWideGlyph(txt))
            {
                if (_diagEnabled && r == curRow) LogStartup($"WIDE-CELL row={r} col={c} txt='{txt}' cp={(int)txt[0]:X4} yWide={yWide:F2} yNarrow={yNarrow:F2}");
                ds.DrawText(txt, (float)colLeft[c], yWide, GetW2dBrush(rc, fg), _tfWide!);
                c++; // the tail cell is "" and gets skipped by the loop above
                continue;
            }

            // Ambiguous/proportional glyph whose natural advance is NOT ~1 cell (arrows, stars,
            // dingbats, special hyphens... = EAW Ambiguous placed in a single cell by nvim but drawn
            // ~1.5-2.5 cells wide by the font). Draw it at ITS OWN cell origin instead of inside the
            // run: a run would accumulate the extra advance and shift every later column of the row
            // (the "width differs between rows" effect). Individually placed, ink may still bleed a
            // couple px into the neighbor but column positions stay locked to the grid. Vertically
            // deviant glyphs (same symbol family, INK center outside the Latin reference) are split
            // out of runs too, and every individually-drawn glyph is placed at ITS OWN ink lift so
            // it optically centers on the cell like the surrounding text (a lone ↲ under the cursor
            // row otherwise sits ~1px low and looks like the cursor row moved).
            if (!IsGridAlignedNarrow(txt) || VertDeviantNarrow(txt))
            {
                int cpS = txt.Length > 0 && char.IsHighSurrogate(txt[0]) && txt.Length > 1 && char.IsLowSurrogate(txt[1])
                    ? char.ConvertToUtf32(txt, 0) : (txt.Length > 0 ? txt[0] : 0);
                if (_diagEnabled && _skewDiagAdded.Add(cpS))
                    LogStartup($"SKEW-CELL row={r} col={c} txt='{txt}' cp=U+{cpS:X4} advCells={GlyphAdvCells(txt):F2} liftN={GlyphLiftN(txt):F2} (drawn at own cell origin)");
                float yOwn = (float)(rowTop[r] + rh / 2 - GlyphLiftN(txt));
                ds.DrawText(txt, (float)colLeft[c], yOwn, GetW2dBrush(rc, fg), _tfNarrow!);
                c++;
                continue;
            }

            int start = c;
            var sb = new System.Text.StringBuilder();
            while (c < cols)
            {
                var cc2 = cells[r * cols + c];
                string t2 = cc2.Text;
                if (t2.Length == 0) { c++; continue; } // covered tail: no ink, run continues
                bool isCur2 = r * cols + c == curIdx && curIdx >= 0;
                Color fg2;
                if (isCur2) fg2 = _defBg;
                else if (cc2.Hl >= 0 && _hlDefs.TryGetValue(cc2.Hl, out var h2)) fg2 = h2.Fg;
                else fg2 = _defFg;
                if (PackColor(fg2) != fgi || IsWideGlyph(t2) || !IsGridAlignedNarrow(t2) || VertDeviantNarrow(t2)) break; // run boundary
                sb.Append(t2);
                c++;
            }
            ds.DrawText(sb.ToString(), (float)colLeft[start], yNarrow, GetW2dBrush(rc, fg), _tfNarrow!);
        }
    }

    double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    _renderMsTotal += ms; int rcc = Interlocked.Increment(ref _renderCount);
    if (_diagEnabled && (rcc % 25 == 0 || ms > 8)) LogStartup($"RENDER #{rcc} {ms:F1}ms avg={_renderMsTotal/rcc:F1}ms cells={rows*cols}");
    }
    catch (Exception ex)
    {
        LogCritical("DRAW EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
    }
}

// Win2D brushes cached by packed ARGB. Must be created inside a Draw/CreateResources handler
// (they need the canvas's device), so this is only called from OnGlyphCanvasDraw / RenderCore.
private Microsoft.Graphics.Canvas.Brushes.ICanvasBrush GetW2dBrush(Microsoft.Graphics.Canvas.ICanvasResourceCreator creator, Color c)
{
    int key = PackColor(c);
    if (!_w2dBrushCache.TryGetValue(key, out var b))
    {
        b = new Microsoft.Graphics.Canvas.Brushes.CanvasSolidColorBrush(creator, c);
        _w2dBrushCache[key] = b;
    }
    return b;
}

// Inverse of PackColor (for the background-run flush path).
private static Color UnpackPacked(int p) => Color.FromArgb((byte)(p >> 24), (byte)(p >> 16), (byte)(p >> 8), (byte)p);
// Background color of one cell for Pass 1: inverted cursor block, highlight bg, or transparent.
private Cell[]? _activeRenderCells; // set by RenderCore each frame (composited multigrid buffer)
private bool _colorDiagLogged;      // one-shot resolved-color DIAG guard
private Color CellBg(int r, int c, int curIdx)
{
    if (r * _screenCols + c == curIdx && curIdx >= 0) return _defFg; // inverted cursor: default fg as block
    var buf = _activeRenderCells ?? _cells;
    var cell = buf[r * _screenCols + c];
    if (cell.Hl >= 0 && _hlDefs.TryGetValue(cell.Hl, out var h)) return h.Bg;
    return TransparentColor;
}
// WinAppSDK 2.x's Windows.UI.Color has no PackedValue property, so pack ARGB from the
// component fields ourselves for brush-cache keys.
private static int PackColor(Color c) => (c.A << 24) | (c.R << 16) | (c.G << 8) | c.B;

// Determine if a character is "wide" (takes up two grid cells) and therefore needs the
// guifontwide font to render at the correct width in the grid. This covers all common
// multibyte Japanese/CJK/Korean characters plus full-width symbols/punctuation that are
// not covered by guifont's glyph set — they fall back to guifontwide instead of showing
// tofu or misaligning the grid. WinAppSDK has no easy per-character glyph coverage API,
// so we use Unicode ranges as a reliable proxy for "multibyte" characters.
private static bool IsWideCodePoint(int cp)
{
    return (cp >= 0x1100 && cp <= 0x115F) ||   // Hangul Jamo
           (cp >= 0x2E80 && cp <= 0xA4CF) ||   // CJK radicals, Kangxi, CJK unified ideographs
           (cp >= 0xAC00 && cp <= 0xD7A3) ||   // Korean Hangul syllables
           (cp >= 0xF900 && cp <= 0xFAFF) ||   // CJK compatibility ideographs
           (cp >= 0xFE30 && cp <= 0xFE4F) ||   // CJK compatibility forms
           (cp >= 0xFF00 && cp <= 0xFFEF) ||   // Fullwidth forms (incl. fullwidth space 0x3000 handled below)
           (cp >= 0x1F300 && cp <= 0x1FAFF) || // Emoji & pictographs (astral, surrogate pairs)
           (cp >= 0x3040 && cp <= 0x30FF) ||   // Hiragana / Katakana
           cp == 0x3000;                        // Fullwidth space
}

// String form that handles surrogate pairs: an emoji/astral glyph arrives as two UTF-16 chars, so
// the first code point must be recombined before the range check (txt[0] alone is just a high surrogate).
private static bool IsWideGlyph(string s)
{
    if (s.Length == 0) return false;
    int cp = char.IsHighSurrogate(s[0]) && s.Length > 1 && char.IsLowSurrogate(s[1])
        ? ((int)s[0] - 0xD800) * 0x400 + (int)s[1] - 0xDC00 + 0x10000
        : s[0];
    return IsWideCodePoint(cp);
}

// ---- Column-alignment guard ---------------------------------------------------------------
// A batched DrawText run is only grid-locked when every glyph in it advances ~one cell. Glyphs the
// narrow font renders proportionally or ~1.5-2.5 cells wide (EAW-Ambiguous arrows/stars/dingbats,
// special hyphens, etc., all placed in ONE cell by nvim) would accumulate their skew across the run
// and push later columns off the grid — differently on each row, so columns stop lining up between
// adjacent lines. Measure once per code point (DirectWrite + fallback = exactly what the run would
// draw) and keep only ~1-cell glyphs in runs; everything else is placed at its own cell origin.
private readonly Dictionary<int, float> _glyphAdv = new();
private readonly HashSet<int> _skewDiagAdded = new(); // DIAG: log each skewed code point once

private float GlyphAdvCells(string s)
{
    int cp = s.Length > 0 && char.IsHighSurrogate(s[0]) && s.Length > 1 && char.IsLowSurrogate(s[1])
        ? char.ConvertToUtf32(s, 0)
        : (s.Length > 0 ? s[0] : 0);
    return MeasureGlyphAdv(cp, s) / Math.Max(0.1f, (float)_cellW);
}

private bool IsGridAlignedNarrow(string s)
{
    if (s.Length == 0) return true;
    int cp = char.IsHighSurrogate(s[0]) && s.Length > 1 && char.IsLowSurrogate(s[1])
        ? char.ConvertToUtf32(s, 0)
        : s[0];
    if (IsWideCodePoint(cp)) return false; // wide glyphs break the narrow run regardless
    if (cp == 0x20) return true; // space: LayoutBounds reports no ink (0 width) but advances mono cell width
    float cells = GlyphAdvCells(s);
    return Math.Abs(cells - 1.0f) <= 0.10f;
}

// Advance (DIPs) of a single glyph in the narrow format, cached by code point.
private float MeasureGlyphAdv(int cp, string s)
{
    if (_glyphAdv.TryGetValue(cp, out var cached)) return cached;
    float a;
    try
    {
        using var l = new Microsoft.Graphics.Canvas.Text.CanvasTextLayout(GlyphCanvas, s, _tfNarrow!, 0, 0);
        a = (float)l.LayoutBounds.Width; // ink width; spaces are special-cased in IsGridAlignedNarrow
    }
    catch { a = (float)_cellW; } // measurement failure -> assume aligned (conservative: stays in runs)
    _glyphAdv[cp] = a;
    return a;
}

// ---- Vertical-alignment guard ---------------------------------------------------------------
// Ink-center offset of a glyph inside its line box (LayoutBounds.Y + H/2), cached per code point
// (narrow font). Measure once per code point with the SAME direct-write + fallback path the run
// would use. All Latin/digits/punctuation come out at the reference 9.60, but symbol glyphs draw
// ~0.6-1.0px LOWER-INSIDE their box (arrows ★ ◆ ✔ ↲ ⏎ ... = the same EAW-Ambiguous set that
// also skews horizontally). Drawn at the Latin reference y such a glyph sits visibly below the
// row's text — on an empty line under the cursor it reads as the cursor row being shifted. So
// individually-drawn glyphs are placed at THEIR OWN lift (ink center lands on the cell center),
// and run batching refuses to swallow a glyph whose lift strays from the reference.
private readonly Dictionary<int, float> _glyphLiftN = new();

private float GlyphLiftN(string s)
{
    if (s.Length == 0) return (float)_liftNarrow;
    int cp = char.IsHighSurrogate(s[0]) && s.Length > 1 && char.IsLowSurrogate(s[1])
        ? ((int)s[0] - 0xD800) * 0x400 + (int)s[1] - 0xDC00 + 0x10000
        : s[0];
    if (_glyphLiftN.TryGetValue(cp, out var cached)) return cached;
    float lift;
    try
    {
        using var l = new Microsoft.Graphics.Canvas.Text.CanvasTextLayout(GlyphCanvas, s, _tfNarrow!, 0, 0);
        var b = l.LayoutBounds;
        lift = (float)(b.Y + b.Height / 2);
    }
    catch { lift = (float)_liftNarrow; } // measurement failure -> Latin reference (safest)
    _glyphLiftN[cp] = lift;
    return lift;
}

private bool VertDeviantNarrow(string s)
{
    if (s.Length == 0 || s[0] == ' ') return false; // space has no ink; "" is a covered tail
    return Math.Abs(GlyphLiftN(s) - (float)_liftNarrow) > 0.4f; // symbols hoist/lower in-box
}

private static int ToInt(object? v) => v switch
    {
        null => -1,
        byte b => b, short s2 => s2, int i => i, long l => (int)l,
        double d => (int)d,
        _ => -1
    };
private static double ToDouble(object? v) => v switch
    {
        null => 0.0,
        byte b => b, short s2 => s2, int i => i, long l => l,
        float f => f, double d => d,
        _ => 0.0
    };
private static bool ToBool(object? v) => v is bool b && b;
private static Hl ParseHl(object? v)
    {
        if (v is Dictionary<string, object?> m)
            return new Hl(HintColor(1, ToInt(m.TryGetValue("foreground", out var f) ? f : null)),
                          HintColor(2, ToInt(m.TryGetValue("background", out var b) ? b : null)),
                          ToInt(m.TryGetValue("blend", out var bl) ? bl : null));
        return default;
    }
private static Color HintColor(int slot, int value)
{
    if (value < 0)
        return slot == 1 ? Color.FromArgb(0xFF, 0xDC, 0xDC, 0xDC) : TransparentColor;
        
    // nvim RGB colors (sent with rgb=true in ui_attach) are typically 0xRRGGBB without an alpha
    // channel. When parsed as ARGB, the alpha byte becomes 0, making text invisible. Detect this
    // by checking if value < 0x1000000 (no explicit alpha) and default to full opacity.
    byte a = (byte)(value >> 24 & 0xFF);
    if (a == 0 && value < 0x1000000) a = 0xFF;  // RGB without alpha -> assume opaque
        
    byte r = (byte)(value >> 16 & 0xFF);
    byte g = (byte)(value >> 8 & 0xFF);
    byte b = (byte)(value & 0xFF);
    return Color.FromArgb(a, r, g, b);
}
private void OnClosed(object sender, object e)
    {
        try { _client?.Dispose(); } catch { }
        try { if (_nvimProc is not null && !_nvimProc.HasExited) _nvimProc.Kill(true); } catch { }
    }
}
