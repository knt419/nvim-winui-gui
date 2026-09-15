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
private int _invalidateCount; // DIAG: count Invalidate() calls (verify Draw keeps firing)

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

    // Size the canvas to exactly the grid; the ScrollViewer centers it in the content area.
    GlyphCanvas.Width = cols * _cellW;
    GlyphCanvas.Height = rows * _cellH;
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
    if (nk != _tfKeyNarrow) { _tfNarrow = MakeTf(nk); _tfKeyNarrow = nk; _natLineHNarrow = -1; LogStartup($"TF narrow={_narrowFont.Split(',')[0]}@{_narrowSize}"); }
    string wk = _wideFont + "@" + _wideSize;
    if (wk != _tfKeyWide) { _tfWide = MakeTf(wk); _tfKeyWide = wk; _natLineHWide = -1; LogStartup($"TF wide={_wideFont.Split(',')[0]}@{_wideSize}"); }
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
    try
    {
    var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
    int rows = _screenRows, cols = _screenCols;
    if (rows <= 0 || cols <= 0) return;
    var ds = args.DrawingSession;
    ds.Clear(_defBg);

    float chh = (float)_cellH;
    int curRow = _curRow, curCol = _curCol;
    int curIdx = curRow >= 0 ? curRow * cols + Math.Clamp(curCol, 0, cols - 1) : -1;

    // LineSpacing: leave at the font's natural value (-1). Pinning it to _cellH made DirectWrite
    // place the glyph at the TOP of a taller line box, so text sat high in each cell and the cursor
    // block (full cell height) appeared shifted down relative to the characters. Instead we center
    // the natural line box inside the cell via an explicit y offset below.

    // Natural line heights (ascent+descent) for vertical centering — measured once per font/size via
    // an offscreen TextBlock (same technique as MeasureRefCell). Draw runs on the UI thread.
    if (_natLineHNarrow < 0) _natLineHNarrow = MeasureNatLineH(_narrowFont, _narrowSize);
    if (_natLineHWide < 0) _natLineHWide = MeasureNatLineH(_wideFont, _wideSize);

    // Pass 1: backgrounds (highlight + inverted cursor). Consecutive same-color cells merge into
    // one rect so a full-width status line is a single DrawRectangle.
    for (int r = 0; r < rows; r++)
    {
        int runStart = -1, runKey = -2;
        for (int c = 0; c <= cols; c++)
        {
            bool hasBg = false; int key = -1;
            if (c < cols)
            {
                var cell = _cells[r * cols + c];
                Color bg;
                if (r * cols + c == curIdx && curIdx >= 0) bg = _defFg; // inverted cursor: default fg as block
                else if (cell.Hl >= 0 && _hlDefs.TryGetValue(cell.Hl, out var h)) bg = h.Bg;
                else bg = TransparentColor;
                hasBg = bg != TransparentColor; // struct equality (ReferenceEquals is always false for structs)
                key = PackColor(bg);
            }
            bool runActive = c < cols && hasBg && key == runKey;
            if (!runActive)
            {
                if (runStart >= 0) // flush the previous run
                    ds.DrawRectangle(new Windows.Foundation.Rect((float)(runStart * _cellW), (float)(r * _cellH), (float)((c - runStart) * _cellW), chh), GetW2dBrush(sender, UnpackPacked(runKey)));
                // CRITICAL: reset the open run. Without this, a transparent cell after a colored
                // one leaves runStart pointing at the old start, and every later same-color cell
                // "extends" that stale run — painting background over intervening blank cells and
                // emitting a cascade of overlapping rects (the spurious cursor-row band).
                runStart = -1;
                if (c < cols && hasBg) { runStart = c; runKey = key; } // start a new one
            }
        }
    }

    // Pass 2: text. A "run" is consecutive cells sharing the same foreground color and narrow
    // font — drawn as ONE DrawText call (the big win over per-cell XAML). Wide glyphs break the
    // run and are drawn individually with the wide format; covered tails ("" ) add no ink.
    for (int r = 0; r < rows; r++)
    {
        float yNarrow = (float)(r * _cellH + (_cellH - _natLineHNarrow) / 2); // centered natural line box
        float yWide   = (float)(r * _cellH + (_cellH - _natLineHWide) / 2);
        int c = 0;
        while (c < cols)
        {
            var cell = _cells[r * cols + c];
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
                ds.DrawText(txt, (float)(c * _cellW), yWide, GetW2dBrush(sender, fg), _tfWide!);
                c++; // the tail cell is "" and gets skipped by the loop above
                continue;
            }

            int start = c;
            var sb = new System.Text.StringBuilder();
            while (c < cols)
            {
                var cc2 = _cells[r * cols + c];
                string t2 = cc2.Text;
                if (t2.Length == 0) { c++; continue; } // covered tail: no ink, run continues
                bool isCur2 = r * cols + c == curIdx && curIdx >= 0;
                Color fg2;
                if (isCur2) fg2 = _defBg;
                else if (cc2.Hl >= 0 && _hlDefs.TryGetValue(cc2.Hl, out var h2)) fg2 = h2.Fg;
                else fg2 = _defFg;
                if (PackColor(fg2) != fgi || IsWideGlyph(t2)) break; // run boundary
                sb.Append(t2);
                c++;
            }
            ds.DrawText(sb.ToString(), (float)(start * _cellW), yNarrow, GetW2dBrush(sender, fg), _tfNarrow!);
        }
    }

    double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    _renderMsTotal += ms; int rc = Interlocked.Increment(ref _renderCount);
    if (_diagEnabled && (rc % 25 == 0 || ms > 8)) LogStartup($"RENDER #{rc} {ms:F1}ms avg={_renderMsTotal/rc:F1}ms cells={rows*cols}");
    }
    catch (Exception ex)
    {
        LogCritical("DRAW EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
    }
}

// Win2D brushes cached by packed ARGB. Must be created inside a Draw/CreateResources handler
// (they need the canvas's device), so this is only called from OnGlyphCanvasDraw.
private Microsoft.Graphics.Canvas.Brushes.ICanvasBrush GetW2dBrush(Microsoft.Graphics.Canvas.UI.Xaml.CanvasControl canvas, Color c)
{
    int key = PackColor(c);
    if (!_w2dBrushCache.TryGetValue(key, out var b))
    {
        b = new Microsoft.Graphics.Canvas.Brushes.CanvasSolidColorBrush(canvas, c);
        _w2dBrushCache[key] = b;
    }
    return b;
}

// Inverse of PackColor (for the background-run flush path).
private static Color UnpackPacked(int p) => Color.FromArgb((byte)(p >> 24), (byte)(p >> 16), (byte)(p >> 8), (byte)p);
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
           (cp >= 0x2600 && cp <= 0x27BF) ||   // Misc symbols + dingbats (nerdfont-style glyphs)
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

private static int ToInt(object? v) => v switch
    {
        null => -1,
        byte b => b, short s2 => s2, int i => i, long l => (int)l,
        double d => (int)d,
        _ => -1
    };
private static Hl ParseHl(object? v)
    {
        if (v is Dictionary<string, object?> m)
            return new Hl(HintColor(1, ToInt(m.TryGetValue("foreground", out var f) ? f : null)),
                          HintColor(2, ToInt(m.TryGetValue("background", out var b) ? b : null)));
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
