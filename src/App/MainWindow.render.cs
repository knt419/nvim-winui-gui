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
    private static readonly Color TransparentColor = default;
    // Render-diff state: layout is only re-applied when the grid shape changes, and per-cell
    // text/fg are compared against last-rendered values (RTxt/RFg on Cell) so unchanged cells
    // produce zero XAML writes. Cursor cell background is diffed via _lastCurIdx. Brushes for
    // colors are cached by packed ARGB value instead of allocating a SolidColorBrush per cell.
    private readonly Dictionary<int, SolidColorBrush> _brushCache = new();
    private int _layoutRows = 0, _layoutCols = 0;
    private bool _layoutDone = false;
    private int _lastCurIdx = -1;
    // Diagnostic counter to cap foreground logging (only logs first few cells).
    private int _diagFgCount;

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
    // Calculate the exact window size so that the content area matches the grid + status text.
    // With ExtendsContentIntoTitleBar=true (set in MainWindow ctor), the content fills the entire
    // window — there's no traditional frame border or title bar taking up space. System close/
    // minimize/maximize buttons are overlaid on top of the top-right corner of the content area,
    // matching Windows Terminal's behavior. So resizing directly to grid+status dimensions makes
    // the entire window fill exactly with the terminal content.
    const double statusTextHeight = 25;
    int width = (int)(cols * _cellW);
    int height = (int)(rows * _cellH + statusTextHeight);
    try { AppWindow.Resize(new SizeInt32(width, height)); } catch { /* ignore */ }
}
private int _schedCount;
private void ScheduleRender()
{
    if (Interlocked.Increment(ref _schedCount) % 50 == 1) LogStartup($"SCHED render count={_schedCount}");
    if (_renderQueued) return;
        _renderQueued = true;
        var ctx = _uiSyncCtx;
        if (ctx is not null && !ReferenceEquals(ctx, System.Threading.SynchronizationContext.Current))
        {
            try { ctx.Post(_ => RenderNow(), null); return; } catch { /* fall through */ }
        }
        RenderNow(); // fallback: run synchronously on current thread (safe for this PoC's non-blocking model)
    }
private void RenderNow()
{
    _renderQueued = false;
    int rows = _screenRows, cols = _screenCols;
    if (rows <= 0 || cols <= 0) return;
    // measures its content with infinite width/height. A Star (*) column under an infinite
    // constraint resolves to 0px, so the whole grid collapsed invisibly. Pixel cells give the
    // Grid a definite size that the ScrollViewer lays out and scrolls.
    // Recalculate cell sizes from the host's actual dimensions so the grid fills the window
    // whenever it resizes (manual or programmatic). Falls back to defaults if layout hasn't
    // completed yet (ActualWidth/Height = 0).
    double availW = Host.ActualWidth, availH = Host.ActualHeight;
    if (availW > 0 && cols > 0) _cellW = Math.Max(1.0, availW / cols);
    if (availH > 0 && rows > 0) _cellH = Math.Max(1.0, availH / rows);

    while (GlyphGrid.RowDefinitions.Count < rows) GlyphGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(_cellH) });
    while (GlyphGrid.RowDefinitions.Count > rows) GlyphGrid.RowDefinitions.RemoveAt(GlyphGrid.RowDefinitions.Count - 1);
    while (GlyphGrid.ColumnDefinitions.Count < cols)
        GlyphGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_cellW) });
    while (GlyphGrid.ColumnDefinitions.Count > cols) GlyphGrid.ColumnDefinitions.RemoveAt(GlyphGrid.ColumnDefinitions.Count - 1);
    // Explicitly set the grid's dimensions so the ScrollViewer always knows its size, even when
    // row/column definitions are dynamically resized. This fixes the "black screen" issue where the
    // Grid had no intrinsic size due to dynamic layout.
    GlyphGrid.Width = cols * _cellW;
    GlyphGrid.Height = rows * _cellH;
    var children = GlyphGrid.Children;
    int total = rows * cols;
    bool layoutDirty = _layoutCols != cols || _layoutRows != rows || _layoutDone == false;
    while (children.Count > total) children.RemoveAt(children.Count - 1);

    // DIFF-BASED render: walk the grid but only write XAML for cells whose text/foreground/
    // background changed since last render (RTxt/RFg/RBg on Cell + _lastCurIdx). A keystroke
    // mutates 1-2 cells, so this is O(total) comparisons with ~0 writes — vs the old full
    // re-render that set Text and allocated a SolidColorBrush for every cell each frame.
    var brushCache = _brushCache;
    int curRow = _curRow, curCol = _curCol;
    int curIdx = curRow >= 0 ? curRow * cols + Math.Clamp(curCol, 0, cols - 1) : -1;
    for (int i = 0; i < total; i++)
    {
        Border box;
        if (i >= children.Count)
        {
            var t = new TextBlock();
            // Start with narrow font/size; may be switched to wide below based on the cell's glyph.
            t.FontFamily = new FontFamily(_narrowFont);
            t.FontSize = _narrowSize;
            box = new Border { Child = t };
            children.Add(box);
        }
        else box = (Border)children[i];
        var glyph = (TextBlock)box.Child;

        var cell = _cells[i];
        int r = i / cols, c = i % cols;
        if (layoutDirty) { Grid.SetRow(box, r); Grid.SetColumn(box, c); }

        bool isCur = i == curIdx && curIdx >= 0;
        Color fg, bg;
        string txt = cell.Text.Length > 0 ? cell.Text : " ";
        // Determine whether this cell needs the wide font (CJK/Hangul/Kana). We only need to
        // update FontFamily when it changes since last render — track via Cell.RFontKey.
        bool isWideGlyph = txt.Length > 0 && IsWideChar(txt[0]);
        int desiredKey = isWideGlyph ? 1 : 0;
        int bgi = -1; // packed background key for cursor/highlight cells only
        if (isCur) { fg = _defBg; bg = _defFg; bgi = PackColor(bg); } // inverted cursor cell
        else if (cell.Hl >= 0 && _hlDefs.TryGetValue(cell.Hl, out var h))
        {
            fg = h.Fg; bg = h.Bg; bgi = PackColor(bg);
        }
        else { fg = _defFg; bg = TransparentColor; }
        int fgi = PackColor(fg);

        if (!string.Equals(cell.RTxt, txt, StringComparison.Ordinal))
        {
            glyph.Text = txt;
            cell.RTxt = txt;
            // Log the first few non-space text changes to verify they land on XAML elements
            if (txt != " " && i < 10) LogStartup($"RENDER-DIAG idx={i} row={r} col={c} txt='{txt}' glyph.Text='{glyph.Text}'");
        }
        // Diagnostic: log foreground values for first few cells to verify colors are correct
        if (i < 5)
        {
            LogStartup($"RENDER-DIAG-FG idx={i} text='{txt}' fg=(A:{fg.A},R:{fg.R},G:{fg.G},B:{bg.B}) bgi={bgi} fgi={fgi}");
        }
        if (cell.RFg != fgi)
        {
            glyph.Foreground = GetBrush(brushCache, fg);
            cell.RFg = fgi;
        }
        if (cell.RBg != bgi || i == _lastCurIdx || i == curIdx) // cursor move: flip old/new cells only
        {
            box.Background = bgi >= 0 ? GetBrush(brushCache, bg) : null;
            cell.RBg = bgi;
        }

        // Font family/size changes only when the wide/narrow classification flips.
        if (cell.RFontKey != desiredKey)
        {
            glyph.FontFamily = new FontFamily(desiredKey == 1 ? _wideFont : _narrowFont);
            glyph.FontSize = desiredKey == 1 ? _wideSize : _narrowSize;
            cell.RFontKey = desiredKey;
        }
    }
    _layoutRows = rows; _layoutCols = cols; _layoutDone = true;
    _lastCurIdx = curIdx;
}

private static SolidColorBrush GetBrush(Dictionary<int, SolidColorBrush> cache, Color c)
{
    int key = PackColor(c);
    if (!cache.TryGetValue(key, out var b))
    {
        // Bounded cache: once full we stop adding entries and fall back to fresh brushes.
        b = new SolidColorBrush(c);
        if (cache.Count < 4096) cache[key] = b;
    }
    return b;
}
// WinAppSDK 2.x's Windows.UI.Color has no PackedValue property, so pack ARGB from the
// component fields ourselves for brush-cache keys.
private static int PackColor(Color c) => (c.A << 24) | (c.R << 16) | (c.G << 8) | c.B;

// Determine if a character is "wide" (Japanese/CJK/Korean). Wide characters need the
// guifontwide font to render at the correct width in the grid.
private static bool IsWideChar(char ch)
{
    int code = (int)ch;
    return code >= 0x3040 && code <= 0x30FF ||   // Hiragana
           code >= 0x4E00 && code <= 0x9FFF ||   // CJK Unified (Chinese/Japanese/Korean radicals, ideographs)
           code >= 0xA000 && code <= 0xAFFF;     // Korean Hangul
}

private static int ToInt(object? v) => v switch
    {
        null => -1,
        byte b => b, short s2 => s2, int i => i, long l => (int)l,
        double d => (int)d,
        _ => -1
    };
private static string Widen(string s) => s.Length > 0 ? s : " ";
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
