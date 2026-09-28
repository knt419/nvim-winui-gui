using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
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

    // Vertical pitch reduction (px) applied to the cell height so consecutive text rows pack tightly —
    // the XAML line box (~1.25em) leaves a visible horizontal gap between glyph rows. Read from
    // NVIM_WINUI_LINESPACE (default 0). 0 = keep the box height; a positive value trims that many px
    // from each row's pitch for a tighter, gap-free terminal look.
    private readonly double _linePitchReduce = ParseLinePitch();
    private static double ParseLinePitch()
    {
        var v = Environment.GetEnvironmentVariable("NVIM_WINUI_LINESPACE");
        return double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d >= 0 ? d : 0.0;
    }

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
            // Height = line box MINUS the linespace trim. The XAML line box (~1.25em) sits taller
            // than the glyph band, so consecutive rows of text leave a visible gap (the reported
            // ~1px). Subtracting the trim makes the vertical pitch hug the ink: rows pack tighter,
            // window and cell height shrink together and the inter-line whitespace disappears.
            // Tunable via NVIM_WINUI_LINESPACE (px, default 0).
            double h = Math.Max(1.0, Math.Ceiling(probe.DesiredSize.Height) - _linePitchReduce);
            if (w >= 1 && h >= 1) { _refCellW = w; _refCellH = h; }
            LogStartup($"CELL-METRICS narrow={_narrowFont}@{_narrowSize} -> refcell {_refCellW}x{_refCellH} (linespace-{_linePitchReduce})");
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

    // Monospace advance of the narrow font (DIPs) via an offscreen TextBlock: n digits render at
    // exactly n*advance. The CELL grid must equal this width — when _cellW is derived from the
    // window size instead (availW/cols), a ~0.2px mismatch per column makes batched runs drift
    // left of the grid and RESET at every run boundary (the cursor cell breaks its run), moving
    // the cursor glyph's left whitespace and everything after it rightward by cursorCol*delta.
    private double MeasureFontAdvance(string fontFamily, double size)
    {
        // True DWrite advance the batched runs will use — the difference between a 10-char and a
        // 20-char layout width (side bearings cancel, the delta is exactly 10 advances). A TextBlock
        // DesiredSize probe reads ~0.1px/cell high for this font, which would keep a residual drift.
        try
        {
            using var l10 = new Microsoft.Graphics.Canvas.Text.CanvasTextLayout(GlyphCanvas, new string('0', 10), _tfNarrow!, 5000, 0);
            using var l20 = new Microsoft.Graphics.Canvas.Text.CanvasTextLayout(GlyphCanvas, new string('0', 20), _tfNarrow!, 5000, 0);
            double adv = (l20.LayoutBounds.Width - l10.LayoutBounds.Width) / 10.0;
            return Math.Max(1.0, adv);
        }
        catch { return _cellW; }
    }
    // The application status line under the grid (see MainWindow.cs visual tree). Hidden
    // by default; set NVIM_WINUI_STATUSBAR=1 to show it. Must match the XAML row height
    // so window<->grid conversions are exact.
    private static bool StatusBarVisible => Environment.GetEnvironmentVariable("NVIM_WINUI_STATUSBAR") == "1";
    private static double StatusTextHeight => StatusBarVisible ? 25.0 : 0.0;
    // Sanity caps for drag-resize (a maximized ultrawide would otherwise request hundreds of cols).
    private const int MaxGridCols = 1000;
    private const int MaxGridRows = 400;
    private static readonly Color TransparentColor = default;

// ---- Win2D (Direct2D GPU) grid rendering ----------------------------------------------------
// The whole grid is drawn onto ONE CanvasControl each frame: the CPU encodes draw commands
// (~8ms for 1920 cells, measured in a spike) and the GPU rasterizes. This replaces the old
// 1920-element XAML cell grid whose per-cell layout pass was the real latency bottleneck.
private readonly Dictionary<int, Microsoft.Graphics.Canvas.Brushes.ICanvasBrush> _w2dBrushCache = new();
private readonly Dictionary<string, Microsoft.Graphics.Canvas.Text.CanvasTextFormat> _tfStyleCache = new(); // italic/bold variants (normal uses _tfNarrow/_tfWide)
private string _tfKeyNarrow = "", _tfKeyWide = "";
private Microsoft.Graphics.Canvas.Text.CanvasTextFormat? _tfNarrow, _tfWide;
private double _natLineHNarrow = -1, _natLineHWide = -1; // natural line heights (for vertical centering)
    private double _liftNarrow = -1, _liftWide = -1;         // glyph INK center offset inside each line box
    private double _fontAdvance = -1;                        // narrow font's monospace advance (= _cellW, kills run drift)
private int _invalidateCount; // DIAG: count Invalidate() calls (verify Draw keeps firing)
private double _dpiScale = 0; // device px per DIP, measured once from the window handle (0 = not yet)
private int _rowLogCount;     // DIAG: throttle ROWTOP logging
private bool _advDiagLogged;  // DIAG: one-shot per-glyph advance probe (column-alignment analysis)
    private bool _emojiDiagLogged; // DIAG: one-shot color-emoji path probe
    private bool _emojiShotLogged0;// DIAG: one-shot offscreen color-emoji snapshot
    private bool _fullShotLogged0; // DIAG: one-shot full-canvas live-composite snapshot

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
    double cw = _fontAdvance > 0 ? _fontAdvance : _refCellW;
    // CEIL, not round: the width must round-trip through SendNvimResize's floor((W-chrome)/cw).
    // With round(), a cols*cw that rounds DOWN makes the snapped window read back as cols-1, and
    // each nvim grid_resize reply shrank the window by one more cell (visible shrink after drag).
    // Ceil keeps (width-chrome)/cw in [cols, cols+1) so floor returns exactly cols — idempotent.
    int width = (int)Math.Ceiling(cols * cw) + _chromeW;
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
    double cw = _fontAdvance > 0 ? _fontAdvance : _refCellW;
    int cols = Math.Clamp((int)((outerW - _chromeW) / cw), 2, MaxGridCols);
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

    // Cell pixel size: the grid is sized from the FONT's monospace advance (a terminal, not a
    // stretcher) so glyphs, fills and the cursor block all share one exact coordinate space — any
    // mismatch makes batched runs drift and reset at cursor/color splits. Falls back to stretching
    // (availW/cols) only until the advance has been measured or if its measurement failed.
    double availW = _root.ActualWidth, availH = Math.Max(0, _root.ActualHeight - StatusTextHeight);
    EnsureTextFormats();
    if (_fontAdvance < 0) _fontAdvance = MeasureFontAdvance(_narrowFont, _narrowSize);
    if (_fontAdvance > 0 && cols > 0) _cellW = Math.Max(1.0, _fontAdvance);
    else if (availW > 0 && cols > 0) _cellW = Math.Max(1.0, availW / cols);
    if (availH > 0 && rows > 0) _cellH = Math.Max(1.0, availH / rows);
    ImeTrackCursor(); // cell metrics moved, so the IME candidate anchor is stale

    // Size the canvas to exactly the grid; the ScrollViewer centers it in the content area. The
    // whole-pixel size lets the last cell edge land on the canvas boundary — no sliver of clear
    // color beyond the final row/column (interior edges use the rowTop/colLeft spaces above).
    GlyphCanvas.Width = Math.Round(cols * _cellW);
    GlyphCanvas.Height = Math.Round(rows * _cellH);
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
    if (nk != _tfKeyNarrow) { _tfNarrow = MakeTf(nk, false, false); _tfKeyNarrow = nk; _natLineHNarrow = -1; _liftNarrow = -1; _fontAdvance = -1; _tfStyleCache.Clear(); _glyphAdv.Clear(); _glyphLiftN.Clear(); LogStartup($"TF narrow={_narrowFont.Split(',')[0]}@{_narrowSize}"); }
    string wk = _wideFont + "@" + _wideSize;
    if (wk != _tfKeyWide) { _tfWide = MakeTf(wk, false, false); _tfKeyWide = wk; _natLineHWide = -1; _liftWide = -1; LogStartup($"TF wide={_wideFont.Split(',')[0]}@{_wideSize}"); }
}

// Style index for the format cache: bit0=italic, bit1=bold (0 = normal).
private static int StyleIdx(bool italic, bool bold) => (italic ? 1 : 0) | (bold ? 2 : 0);

// Text format for a cell's style. Normal reuses the cached _tfNarrow/_tfWide; italic/bold/
// bold-italic are built once per font+size and cached by key — most sessions never touch them,
// so they stay lazy. DirectWrite synthesizes oblique when the family has no true italic face,
// so this works with any guifont (neovide does the same via DWRITE_FONT_SIMULATED_ITALIC).
private Microsoft.Graphics.Canvas.Text.CanvasTextFormat Tf(bool wide, bool italic, bool bold)
{
    int si = StyleIdx(italic, bold);
    if (si == 0) return wide ? _tfWide! : _tfNarrow!;
    string key = (wide ? "W:" + _wideFont + "@" + _wideSize : "N:" + _narrowFont + "@" + _narrowSize) + "|" + si;
    if (!_tfStyleCache.TryGetValue(key, out var tf))
    {
        tf = MakeTf(wide ? _wideFont + "@" + _wideSize : _narrowFont + "@" + _narrowSize, italic, bold);
        _tfStyleCache[key] = tf;
    }
    return tf;
}

private static Microsoft.Graphics.Canvas.Text.CanvasTextFormat MakeTf(string key, bool italic, bool bold)
{
    var tf = new Microsoft.Graphics.Canvas.Text.CanvasTextFormat();
    // DirectWrite takes a single family name (no comma lists); its automatic font fallback covers
    // CJK/emoji/symbols the primary lacks, so use just the first family of the parsed chain.
    int at = key.IndexOf('@');
    string fam = (at >= 0 ? key.Substring(0, at) : key).Split(',')[0].Trim();
    if (!string.IsNullOrEmpty(fam)) tf.FontFamily = fam;
    double size = 14 * PtToDip; // fallback only — the key always carries a parsed (pt->dip) size
    if (at >= 0 && double.TryParse(key.Substring(at + 1), out var s)) size = s;
    tf.FontSize = (float)size;
    // Request Italic (not Oblique): Maple Mono's italic file is registered with subfamily "Italic",
    // so an Oblique request misses the real face and DirectWrite falls back to a synthetic slant.
    tf.FontStyle = italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;
    // WinRT's FontWeight has no Bold/Normal statics — use the numeric weights (400 normal, 700 bold).
    tf.FontWeight = new Windows.UI.Text.FontWeight((ushort)(bold ? 700 : 400));
    return tf;
}

// Resolve a cell's text style from its highlight (no-op when the cell has no hl).
private void HlStyle(int hlId, out bool italic, out bool bold, out bool underline, out bool undercurl, out bool underdouble, out bool strike, out bool dim)
{
    italic = false; bold = false; underline = false; undercurl = false; underdouble = false; strike = false; dim = false;
    if (_hlDefs.TryGetValue(hlId, out var h)) { italic = h.Italic; bold = h.Bold; underline = h.Underline; undercurl = h.UnderCurl; underdouble = h.UnderDouble; strike = h.StrikeThrough; dim = h.Dim; }
}

// 'dim' terminal semantics: blend the foreground 50% toward the background.
private static Color DimToward(Color fg, Color bg) => Color.FromArgb(0xFF, (byte)((fg.R + bg.R) / 2), (byte)((fg.G + bg.G) / 2), (byte)((fg.B + bg.B) / 2));

// Underline / undercurl / underdouble / strikethrough for one text run [x, x+w] in a row at rowTopY with height rh.
private void DrawDecorations(Microsoft.Graphics.Canvas.CanvasDrawingSession ds, Microsoft.Graphics.Canvas.ICanvasResourceCreator rc, bool underline, bool undercurl, bool underdouble, bool strike, float x, float w, double rowTopY, double rh, Color fg)
{
    if (!underline && !undercurl && !underdouble && !strike) return;
    var brush = GetW2dBrush(rc, fg);
    const float th = 1.0f; // device-independent px line thickness (snaps to ~1 device px at 96dpi)
    if (underline || undercurl || underdouble)
    {
        float y = (float)(rowTopY + rh * 0.84); // just above the cell bottom, like neovide's underline slot
        if (undercurl)
        {
            // Sine wave: ~6 DIP period, ±0.75 DIP amplitude — reads as a "wavy" underline at any run width.
            var pb = new Microsoft.Graphics.Canvas.Geometry.CanvasPathBuilder(rc);
            pb.BeginFigure(new System.Numerics.Vector2(x, y));
            float px = x;
            while (px < x + w - 1)
            {
                float nx = Math.Min(px + 2f, x + w);
                pb.AddLine(new System.Numerics.Vector2(nx, y + (float)(Math.Sin((nx - x) / 6.0 * 2 * Math.PI) * 0.75)));
                px = nx;
            }
            pb.EndFigure(Microsoft.Graphics.Canvas.Geometry.CanvasFigureLoop.Open);
            using var geo = Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreatePath(pb);
            ds.DrawGeometry(geo, 0f, 0f, brush, th);
        }
        else if (underdouble)
        {
            // Two parallel lines ~2.5 DIP apart (neovide-style double underline).
            ds.DrawLine(x, y - 1.5f, x + w, y - 1.5f, brush, th);
            ds.DrawLine(x, y + 1.0f, x + w, y + 1.0f, brush, th);
        }
        else
            ds.DrawLine(x, y, x + w, y, brush, th);
    }
    if (strike)
    {
        float y = (float)(rowTopY + rh * 0.52); // mid-x-height band
        ds.DrawLine(x, y, x + w, y, brush, th);
    }
}

// The GPU render: clear, then one pass for backgrounds (merged rects) and one for text
// (consecutive same-color cells batched into single DrawText runs). Runs on the UI thread.
private void OnGlyphCanvasDraw(Microsoft.Graphics.Canvas.UI.Xaml.CanvasControl sender, Microsoft.Graphics.Canvas.UI.Xaml.CanvasDrawEventArgs args)
{
    try { RenderCore(args.DrawingSession, sender); }
    catch (Exception ex) { LogCritical("DRAW EXCEPTION: " + ex.GetType().Name + ": " + ex.Message); }
}

// When a floating window (:help, completion, terminal-in-float...) is up, the underlying parent
// layer gets a Gaussian blur so the float reads as focused foreground (configurable via
// NVIM_WINUI_FLOAT_BLUR, DIP radius). 0 disables.
private double _floatBlurAmount = ParseFloatBlur();
private static double ParseFloatBlur()
{
    var v = Environment.GetEnvironmentVariable("NVIM_WINUI_FLOAT_BLUR");
    return double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d >= 0 ? d : 6.0;
}

// Shared render body — also used by the DIAG snapshot path (offscreen CanvasRenderTarget).
// cellsOverride: draw this exact buffer (base-only during the blur split) instead of compositing
// fresh. blurLayerPass: marks the recursive render of the parent layer into the offscreen target
// (skips DIAG one-shots and the overlay orchestration).
    private void RenderCore(Microsoft.Graphics.Canvas.CanvasDrawingSession ds, Microsoft.Graphics.Canvas.ICanvasResourceCreator rc, Cell[]? cellsOverride = null, bool blurLayerPass = false, bool suppressCursor = false)
{
    bool outer = cellsOverride is null && !blurLayerPass;
    try
    {
    var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
    int rows = _screenRows, cols = _screenCols;
    if (rows <= 0 || cols <= 0) return;
    // Floating window on top of the grid stack → blur the parent layer behind it, then redraw the
    // float(s) sharp. Any other state (message grid, normal splits, no overlay at all) renders as
    // one flat composite (previous behavior): the blur only ever appears WITH a visible float.
    if (outer && _multigridActive && _mgrid.Values.Any(g => MGridIsOverlay(g) && MGridHasContent(g) && g.Cols < _screenCols))
    {
        RenderBlurredBase(ds, rc);
        return;
    }
    // Measure the window's DPI scale once — needed to snap cell boundaries to whole DEVICE pixels.
    if (_dpiScale < 1.0) { try { IntPtr dh = FindWindow(null, Title); uint d = GetDpiForWindow(dh); if (d > 0) _dpiScale = d / 96.0; } catch { } }
    ds.Clear(_defBg);

    // Multigrid: composite outer frame (grid 1) + window grids into the draw buffer, and resolve
    // the per-grid cursor to outer-frame coordinates. In linegrid mode this is a no-op passthrough.
    var cells = cellsOverride ?? BuildRenderCells();
    _activeRenderCells = cells;

    int curRow = _curLocalRow, curCol = _curLocalCol;
    if (_multigridActive) MGridResolveCursor(_curGridId, _curLocalRow, _curLocalCol, out curRow, out curCol);
    // Clamp: a stale cursor row beyond the current grid (e.g. after a shrink before nvim's next
    // cursor_position) would make curIdx land outside _cells and the block silently vanish.
    int curIdx = (curRow >= 0 && curRow < rows) ? curRow * cols + Math.Clamp(curCol, 0, cols - 1) : -1;
    if (suppressCursor) curIdx = -1;
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
    if (outer && _diagEnabled && Interlocked.Increment(ref _rowLogCount) % 50 == 1)
    {
        var sbr2 = new System.Text.StringBuilder();
        for (int r = 0; r < Math.Min(6, rows); r++) sbr2.Append($"r{r}:[{rowTop[r]:F2},{rowTop[r+1]-rowTop[r]:F2}] ");
        LogStartup($"ROWTOP cellH={_cellH:F4} dpi={dpi:F3} {sbr2}");
    }
    // Horizontal: NO snap. Batched runs advance glyphs at the font's true fractional cell (8.8px);
    // snapping colLeft to whole pixels would make the cursor's split-out run + suffix land at the
    // ROUNDED boundary while the cursor-off line sits at c*cellW — a ±0.5px jump of the glyph the
    // cursor touches and everything after it. Cartesian: text, fills, wide/skew and the cursor
    // block ALL share the exact c*cellW space, so run splits never move anything. Fills crossing a
    // color boundary keep an AA hairline there, which is wanted; interior seams are vertical-only
    // and remain eliminated by the rowTop merge below.
    var colLeft = new double[cols + 1];
    for (int c = 0; c <= cols; c++) colLeft[c] = c * _cellW;

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
            if (outer && _diagEnabled)
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
    if (outer && _diagEnabled && !_advDiagLogged)
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
            // Kerning probe: if 'AV'/'WA'/'LO' layout width < sum of the two solo glyph advances the
            // font pairs kern, and ANY run split (the cursor cell gets its own run) loses the pair —
            // the gap grows ("余白が増えて") and the glyphs after the cursor shift. Monospace fonts
            // usually have no kerning; this tells us which behavior drives the cursor-row artifact.
            { string[] pairs = { "AV", "WA", "AT", "LO", "HE", "TA", "LY" };
              foreach (var p in pairs)
              {
                  using var lp = new Microsoft.Graphics.Canvas.Text.CanvasTextLayout(rc, p, _tfNarrow!, 5000, 0);
                  double pairW = lp.LayoutBounds.Width;
                  double solo = 0;
                  foreach (var ch in p)
                  {
                      using var ls = new Microsoft.Graphics.Canvas.Text.CanvasTextLayout(rc, ch.ToString(), _tfNarrow!, 5000, 0);
                      solo += ls.LayoutBounds.Width;
                  }
                  LogStartup($"KERN-PAIR '{p}' pairW={pairW:F2} soloSum={solo:F2} diff={pairW - solo:F2}px {(Math.Abs(pairW - solo) > 0.05 ? ">> KERNED <<" : "mono")}");
              }
              // True per-glyph advance: diff of 10-char vs 20-char layout width (side bearings cancel).
              using (var l10 = new Microsoft.Graphics.Canvas.Text.CanvasTextLayout(rc, new string('0', 10), _tfNarrow!, 5000, 0))
              using (var l20 = new Microsoft.Graphics.Canvas.Text.CanvasTextLayout(rc, new string('0', 20), _tfNarrow!, 5000, 0))
              {
                  double a10 = l10.LayoutBounds.Width, a20 = l20.LayoutBounds.Width;
                  LogStartup($"ADV-TRUE 10ch={a10:F2} 20ch={a20:F2} perGlyph={(a20 - a10) / 10.0:F4} cellW={_cellW:F2} match={Math.Abs((a20 - a10) / 10.0 - _cellW):F3}px");
              }
            }
            // Vertical: this block reported the OLD line-box centering misalignment and is now moot —
            // yNarrow/yWide compensate via _liftNarrow/_liftWide (INK-LIFT logs the actual centers).
        }
        catch (Exception ex) { LogStartup("GLYPH-ADV probe failed: " + ex.Message); }
    }

    // One-shot DIAG probe: which cells take the color-emoji path and, if so, how the emoji font
    // sizes into the cell (fit width vs the raw ~1.5-2.5-cell bleed the old monochrome fallback had).
    if (outer && _diagEnabled && !_emojiDiagLogged)
    {
        _emojiDiagLogged = true;
        try
        {
            foreach (var sc in new[] { "✅", "✔", "⚠", "⚠️", "⭐", "★", "❤", "😀", "❌" })
            {
                int cp = FirstCodePoint(sc);
                bool wide = IsWideGlyph(sc);
                float rhD = (float)(_cellH > 0 ? (rowTop.Length > 1 ? rowTop[1] - rowTop[0] : _cellH) : _cellH);
                float sizeN = EmojiNaturalSize(sc, rhD);
                float advN = EmojiAdvance(sc, sizeN);
                const float blankMarginD = 1.0f;
                int occN = 1;
                while (occN < cols && occN < 40 && colLeft[occN] < advN + blankMarginD) occN++;
                LogStartup($"EMOJI-DIAG '{sc}' U+{cp:X4} emoji={IsEmojiPresentation(sc)} wide={wide} inkW={advN:F2}px @size={sizeN:F2}R -> blank to inkEnd+1px = {occN} cells reserved");
            }
        }
        catch (Exception ex) { LogStartup("EMOJI-DIAG probe failed: " + ex.Message); }
    }

    // One-shot DIAG snapshot: render the emoji cells into an OFFSCREEN render target (same formats
    // and centering as the live path) and save a PNG so the actual pixels — color vs monochrome
    // outline — can be inspected. COLR/CPAL layers only appear if the drawing stack honors them.
    if (outer && _diagEnabled && !_emojiShotLogged0)
    {
        _emojiShotLogged0 = true;
        try
        {
            double rhS = rowTop.Length > 1 ? rowTop[1] - rowTop[0] : _cellH;
            var rt = new Microsoft.Graphics.Canvas.CanvasRenderTarget(rc, 240, 130, 96);
            using (var ds2 = rt.CreateDrawingSession())
            {
                ds2.Clear(Windows.UI.Color.FromArgb(255, 60, 60, 60));
                float x = 12;
                foreach (var sc in new[] { "✅", "⚠️", "😀", "★" })
                {
                    bool w = IsWideGlyph(sc);
                    bool em = IsEmojiPresentation(sc);
                    float size = em ? EmojiNaturalSize(sc, (float)rhS) : (float)Math.Min(w ? 2 * _cellW : _cellW, rhS);
                    float lift = em ? EmojiLift(sc, size) : (w ? (float)_liftWide : (float)GlyphLiftN(sc));
                    float y = (float)(rhS / 2 - lift) + 12;
                    // Use the STRING DrawText overload (exactly what the live path does) — the
                    // DrawTextLayout overload does not consult the format's EnableColorFont option.
                    if (em) ds2.DrawText(sc, x, y, GetW2dBrush(rt, Windows.UI.Color.FromArgb(255, 255, 255, 255)), EmojiTf(size));
                    else ds2.DrawText(sc, x, y, GetW2dBrush(rt, Windows.UI.Color.FromArgb(255, 255, 255, 255)), w ? _tfWide! : _tfNarrow!);
                    x += 56;
                }
                // Probe band: an overwide symbol (★, deferred pass) followed immediately by text "OK".
                // The deferred ink must be clipped at the first real-text cell so 'O' stays intact.
                // Star and text go to SEPARATE y-bands so the clip can be measured independently:
                // the star's band must show NO ink right of the clip boundary (12+cellW), and the
                // OK band must show 'O' starting exactly at its own cell.
                float yB = (float)(rhS / 2 - GlyphLiftN("★")) + 44;
                float yT = (float)(rhS / 2 - _liftNarrow) + 64;
                ds2.DrawText("OK", 12 + (float)_cellW, yT, GetW2dBrush(rt, Windows.UI.Color.FromArgb(255, 255, 255, 255)), _tfNarrow!);
                float clipX = 12 + (float)_cellW;
                using (ds2.CreateLayer(1.0f, new Windows.Foundation.Rect(0, 0, clipX, 20000)))
                    ds2.DrawText("★", 12, yB, GetW2dBrush(rt, Windows.UI.Color.FromArgb(255, 255, 255, 255)), _tfNarrow!);
                // Repro band C (EmojiCells allocation): emoji cell + tail + "OK" at cell EmojiCells.
                // The emoji gets that many cells of room -> natural size, O at 12+EmojiCells*cellW.
                float yC = (float)(rhS / 2 - EmojiLift("✅", EmojiNaturalSize("✅", (float)rhS))) + 86;
                float yCn = (float)(rhS / 2 - _liftNarrow) + 86;
                ds2.DrawText("✅", 12, yC, GetW2dBrush(rt, Windows.UI.Color.FromArgb(255, 255, 255, 255)), EmojiTf(EmojiNaturalSize("✅", (float)rhS)));
                ds2.DrawText("OK", 12 + EmojiCells * (float)_cellW, yCn, GetW2dBrush(rt, Windows.UI.Color.FromArgb(255, 255, 255, 255)), _tfNarrow!);
                // Repro band D (width-1 tight allocation): " " at cell1, "O" at cell2 (17.6px).
                // No room for the natural ink -> the emoji shrinks to fit 2 cells, O stays visible.
                float availD = 2 * (float)_cellW;
                float sizeD = Math.Max(3f, EmojiNaturalSize("✅", (float)rhS) * availD / EmojiAdvance("✅", EmojiNaturalSize("✅", (float)rhS)));
                float yD = (float)(rhS / 2 - EmojiLift("✅", sizeD)) + 108;
                float yDn = (float)(rhS / 2 - _liftNarrow) + 108;
                ds2.DrawText("✅", 12, yD, GetW2dBrush(rt, Windows.UI.Color.FromArgb(255, 255, 255, 255)), EmojiTf(sizeD));
                ds2.DrawText("OK", 12 + availD, yDn, GetW2dBrush(rt, Windows.UI.Color.FromArgb(255, 255, 255, 255)), _tfNarrow!);
            }
            string shotPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NvimWinUIGui", "emojid.png");
            _ = SaveRtAsync(rt, shotPath);
            LogStartup("EMOJI-SHOT queued -> " + shotPath);
        }
        catch (Exception ex) { LogStartup("EMOJI-SHOT failed: " + ex.Message); }
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
    var skewPending = new System.Collections.Generic.List<(string Text, float X, float Y, int Fg, float ClipX, bool Italic, bool Bold, bool Ul, bool Uc, bool Ud, bool St, double RowTop, double Rh)>();
    for (int r = 0; r < rows; r++)
    {
        double rh = rowTop[r + 1] - rowTop[r]; // this row's pixel height (device-px snapped)
        float yNarrow = (float)(rowTop[r] + rh / 2 - _liftNarrow); // both fonts aligned on ink center
        float yWide   = (float)(rowTop[r] + rh / 2 - _liftWide);
        if (outer && _diagEnabled && r == curRow)
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
            HlStyle(cell.Hl, out var runIt, out var runBd, out var runUl, out var runUc, out var runUd, out var runSt, out var runDim); // style of the lead cell
            Color fg;
            if (isCur && _cursorShape == "block") fg = _defBg; // inverted cursor: default bg as glyph color (bar/underline keep normal ink)
            else if (cell.Hl >= 0 && _hlDefs.TryGetValue(cell.Hl, out var h)) fg = HlFg(h) ?? _defFg;
            else fg = _defFg;
            if (!isCur && runDim) fg = DimToward(fg, CellBg(r, c, curIdx)); // dim: blend toward the cell's bg
            int fgi = PackColor(fg);

            if (IsEmojiPresentation(txt))
            {
                // Color-emoji cell. Always draw at the NATURAL display size (ink fitted to the ROW
                // HEIGHT — no width squeezing, no shrinking for following text). Neovide shapes every
                // glyph at one font size regardless of what follows; our old "shrink to fit the room"
                // branch made an emoji whose line had trailing text render ~35% smaller than one on a
                // bare line (checkhealth: 'lspconfig:' ✅ full-size, 'OK ...' ✅ shrunk). The reserved
                // blank is sized from the ink's RIGHT EDGE: cells are reserved (rendered as space) until
                // the next column's left edge clears the ink end by a margin. If a REAL (non-blank)
                // character already sits inside the ink's reach — nvim tight-packed the emoji, e.g. a
                // 1-cell allocation — the reservation is capped at that column so the following text is
                // never swallowed; the glyph itself keeps its natural size and may overlap slightly.
                float size = EmojiNaturalSize(txt, (float)rh);
                int realCol = -1;
                for (int c2 = c + 1; c2 < cols && c2 <= c + 8; c2++)
                {
                    string t2 = cells[r * cols + c2].Text;
                    if (t2.Length == 0 || string.IsNullOrWhiteSpace(t2)) continue; // tail / blank — no ink
                    if (IsSelectorOnly(t2)) continue; // VS16/ZWJ-only cell: no ink, absorbed by this emoji
                    realCol = c2;
                    break;
                }
                // Draw at nvim's own cell allocation, natural origin: ✅/❌ occupy 2 cells, ⚠️+VS16
                // occupies 3 — the grid already carries that spacing, so no per-glyph x adjustment.
                // (A previous right-align-to-following-text pass shifted ⚠️ backwards; removed.)
                float adv = EmojiAdvance(txt, size);
                float inkEnd = (float)colLeft[c] + adv;
                const float blankMargin = 1.0f;
                int occ = 1;
                while (occ < cols - c && colLeft[c + occ] < inkEnd + blankMargin) occ++;
                if (realCol >= 0 && occ > realCol - c) occ = realCol - c; // never swallow real text
                float yEm = (float)(rowTop[r] + rh / 2 - EmojiLift(txt, size));
                if (outer && _diagEnabled && r == curRow) LogStartup($"EMOJI-CELL row={r} col={c} txt='{txt}' cp=U+{FirstCodePoint(txt):X4} inkW={adv:F2}px size={size:F2}R realCol={realCol} cells={occ} xEm={(float)colLeft[c]:F2} yEm={yEm:F2}");
                ds.DrawText(EmojiDrawText(txt), (float)colLeft[c], yEm, GetW2dBrush(rc, fg), EmojiTf(size));
                c += occ; // this cell + the reserved blank span
                continue;
            }

            if (IsWideGlyph(txt))
            {
                HlStyle(cell.Hl, out var wIt, out var wBd, out var wUl, out var wUc, out var wUd, out var wSt, out _);
                if (_diagEnabled && r == curRow) LogStartup($"WIDE-CELL row={r} col={c} txt='{txt}' cp={(int)txt[0]:X4} yWide={yWide:F2} yNarrow={yNarrow:F2}");
                ds.DrawText(txt, (float)colLeft[c], yWide, GetW2dBrush(rc, fg), Tf(true, wIt, wBd));
                float wEnd = c + 1 < cols ? (float)colLeft[c + 1] : (float)(colLeft[c] + _cellW); // wide glyph spans two cells
                DrawDecorations(ds, rc, wUl, wUc, wUd, wSt, (float)colLeft[c], (float)(wEnd - colLeft[c]), rowTop[r], rh, fg);
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
                if (outer && _diagEnabled && _skewDiagAdded.Add(cpS))
                    LogStartup($"SKEW-CELL row={r} col={c} txt='{txt}' cp=U+{cpS:X4} advCells={GlyphAdvCells(txt):F2} liftN={GlyphLiftN(txt):F2} (drawn at own cell origin)");
                float yOwn = (float)(rowTop[r] + rh / 2 - GlyphLiftN(txt));
                // DEFER to a late pass: this glyph draws ~1.5-2.5 cells of INK from a 1-cell slot, so
                // its bleed reaches into the NEXT cell. Drawn in row order, any later glyph there (a
                // filler char — e.g. the parent window's fillchars under/next to a floating window)
                // paints OVER the bleed and shears the symbol's second half. Flushing the deferred
                // list after every batched glyph makes the symbol's ink win the overlap, matching
                // float-over-parent z-order, while batched/wide/emoji text stays cell-faithful.
                // To keep the ink from EATING real text (e.g. a 'O' right after the symbol), the
                // bleed is clipped at the first following cell whose content is actual text (letters
                // and such) — only blank/space/box-drawing/fold cells may be covered by the bleed.
                float clipX = float.MaxValue;
                for (int c2 = c + 1; c2 < cols && c2 <= c + 8; c2++)
                {
                    if (!CoverableNeighbor(cells[r * cols + c2].Text)) { clipX = (float)colLeft[c2]; break; }
                }
                HlStyle(cell.Hl, out var sIt, out var sBd, out var sUl, out var sUc, out var sUd, out var sSt, out _);
                skewPending.Add((txt, (float)colLeft[c], yOwn, fgi, clipX, sIt, sBd, sUl, sUc, sUd, sSt, rowTop[r], rh));
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
                if (isCur2 && _cursorShape == "block") fg2 = _defBg;
                else if (cc2.Hl >= 0 && _hlDefs.TryGetValue(cc2.Hl, out var h2)) fg2 = HlFg(h2) ?? _defFg;
                else fg2 = _defFg;
                // A style change (italic/bold/decoration on/off) breaks the run too: one DrawText call can only
                // carry a single text format, and decorations are drawn per-run. Dim must be applied to fg2
                // EXACTLY like it was to the lead cell's fg — otherwise PackColor(fg2) != fgi for every dim
                // cell, the run breaks at its FIRST character, sb stays empty and c never advances (spin).
                HlStyle(cc2.Hl, out var it2, out var bd2, out var ul2, out var uc2, out var ud2, out var st2, out var dm2);
                if (!isCur2 && dm2) fg2 = DimToward(fg2, CellBg(r, c, curIdx));
                if (PackColor(fg2) != fgi || IsEmojiPresentation(t2) || IsWideGlyph(t2) || !IsGridAlignedNarrow(t2) || VertDeviantNarrow(t2) || (it2, bd2, ul2, uc2, ud2, st2) != (runIt, runBd, runUl, runUc, runUd, runSt)) break; // run boundary
                sb.Append(t2);
                c++;
            }
            if (sb.Length == 0) { sb.Append(cells[r * cols + c].Text); c++; } // invariant: a run always consumes its lead cell — never spin on a style mismatch at the boundary
            ds.DrawText(sb.ToString(), (float)colLeft[start], yNarrow, GetW2dBrush(rc, fg), Tf(false, runIt, runBd));
            float runEndX = c < cols ? (float)colLeft[c] : (float)(colLeft[cols - 1] + _cellW); // right edge of the last cell in the run
            DrawDecorations(ds, rc, runUl, runUc, runUd, runSt, (float)colLeft[start], (float)(runEndX - colLeft[start]), rowTop[r], rh, fg);
        }
    }

    // Pass 2b: deferred overwide glyphs. Drawn AFTER every batched/wide/emoji glyph so their
    // ~2-cell ink bleed is never painted over by whatever lives in the next cell (e.g. a
    // parent-window fillchar at a floating window's boundary). Each glyph's ink is clipped at
    // the first following TEXT cell so the bleed can widen over blank/border cells but never
    // cuts into a following letter.
    foreach (var g in skewPending)
    {
        if (g.ClipX < float.MaxValue && g.ClipX > g.X)
        {
            using (ds.CreateLayer(1.0f, new Windows.Foundation.Rect(0, 0, g.ClipX, 20000)))
                ds.DrawText(g.Text, g.X, g.Y, GetW2dBrush(rc, UnpackPacked(g.Fg)), Tf(false, g.Italic, g.Bold));
        }
        else
        {
            ds.DrawText(g.Text, g.X, g.Y, GetW2dBrush(rc, UnpackPacked(g.Fg)), Tf(false, g.Italic, g.Bold));
        }
        // Decorations follow the glyph's INK extent (clipped like its bleed), not the 1-cell slot.
        float decEnd = Math.Min(g.ClipX < float.MaxValue ? g.ClipX : (float)(g.X + _cellW * 3), (float)(g.X + _cellW * 3));
        DrawDecorations(ds, rc, g.Ul, g.Uc, g.Ud, g.St, g.X, (float)(decEnd - g.X), g.RowTop, g.Rh, UnpackPacked(g.Fg));
    }

    // Cursor bar/underline shapes (mode_info_set): a thin _defFg strip over the cursor cell, drawn
    // AFTER text so it sits on top like neovide. Thickness = cell_percentage of the dimension;
    // vertical hugs the left edge, horizontal the bottom edge. Skipped while blinked off.
    if (curIdx >= 0 && _cursorVisible && _cursorShape != "block")
    {
        int cr2 = curIdx / cols, cc2 = curIdx % cols;
        double rh2 = rowTop[cr2 + 1] - rowTop[cr2];
        float x2 = (float)colLeft[cc2], w2 = (float)_cellW;
        if (_cursorShape == "vertical")
            ds.FillRectangle(new Windows.Foundation.Rect(x2, rowTop[cr2], Math.Max(1f, w2 * _cursorCellPct / 100f), rh2), GetW2dBrush(rc, _defFg));
        else if (_cursorShape == "horizontal")
            ds.FillRectangle(new Windows.Foundation.Rect(x2, rowTop[cr2] + rh2 - Math.Max(1f, (float)rh2 * _cursorCellPct / 100f), w2, Math.Max(1f, (float)rh2 * _cursorCellPct / 100f)), GetW2dBrush(rc, _defFg));
    }

    double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    _renderMsTotal += ms; int rcc = Interlocked.Increment(ref _renderCount);

    // DIAG: full-canvas snapshot of the LIVE composite (backgrounds + text) so the actual rendered
    // pixels can be inspected without a screen capture. With NVIM_WINUI_SHOT=1 it saves every 30th
    // render (so it lands on whatever is on screen at that moment); otherwise one-shot.
    if (outer && _diagEnabled && (!_fullShotLogged0 || (Environment.GetEnvironmentVariable("NVIM_WINUI_SHOT") == "1" && rcc % 30 == 0)))
    {
        if (_fullShotLogged0) { /* keep shooting while NVIM_WINUI_SHOT=1 */ } else _fullShotLogged0 = true;
        try
        {
            float wS = (float)Math.Round(GlyphCanvas.Width), hS = (float)Math.Round(GlyphCanvas.Height);
            var rtS = new Microsoft.Graphics.Canvas.CanvasRenderTarget((Microsoft.Graphics.Canvas.ICanvasResourceCreatorWithDpi)rc, wS, hS, ((Microsoft.Graphics.Canvas.ICanvasResourceCreatorWithDpi)rc).Dpi);
            using (var dsS = rtS.CreateDrawingSession()) RenderCore(dsS, rc, cells, blurLayerPass: true, suppressCursor: false);
            string pS = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NvimWinUIGui", "fullshot.png");
            _ = SaveRtAsync(rtS, pS);
            LogStartup("FULL-SHOT queued -> " + pS);
        }
        catch (Exception ex) { LogStartup("FULL-SHOT failed: " + ex.Message); }
    }

    if (_diagEnabled && (rcc % 25 == 0 || ms > 8)) LogStartup($"RENDER #{rcc} {ms:F1}ms avg={_renderMsTotal/rcc:F1}ms cells={rows*cols}");
    }
    catch (Exception ex)
    {
        LogCritical("DRAW EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
    }
}

// Parent-layer blur when a floating/message grid is up. The base composite (outer frame + regular
// windows, i.e. everything except the overlay grids) is rasterized into an OFFSCREEN target, run
// through an RGBA Gaussian blur, and drawn as the backdrop; the overlay grids are then redrawn
// SHARP on top via RenderOverlayLayer. BlurAmount is the halo radius in DIPs — cells far from the
// float wash out, cells near it read through — giving the float a focused foreground feel.
private void RenderBlurredBase(Microsoft.Graphics.Canvas.CanvasDrawingSession ds, Microsoft.Graphics.Canvas.ICanvasResourceCreator rc)
{
    if (_floatBlurAmount <= 0) { RenderCore(ds, rc, BuildRenderCells(), blurLayerPass: true); return; } // disabled → flat composite
    var baseCells = BuildRenderCells(true);
    float w = (float)Math.Round(GlyphCanvas.Width), h = (float)Math.Round(GlyphCanvas.Height);
    if (w <= 0 || h <= 0) return;
    ds.Clear(_defBg);
    try
    {
        using (var rt = new Microsoft.Graphics.Canvas.CanvasRenderTarget((Microsoft.Graphics.Canvas.ICanvasResourceCreatorWithDpi)rc,
                                                                          w, h,
                                                                          ((Microsoft.Graphics.Canvas.ICanvasResourceCreatorWithDpi)rc).Dpi))
        {
             using (var dsv = rt.CreateDrawingSession())
                 RenderCore(dsv, rc, baseCells, blurLayerPass: true, suppressCursor: _floatBlurAmount > 0 && CursorIsInSharpLayer());
            // Draw the BLURRED parent over the WHOLE canvas, then redraw the float(s) SHARP on top
            // (RenderOverlayLayer). This is the user's reference look: the blurred parent stays
            // visible both in the float's blank interior AND outside its frame — the mask experiment
            // that hid the blur behind the float made the canvas go pitch-black outside, so it is
            // removed and the full-canvas blur restored.
            using (var blur = new GaussianBlurEffect
            {
                Source = rt,
                BlurAmount = (float)_floatBlurAmount,
                Optimization = EffectOptimization.Balanced,
            })
                ds.DrawImage(blur);
        }
        RenderOverlayLayer(ds, rc);
    }
    catch (Exception ex)
    {
        LogCritical("BLUR overlay render failed: " + ex.GetType().Name + ": " + ex.Message);
        RenderCore(ds, rc, BuildRenderCells(), blurLayerPass: true); // fallback: previous sharp composite
    }
}

// The sharp foreground: floating and message grids redrawn after the blurred parent. Each cell's
// own background (opaque or blended alpha over the blur) is filled, then its glyph is drawn with
// the correct wide/emoji/narrow path using the SAME centering math as the main pass, so a float
// looks pixel-identical to the non-blurred render. The message grid is included so the status /
// cmd line stays sharp instead of being blurred with the parent.
private void RenderOverlayLayer(Microsoft.Graphics.Canvas.CanvasDrawingSession ds, Microsoft.Graphics.Canvas.ICanvasResourceCreator rc)
{
    int rows = _screenRows, cols = _screenCols;
    double dpi = _dpiScale > 0 ? _dpiScale : 1.0;
    var rowTop = new double[rows + 1];
    for (int r = 0; r <= rows; r++) rowTop[r] = Math.Round(r * _cellH * dpi) / dpi;
    var colLeft = new double[cols + 1];
    for (int c = 0; c <= cols; c++) colLeft[c] = c * _cellW;

    foreach (var g in _mgrid.Values.OrderBy(g => g.ZIndex))
    {
        if (!MGridIsSharpLayer(g)) continue;
        if (g.Cells.Length == 0) continue; // positioned but never sized (see BuildRenderCells): nothing to draw
        for (int r = 0; r < g.Rows; r++)
        {
            int tr = g.PosRow + r;
            if (tr < 0 || tr >= rows) continue;
            double rh = rowTop[tr + 1] - rowTop[tr];
            float yNarrow = (float)(rowTop[tr] + rh / 2 - _liftNarrow);
            float yWide   = (float)(rowTop[tr] + rh / 2 - _liftWide);
            // Backgrounds: merge SAME-COLOR horizontal runs into ONE rect (the base pass does this too so
            // a float looks pixel-identical to the flat composite). Per-cell rects of fractional _cellW
            // width are AA-rasterized independently; two cells sharing a column edge then each blend ~50%
            // at that edge and the clear/base colour shows through a ~1px vertical seam — exactly the
            // "縦線が入っている" the user sees. A run also spans the 2nd (covered tail) cell of a wide
            // glyph because that tail carries the SAME highlight, so the full-width glyph ink never sits
            // on two differently-painted halves ("左と右で色が違う").
            for (int c = 0; c < g.Cols; )
            {
                int tc = g.PosCol + c;
                if (tc < 0 || tc >= cols) { c++; continue; }
                var cell = g.Cells[r * g.Cols + c];
                var rawBg = GetRawHlBg(cell);
                if (rawBg is not { } rb || rb.A == 0)
                {
                    // Transparent-bg cell: NO fill — its ink sits sharp over the blurred parent/base (this
                    // is what makes a float look FLAT, not blurry). The base pass draws ink for EVERY
                    // cell REGARDLESS of bg, so skipping a transparent cell here erased the float's
                    // BORDER and the over-blur parent text the instant a blur fired — border cells are
                    // box-drawing glyphs with A==0 bg, and the parent's own ink never came back, so the
                    // user saw no frame and an empty parent ("枠が消える・親が見えなくなる").
                    var ncp = g.Cells[r * g.Cols + c];
                    string ntp = ncp.Text;
                    if (ntp.Length > 0)
                    {
                        Color nfg = _defFg;
                        if (ncp.Hl >= 0 && _hlDefs.TryGetValue(ncp.Hl, out var nh)) nfg = HlFg(nh) ?? _defFg;
                        float yTn = (float)(rowTop[tr] + rh / 2 - _liftNarrow);
                        float yTw = (float)(rowTop[tr] + rh / 2 - _liftWide);
                        HlStyle(ncp.Hl, out var nIt, out var nBd, out var nUl, out var nUc, out var nUd, out var nSt, out var nDim);
                        if (nDim) nfg = DimToward(nfg, _defBg); // float cell over the base bg
                        if (IsEmojiPresentation(ntp))
                        {
                            float nsize = EmojiNaturalSize(ntp, (float)rh);
                            float nEm = (float)(rowTop[tr] + rh / 2 - EmojiLift(ntp, nsize));
                             ds.DrawText(EmojiDrawText(ntp), (float)colLeft[tc], nEm, GetW2dBrush(rc, nfg), EmojiTf(nsize));
                        }
                        else if (IsWideGlyph(ntp))
                            ds.DrawText(ntp, (float)colLeft[tc], yTw, GetW2dBrush(rc, nfg), Tf(true, nIt, nBd));
                        else
                            ds.DrawText(ntp, (float)colLeft[tc], yTn, GetW2dBrush(rc, nfg), Tf(false, nIt, nBd));
                        DrawDecorations(ds, rc, nUl, nUc, nUd, nSt, (float)colLeft[tc], (float)(IsWideGlyph(ntp) ? _cellW * 2 : _cellW), rowTop[tr], rh, nfg);
                    }
                    c++;
                    continue;
                }
                int ts = tc;
                int cs = c;
                do
                {
                    c++;
                    if (c >= g.Cols) break;
                    int tn = g.PosCol + c;
                    if (tn < 0 || tn >= cols) break;
                    var ncell = g.Cells[r * g.Cols + c];
                    var nbg = GetRawHlBg(ncell);
                    if (nbg is not { } nb || nb.A == 0 || nb != rb) break; // run ends at a colour edge
                } while (true);
                // c now points PAST the run (first non-matching cell, or g.Cols).
                int runEnd = c;
                int te = g.PosCol + (runEnd - 1);
                ds.FillRectangle(new Windows.Foundation.Rect(colLeft[ts], rowTop[tr], colLeft[te] - colLeft[ts] + _cellW, rh), GetW2dBrush(rc, rb));
                // Draw ink for EVERY cell in the run (not just the lead): same-bg runs carry
                // multi-char text ("hello"), and advancing c by 1 with a fill-per-iteration
                // used to stack N overlapping alpha fills and crush semi-transparent blacks
                // (lazy backdrop A=102) into opaque #010101. One fill, then all glyphs.
                for (int i = cs; i < runEnd; i++)
                {
                    int tci = g.PosCol + i;
                    if (tci < 0 || tci >= cols) continue;
                    var lead = g.Cells[r * g.Cols + i];
                    string t = lead.Text;
                    if (t.Length == 0) continue;
                    Color fg = _defFg;
                    if (lead.Hl >= 0 && _hlDefs.TryGetValue(lead.Hl, out var h)) { var lf = HlFg(h); if (lf is not null) fg = lf.Value; }
                    HlStyle(lead.Hl, out var fIt, out var fBd, out var fUl, out var fUc, out var fUd, out var fSt, out var fDim);
                    if (fDim) fg = DimToward(fg, rb); // dim: blend toward this run's bg
                    if (IsEmojiPresentation(t))
                    {
                        float fsize = EmojiNaturalSize(t, (float)rh);
                        float yEm = (float)(rowTop[tr] + rh / 2 - EmojiLift(t, fsize));
                         ds.DrawText(EmojiDrawText(t), (float)colLeft[tci], yEm, GetW2dBrush(rc, fg), EmojiTf(fsize));
                    }
                    else if (IsWideGlyph(t))
                    {
                        ds.DrawText(t, (float)colLeft[tci], yWide, GetW2dBrush(rc, fg), Tf(true, fIt, fBd));
                    }
                    else
                    {
                        // Narrow glyph: clip at the first following REAL-TEXT cell (same as base pass).
                        float clipX = float.MaxValue;
                        for (int c2 = i + 1; c2 < g.Cols && c2 <= i + 8; c2++)
                        {
                            if (!CoverableNeighbor(g.Cells[r * g.Cols + c2].Text)) { clipX = (float)colLeft[g.PosCol + c2]; break; }
                        }
                        if (clipX < float.MaxValue && clipX > colLeft[tci])
                            using (ds.CreateLayer(1.0f, new Windows.Foundation.Rect(0, 0, clipX, 20000)))
                                ds.DrawText(t, (float)colLeft[tci], yNarrow, GetW2dBrush(rc, fg), Tf(false, fIt, fBd));
                        else
                            ds.DrawText(t, (float)colLeft[tci], yNarrow, GetW2dBrush(rc, fg), Tf(false, fIt, fBd));
                    }
                    DrawDecorations(ds, rc, fUl, fUc, fUd, fSt, (float)colLeft[tci], (float)(IsWideGlyph(t) ? _cellW * 2 : _cellW), rowTop[tr], rh, fg);
                }
                 // c already points past the run — do NOT reset to cs (that re-filled the run).
            }
        }
    }

    // Sharp cursor: draw the inverted cursor block on top of the overlay (float / message)
    // so it is never blurred with the parent. When blur is off the cursor stays in the
    // base pass; suppressing it there only while a blur is active avoids a missing cursor.
    if (_floatBlurAmount > 0 && CursorIsInSharpLayer() && _cursorVisible && _mgrid.TryGetValue(_curGridId, out var cg))
    {
        int lr = _curLocalRow, lc = NvimColToAppCol(cg.Cells, cg.Cols, _curLocalRow, _curLocalCol); // nvim col -> app col (emoji spans EmojiCells)
        if (lr >= 0 && lr < cg.Rows && lc >= 0 && lc < cg.Cols && lr * cg.Cols + lc < cg.Cells.Length)
        {
            int cr = cg.PosRow + lr, cc = cg.PosCol + lc;
            if (cr >= 0 && cr < rows && cc >= 0 && cc < cols)
            {
                double rh = rowTop[cr + 1] - rowTop[cr];
                string t = cg.Cells[lr * cg.Cols + lc].Text;
                HlStyle(cg.Cells[lr * cg.Cols + lc].Hl, out var cIt, out var cBd, out var cUl, out var cUc, out var cUd, out var cSt, out _);
                if (_cursorShape == "block")
                {
                    ds.FillRectangle(new Windows.Foundation.Rect(colLeft[cc], rowTop[cr], _cellW, rh), GetW2dBrush(rc, _defFg));
                    if (t.Length > 0)
                    {
                        float yNarrow = (float)(rowTop[cr] + rh / 2 - _liftNarrow);
                        float yWide = (float)(rowTop[cr] + rh / 2 - _liftWide);
                        if (IsEmojiPresentation(t))
                        {
                            float size = EmojiNaturalSize(t, (float)rh);
                             ds.DrawText(EmojiDrawText(t), (float)colLeft[cc], (float)(rowTop[cr] + rh / 2 - EmojiLift(t, size)), GetW2dBrush(rc, _defBg), EmojiTf(size));
                        }
                        else if (IsWideGlyph(t))
                            ds.DrawText(t, (float)colLeft[cc], yWide, GetW2dBrush(rc, _defBg), Tf(true, cIt, cBd));
                        else
                            ds.DrawText(t, (float)colLeft[cc], yNarrow, GetW2dBrush(rc, _defBg), Tf(false, cIt, cBd));
                        DrawDecorations(ds, rc, cUl, cUc, cUd, cSt, (float)colLeft[cc], (float)(IsWideGlyph(t) ? _cellW * 2 : _cellW), rowTop[cr], rh, _defBg); // inverted: same color as the glyph
                    }
                }
                else if (t.Length > 0)
                {
                    // bar/underline over a float cell: normal ink + thin strip on top.
                    Color cFg = cg.Cells[lr * cg.Cols + lc].Hl >= 0 && _hlDefs.TryGetValue(cg.Cells[lr * cg.Cols + lc].Hl, out var ch) ? (HlFg(ch) ?? _defFg) : _defFg;
                    float yNarrow = (float)(rowTop[cr] + rh / 2 - _liftNarrow);
                    float yWide = (float)(rowTop[cr] + rh / 2 - _liftWide);
                    if (IsEmojiPresentation(t))
                    {
                        float size = EmojiNaturalSize(t, (float)rh);
                        ds.DrawText(EmojiDrawText(t), (float)colLeft[cc], (float)(rowTop[cr] + rh / 2 - EmojiLift(t, size)), GetW2dBrush(rc, cFg), EmojiTf(size));
                    }
                    else if (IsWideGlyph(t))
                        ds.DrawText(t, (float)colLeft[cc], yWide, GetW2dBrush(rc, cFg), Tf(true, cIt, cBd));
                    else
                        ds.DrawText(t, (float)colLeft[cc], yNarrow, GetW2dBrush(rc, cFg), Tf(false, cIt, cBd));
                    DrawDecorations(ds, rc, cUl, cUc, cUd, cSt, (float)colLeft[cc], (float)(IsWideGlyph(t) ? _cellW * 2 : _cellW), rowTop[cr], rh, cFg);
                    if (_cursorShape == "vertical")
                        ds.FillRectangle(new Windows.Foundation.Rect(colLeft[cc], rowTop[cr], Math.Max(1f, (float)_cellW * _cursorCellPct / 100f), rh), GetW2dBrush(rc, _defFg));
                    else if (_cursorShape == "horizontal")
                        ds.FillRectangle(new Windows.Foundation.Rect(colLeft[cc], rowTop[cr] + rh - Math.Max(1f, (float)rh * _cursorCellPct / 100f), (float)_cellW, Math.Max(1f, (float)rh * _cursorCellPct / 100f)), GetW2dBrush(rc, _defFg));
                }
            }
        }
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
private Color CellBg(int r, int c, int curIdx)
{
    if (r * _screenCols + c == curIdx && curIdx >= 0 && _cursorShape == "block") return _defFg; // inverted cursor: default fg as block (bar/underline shapes draw their own thin fill after text)
    var buf = _activeRenderCells ?? _cells;
    var cell = buf[r * _screenCols + c];
    if (cell.Hl >= 0 && _hlDefs.TryGetValue(cell.Hl, out var h)) { var cb = HlBg(h); if (cb is not null) return cb.Value; }
    return TransparentColor;
}
// nvim's `blend` attribute on a highlight: the percentage of the WINDOW background color to mix
// into this group's background. blend=0 -> pure hl bg (opaque), blend=100 -> pure window bg. A real
// terminal blends them, so without this step blended groups (e.g. blink.cmp / checkhealth status
// colors) render fully opaque and look brighter than in the terminal. Float windows are already
// composited by multigrid into synthetic hl ids whose Blend is 0, so they never reach here twice.
private Color BlendHlBg(Hl h)
{
    var b = h.Bg;
    if (b.A == 0 || h.Blend <= 0) return b;
    int pct = Math.Clamp(h.Blend, 0, 100);
    if (pct >= 100) return EffBg();
    Color baseC = EffBg();
    byte r = (byte)((b.R * (100 - pct) + baseC.R * pct) / 100);
    byte g = (byte)((b.G * (100 - pct) + baseC.G * pct) / 100);
    byte bl = (byte)((b.B * (100 - pct) + baseC.B * pct) / 100);
    return Color.FromArgb(0xFF, r, g, bl);
}
// Reverse-aware foreground of a highlight, or null when nvim sent no explicit fg (caller falls
// back to its default). Terminal `reverse` swaps effective fg/bg: the GLYPH takes the background
// side (explicit bg, else Normal's bg) and the FILL takes the foreground side (explicit fg, else
// Normal's fg). A pure {reverse:true} group (checkhealth.vim's healthSectionDelim) therefore draws
// dark glyphs on a Normal.fg fill — exactly what neovide renders.
private Color? HlFg(Hl h) => !h.Reverse ? (h.FgSet ? h.Fg : null) : (h.BgSet ? h.Bg : _defBg);
// Reverse-aware background of a highlight, or null when nvim sent no explicit bg and the group is
// not reversed (caller falls back to its default). Blend applies to the non-reversed path only —
// a reverse group's fill is an opaque swap color; blending it would wash out the inversion.
private Color? HlBg(Hl h) => !h.Reverse ? (h.BgSet ? BlendHlBg(h) : null) : (h.FgSet ? h.Fg : _defFg);
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

// An overwide glyph's ink may bleed over the NEXT cell only while that cell holds nothing the
// user reads as text: covered tails (""), whitespace, box-drawing borders/fillchars, and the
// "…" fold filler. Real letters must never be painted over — the bleed is clipped at the first
// such cell.
private static bool CoverableNeighbor(string t)
{
    if (t.Length == 0) return true;
    if (string.IsNullOrWhiteSpace(t)) return true;
    char ch = t[0];
    if (ch == '\u2026') return true;                 // … (folded-line fill char)
    if (ch >= '\u2500' && ch <= '\u257F') return true; // box-drawing (borders, nvim fillchars)
    return false;
}

// ---- Color emoji path ---------------------------------------------------------------
// DWrite's automatic fallback renders emoji-presentation glyphs (✅ ⚠ ❤ ⭐ ...) from Segoe UI
// Symbol as MONOCHROME at ~1.5-2.5 cells wide, so they bleed over the next cell AND come out
// black-and-white; a sequence ending in U+FE0F (⚠️) even draws a tofu box for the selector.
// Those cells belong to the equality presentation semantics (Unicode Emoji_Presentation + any
// cell carrying a variation selector / ZWJ) and Windows only renders them COLORED when the
// layout's font family actually IS an emoji font — a plain comma-chained fallback list does not
// control DWrite's resolver (MakeTf uses only the first family). So: classify emoji-presentation
// cells and draw them with a dedicated "Segoe UI Emoji" format, sized to FIT the cell — min(slot
// width, line height) — so a 1-cell emoji no longer bleeds into its neighbor (the "no space after
// ✅" overlap) and the VS16/ZWJ sequences collapse into one glyph (no tofu after ⚠️).
private static readonly (int Lo, int Hi)[] EmojiBmpRanges =
{
    (0x231A,0x231B),(0x2328,0x2328),(0x23CF,0x23CF),(0x23E9,0x23F3),(0x23F8,0x23FA),
    (0x24C2,0x24C2),(0x25AA,0x25AB),(0x25B6,0x25B6),(0x25C0,0x25C0),(0x25FB,0x25FE),
    (0x2600,0x2604),(0x260E,0x260E),(0x2611,0x2611),(0x2614,0x2615),(0x2618,0x2618),
    (0x261D,0x261D),(0x2620,0x2620),(0x2622,0x2623),(0x2626,0x2626),(0x262A,0x262A),
    (0x262E,0x262F),(0x2638,0x263A),(0x2640,0x2640),(0x2642,0x2642),(0x2648,0x2653),
    (0x265F,0x265F),(0x2660,0x2660),(0x2663,0x2663),(0x2665,0x2666),(0x2668,0x2668),
    (0x267B,0x267B),(0x267E,0x267F),(0x2692,0x2697),(0x2699,0x2699),(0x269B,0x269C),
    (0x26A0,0x26A1),(0x26A7,0x26A7),(0x26AA,0x26AB),(0x26B0,0x26B1),(0x26BD,0x26BE),
    (0x26C4,0x26C5),(0x26C8,0x26C8),(0x26CE,0x26CF),(0x26D1,0x26D1),(0x26D3,0x26D4),
    (0x26E9,0x26EA),(0x26F0,0x26F5),(0x26F7,0x26F8),(0x26F9,0x26FA),(0x26FD,0x26FD),
    (0x2702,0x2702),(0x2705,0x2705),(0x2708,0x2709),(0x270A,0x270D),(0x270F,0x270F),
    (0x2712,0x2712),(0x2714,0x2714),(0x2716,0x2716),(0x271D,0x271D),(0x2721,0x2721),
    (0x2728,0x2728),(0x2733,0x2734),(0x2744,0x2744),(0x2747,0x2747),(0x274C,0x274C),
    (0x274E,0x274E),(0x2753,0x2755),(0x2757,0x2757),(0x2763,0x2764),(0x2795,0x2797),
    (0x27A1,0x27A1),(0x27B0,0x27B0),(0x27BF,0x27BF),(0x2934,0x2935),(0x2B05,0x2B07),
    (0x2B1B,0x2B1C),(0x2B50,0x2B50),(0x2B55,0x2B55),(0x3030,0x3030),(0x303D,0x303D),
    (0x3297,0x3297),(0x3299,0x3299)
};

private static int FirstCodePoint(string s)
{
    if (s.Length == 0) return 0;
    return char.IsHighSurrogate(s[0]) && s.Length > 1 && char.IsLowSurrogate(s[1])
        ? char.ConvertToUtf32(s, 0)
        : s[0];
}

// A cell holding ONLY variation selectors / ZWJ (e.g. the U+FE0F that nvim splits off from ⚠️
// into its own grid cell) has no visible ink of its own. It must not count as "real text" in the
// emoji tight-fit scan — treating it as such shrank ⚠ to ~1/3 size (size=6.4R at rh=19) because
// the fit target was a single cell while the natural ink spans more than one cell. The preceding emoji cell
// absorbs it: EmojiDrawText appends VS16, and the reserved blank span covers this cell.
private static bool IsSelectorOnly(string s)
{
    if (s.Length == 0) return false;
    foreach (var ch in s)
        if (ch != '\uFE0F' && ch != '\uFE0E' && ch != '\u200D') return false;
    return true;
}

private static bool IsEmojiPresentation(string s)
{
    if (s.Length == 0) return false;
    int cp = FirstCodePoint(s);
    if (cp >= 0x1F000 && cp <= 0x1FAFF) return true; // astral emoji & pictographs
    foreach (var ch in s) if (ch == 0xFE0F || ch == 0x200D) return true; // explicit emoji request
    foreach (var r in EmojiBmpRanges) if (cp >= r.Lo && cp <= r.Hi) return true;
    return false;
}

private readonly Dictionary<float, Microsoft.Graphics.Canvas.Text.CanvasTextFormat> _tfEmoji = new();
private readonly Dictionary<(int cp, float size), float> _emojiLift = new();

private Microsoft.Graphics.Canvas.Text.CanvasTextFormat EmojiTf(float size)
{
    if (_tfEmoji.TryGetValue(size, out var tf)) return tf;
    // EnableColorFont: like Direct2D, Win2D draws color fonts (COLR/CPAL, e.g. Segoe UI Emoji)
    // as their MONOCHROME outline unless the format opts in to color glyph rendering. Without it
    // the emoji stays a black/white outline no matter which family the layout resolves.
    tf = new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
    {
        FontFamily = "Segoe UI Emoji",
        FontSize = size,
        // Like Direct2D, Win2D draws color fonts (COLR/CPAL, e.g. Segoe UI Emoji) as their
        // MONOCHROME outline unless the format opts in to color glyph rendering via
        // CanvasDrawTextOptions.EnableColorFont. Without it the emoji stays a black/white
        // outline no matter which family the layout resolves.
        Options = Microsoft.Graphics.Canvas.Text.CanvasDrawTextOptions.EnableColorFont
    };
    _tfEmoji[size] = tf;
    return tf;
}

private float EmojiLift(string s, float size)
{
    int cp = FirstCodePoint(s);
    // Key on (cp, size): lift is proportional to the font size (= row height), so a value cached
    // at one rh must not be reused when the grid re-sizes — that was shifting emoji by several px.
    if (_emojiLift.TryGetValue((cp, size), out var cached)) return cached;
    float lift;
    try
    {
        // Center the ACTUAL color-emoji ink (DrawBounds = tight rect of visible pixels) on the cell,
        // not the font's LayoutBounds box: for Segoe UI Emoji the layout-box center and the color
        // glyph's visual center differ by a few px, which made emoji sit slightly LOW vs normal text.
        // Normal glyphs are placed with their ink-center at rowTop+rh/2; doing the same here (with the
        // real ink) puts emoji on exactly that line. Measure EmojiDrawText(s) (VS16-appended) so we get
        // the COLOR presentation's bounds — a bare U+26A0 would measure as the narrow mono outline.
        using var l = new Microsoft.Graphics.Canvas.Text.CanvasTextLayout(GlyphCanvas, EmojiDrawText(s), EmojiTf(size), 0, 0);
        var b = l.DrawBounds;
        lift = (float)(b.Y + b.Height / 2);
    }
    catch { lift = 0.0f; } // measurement failure -> draw with layout top at the cell center line
    _emojiLift[(cp, size)] = lift;
    return lift;
}

 // Emoji display size: ~70% of the ROW HEIGHT (user preference — full row height looked too big;
 // Neovide renders its emoji at roughly this proportion). Never scale down based on DrawBounds/
 // LayoutBounds height or for following text — those include font bearings and vary wildly between
 // mono/color variants of the same codepoint, which caused per-line size differences. The caller
 // reserves blank cells for horizontal overflow via EmojiAdvance.
 private static float EmojiNaturalSize(string s, float rh) => rh * 0.7f;

 // Ensure the glyph is rendered as a COLOR emoji (not a monochrome outline).
 // Without U+FE0F (VS16), fonts like Segoe UI Emoji may pick the text/mono
 // presentation of a codepoint that also has an emoji presentation, which is
 // what happened with U+26A0 alone — a tiny black outline instead of the
 // colored glyph. Appending VS16 forces the color-emoji presentation.
 private static string EmojiDrawText(string s) => s.EndsWith("\uFE0F") ? s : s + "\uFE0F";

// Emoji ink width at a given size (the full color-glyph advance — the horizontal span of cells
// the emoji's ink covers). The trailing cells of that span are reserved as blank.
private float EmojiAdvance(string s, float size)
{
    try
    {
        using (var l = new Microsoft.Graphics.Canvas.Text.CanvasTextLayout(GlyphCanvas, s, EmojiTf(size), 5000, 0))
            return (float)l.LayoutBounds.Width;
    }
    catch { }
    return 0.0f; // measurement failure -> fall back to a single cell
}

// DIAG only: asynchronously save an offscreen render target to a PNG (Win2D has no synchronous
// image save); keep the target alive until the save completes, then dispose it.
private static async Task SaveRtAsync(Microsoft.Graphics.Canvas.CanvasRenderTarget rt, string path)
{
    try
    {
        await rt.SaveAsync(path, Microsoft.Graphics.Canvas.CanvasBitmapFileFormat.Png);
    }
    catch (Exception ex) { LogStartup("EMOJI-SHOT save failed: " + ex.Message); }
    finally { rt.Dispose(); }
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
        // ADVANCE, not ink width: LayoutBounds.Width is the INK extent and varies per glyph even in a
        // monospace font ('i'/'.' are ~85% of the cell), which made IsGridAlignedNarrow reject every
        // Latin char (|0.85-1.0| > 0.10) and scatter them onto individually-measured lifts — the
        // "height differs between characters" jitter on Maple Mono. LayoutBoundsIncludingTrailingWhitespace
        // extends to the end of the glyph's advance, so it is exactly one cell for every glyph a
        // monospace font owns; only glyphs whose ADVANCE strays (proportional fonts, fallback symbols)
        // get individual placement.
        a = (float)l.LayoutBoundsIncludingTrailingWhitespace.Width;
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
        {
            int fg = ToInt(m.TryGetValue("foreground", out var f) ? f : null);
            int bg = ToInt(m.TryGetValue("background", out var b) ? b : null);
            return new Hl(HintColor(1, fg), HintColor(2, bg),
                          ToInt(m.TryGetValue("blend", out var bl) ? bl : null),
                          m.TryGetValue("reverse", out var rv) && rv is bool rb && rb,
                          fg >= 0, bg >= 0,
                          m.TryGetValue("italic", out var it) && it is bool ib && ib,
                          m.TryGetValue("bold", out var bd) && bd is bool bb && bb,
                          m.TryGetValue("underline", out var ul) && ul is bool ub && ub,
                          m.TryGetValue("undercurl", out var uc) && uc is bool ucb && ucb,
                          m.TryGetValue("underdouble", out var ud) && ud is bool udb && udb,
                          m.TryGetValue("strikethrough", out var st) && st is bool stb && stb,
                          m.TryGetValue("dim", out var dm) && dm is bool dmb && dmb);
        }
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
// Cursor blink: steady (always on) for every mode EXCEPT "terminal", which keeps its native
// blink (blinkon/blinkoff from mode_info_set). A thread-pool timer toggles _cursorVisible and
// posts a repaint; it fires off-UI-thread, so only the bool flip + ScheduleRender run there.
private int CurrentModeIdx() => _curModeIdx;
private void ApplyCursorBlink(int mi)
{
    _blinkTimer?.Dispose(); _blinkTimer = null;
    if (mi < 0 || mi >= _modeInfos.Count) { _cursorVisible = true; return; }
    var info = _modeInfos[mi];
    // Only terminal mode blinks; all other modes are steady.
    if (!info.Name.Equals("terminal", StringComparison.OrdinalIgnoreCase)) { _cursorVisible = true; return; }
    int wait = Math.Max(0, info.BlinkWait), on = Math.Max(1, info.BlinkOn), off = Math.Max(1, info.BlinkOff);
    if (off == 0) { _cursorVisible = true; return; } // no blink params: steady
    _cursorVisible = info.BlinkStart; // begin in the "on" phase
    var t = new System.Threading.Timer(_ =>
    {
        lock (_blinkTimerLock)
        {
            if (off == 0) return;
            _cursorVisible = !_cursorVisible;
            ScheduleRender();
        }
    }, null, info.BlinkStart ? on : wait, on + off); // first flip ends the starting phase
    lock (_blinkTimerLock) { _blinkTimer?.Dispose(); _blinkTimer = t; }
}
private readonly object _blinkTimerLock = new();

private void OnClosed(object sender, object e)
{
    try { _blinkTimer?.Dispose(); } catch { }
    try { ImeDetach(); } catch { }
    try { _client?.Dispose(); } catch { }
    try { if (_nvimProc is not null && !_nvimProc.HasExited) _nvimProc.Kill(true); } catch { }
}
}
