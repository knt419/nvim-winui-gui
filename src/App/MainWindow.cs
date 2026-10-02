using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Graphics;
using Windows.UI;
using Windows.System;
using NvimCore;

namespace NvimWinUIGui;

/// <summary>
/// PoC host window (pure C#, no XAML — this box's net472 XamlCompiler.exe dies silently,
/// so the UI is built in code and MarkupCompilePass1 is never triggered). Spawns nvim --listen,
/// connects via NvimClient, attaches the ui, renders grid notifications into a TextBlock
/// cell grid, forwards key input to nvim_input.
/// </summary>
public partial class MainWindow : Window
{
    // Resolve nvim.exe at startup instead of a hardcoded absolute path.
    // Order: NVIM_WINUI_NVIM env var (explicit override) -> PATH lookup -> default install dir.
    private static string ResolveNvimPath()
    {
        var ov = Environment.GetEnvironmentVariable("NVIM_WINUI_NVIM");
        if (!string.IsNullOrEmpty(ov) && File.Exists(ov)) return ov;

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string p = Path.Combine(dir.Trim().Trim('"'), "nvim.exe");
                if (File.Exists(p)) return p;
            }
            catch { /* skip malformed PATH entries */ }
        }

        const string fallback = @"C:\Program Files\Neovim\bin\nvim.exe";
        return File.Exists(fallback) ? fallback : "nvim.exe"; // last resort: let the OS resolve it
    }

    private readonly Microsoft.Graphics.Canvas.UI.Xaml.CanvasControl GlyphCanvas;
    private readonly TextBlock StatusText;
    private readonly Grid _root;

    private NvimClient? _client;
    private Process? _nvimProc;
    private Microsoft.UI.Dispatching.DispatcherQueue? _uiDq;
    // The dispatcher SynchronizationContext installed on the UI thread by App.OnLaunched.
    // Captured here (on that same thread) so IO-thread callbacks can Post work back to the UI thread.
    private System.Threading.SynchronizationContext? _uiSyncCtx;

    // Redraw protocol state.
    private int _cols = 80;
    private int _rows = 24;
    private Cell[] _cells = Array.Empty<Cell>();
    private readonly Dictionary<int, Hl> _hlDefs = new();
    private Color _defFg = Color.FromArgb(0xFF, 0xDC, 0xDC, 0xDC);
    private Color _defBg = Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E);

    // Effective opaque background: nvim may report a transparent default bg (A=0). Clearing the
    // swap chain / painting the root with that would expose the black window base. Fall back to a
    // fixed dark color so the surface is always fully covered.
    private Color EffBg() => _defBg.A == 0 ? Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E) : _defBg;
    private int _curRow = -1;
    private int _curCol = -1;
    // Multigrid: cursor is reported per-grid as [grid_id, row, col]; keep the grid + local coords
    // and resolve to outer-frame coordinates at render time (robust to win_pos arrival order).
    private int _curGridId = 1;
    private int _curLocalRow = -1;
    private int _curLocalCol = -1;
    // Current nvim mode name from mode_change (e.g. "normal", "cmdline", "search").
    private string _modeName = "normal";
    // Last mode_idx from mode_change — indexes into _modeInfos for cursor shape/blink.
    private int _curModeIdx = -1;

    // Cursor shape per mode, from mode_info_set: "block" / "horizontal" / "vertical".
    // cell_percentage scales the bar/underline thickness (% of the cell dimension).
    // Name is the nvim mode name (e.g. "terminal") — used to keep blink only for terminal.
    private readonly System.Collections.Generic.List<(string Name, string Shape, int Pct, int BlinkWait, int BlinkOn, int BlinkOff, bool BlinkStart)> _modeInfos = new();
    private string _cursorShape = "block";
    private int _cursorCellPct = 100;
    // Blink state: off (BlinkOff=0) means steady. Toggled by a timer on the thread pool;
    // ScheduleRender posts the repaint to the UI thread.
    private bool _cursorVisible = true;
    private System.Threading.Timer? _blinkTimer;

    // Font fallback chains. The first family is the user's guifont; the rest are system fonts that
    // supply glyphs the primary lacks, so wide/CJK/symbol code points don't render as tofu:
    //   Yu Gothic / Meiryo / MS Gothic  -> full-width CJK (Japanese)
    //   Segoe UI Emoji                  -> retried here for wide text if the wide font lacks a glyph
    //   Segoe UI Symbol                 -> box-drawing, arrows, misc symbols
    // NOTE: Emoji-presentation cells (✅ ⚠ ❤ ... and any VS16/ZWJ sequence) are NOT resolved
    // through these chains — DWrite's automatic fallback renders them as monochrome/out-of-grid.
    // They take the dedicated "Segoe UI Emoji" path in RenderCore (color, fitted to the cell).
    private const string NarrowFallback = "Cascadia Mono, Consolas, Segoe UI Symbol";
    private const string WideFallback = "Cascadia Mono, Consolas, Yu Gothic, Meiryo, MS Gothic, Segoe UI Emoji, Segoe UI Symbol";

    // Fonts applied from nvim's guifont/guifontwide. WinUI FontFamily has no width/weight
    // parameters, so the "wide" flag is honored by looking for a matching *Wide* family name
    // via GDI font enumeration (falls back to the base family if none exists).
    private string _narrowFont = NarrowFallback;
    private double _narrowSize = 14 * PtToDip;
    private string _wideFont = WideFallback;
    private double _wideSize = 14 * PtToDip;

    // nvim's guifont/guifontwide size token (e.g. "h16") is in POINTS (1/72 inch). WinUI and
    // DirectWrite FontSize are in DIPs (1/96 inch), so convert at parse time: dip = pt * 96/72.
    private const double PtToDip = 96.0 / 72.0;

    // Parse an nvim guifont/guifontwide setting into family and size. The canonical form is
    // "FontName:Style:Size" (e.g. "Cascadia Mono:hregular:12"), but the Style field may be omitted,
    // giving just "FontName:Size" (e.g. "OperatorMono Nerd Font:h16"). Size tokens are written with
    // an 'h' prefix ("h16") and we accept them in either position; a bare number is also accepted.
    // The token value is POINTS — converted to DIPs here so every downstream consumer (TextBlock,
    // CanvasTextFormat) gets the unit it expects. Style/weight itself is ignored (WinUI FontFamily
    // has no weight parameter). If no size is found, default to 14pt. Empty/null setting -> defaults.
    // The parsed family is always followed by `fallback` so CJK/emoji/symbol code points the primary
    // font lacks still render (no tofu).
    private static void ParseNvimFont(string? setting, string fallback, out string family, out double size)
    {
        if (string.IsNullOrEmpty(setting))
        {
            family = fallback; // full chain as-is (no duplication); matches field defaults -> no spurious re-render
            size = 14 * PtToDip;
            return;
        }
        var parts = setting.Split(':', StringSplitOptions.RemoveEmptyEntries);
        family = parts[0].Trim();
        size = 14 * PtToDip;
        for (int i = 1; i < parts.Length; i++)
        {
            string t = parts[i].Trim().ToLowerInvariant();
            if (t.StartsWith("h") && int.TryParse(t.Substring(1), out int hsz)) { size = hsz * PtToDip; break; } // "h16" -> 16pt
            if (int.TryParse(t, out int sz)) { size = sz * PtToDip; break; }                                   // bare "16" -> 16pt
        }
        // Append the fallback chain so CJK/emoji/symbol code points the primary font lacks
        // still render (no tofu). FontFamily ignores repeated family names.
        family += ", " + fallback;
    }

    public MainWindow()
    {
        Title = "nvim-winui-gui";
        // Taskbar/Alt-Tab icon. The exe already embeds Assets\appicon.ico (<ApplicationIcon>), but set it
        // on the AppWindow as well so the running window shows it even for a fresh/uncached exe path.
        try
        {
            string ico = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "appicon.ico");
            if (System.IO.File.Exists(ico)) AppWindow.SetIcon(ico);
        }
        catch { }
        // ExtendsContentIntoTitleBar makes the content area fill the full window (title bar region
        // becomes part of content), so Resize(width, height) directly sets the display size.
        ExtendsContentIntoTitleBar = true;
        try { AppWindow.Resize(new SizeInt32(760, 430)); } catch { }

        // Pure-C# unpackaged: Window.Dispatcher is not reliably populated here; capture the queue directly.
        _uiDq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        // Same thread, same moment: grab App.OnLaunched's dispatcher sync context for IO-thread -> UI-thread marshaling.
        _uiSyncCtx = System.Threading.SynchronizationContext.Current;

        // GPU grid: one Win2D CanvasControl replaces the old 1920-element XAML cell grid. It sits
        // DIRECTLY in row 0 of _root (not inside a ScrollViewer — DirectComposition surfaces don't
        // composite reliably under WinUI3's ScrollViewer, which left the canvas invisible). The
        // window is always sized to exactly fit the grid (UpdateWindowSize), so no scrolling is
        // needed; the canvas is centered in row 0 as a safety net for sub-pixel drift.
        // Opaque background matching the theme: the Win2D swap chain composites ON TOP of this XAML
        // background. When a floating window triggers grid_resize -> UpdateWindowSize, the swap chain
        // is recreated and for one or more frames (plus sub-pixel rounding margins) the GPU content does
        // not yet cover the full control area; with a null/transparent background the black window base
        // shows through as pure-black bands around the edges. An opaque _defBg fills those gaps with the
        // theme color instead of black. Kept in sync by the default_colors_set handler (notify.cs).
        GlyphCanvas = new Microsoft.Graphics.Canvas.UI.Xaml.CanvasControl
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(_defBg),
        };
        GlyphCanvas.Draw += OnGlyphCanvasDraw; // the GPU render (see MainWindow.render.cs)
        StatusText = new TextBlock
        {
            Margin = new Thickness(8, 3, 8, 3),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x9A, 0xA0, 0xA6))
        };
        // Layout root. The IME target is NOT a XAML child of this tree — it is a native Win32 EDIT
        // parented to the top-level HWND (see MainWindow.ime.cs), because this pure-C# build has no
        // PRI/XBF pipeline and so has no control templates to instantiate a XAML TextBox from.
        _root = new Grid { Background = new SolidColorBrush(_defBg) };
         _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
         // Fixed height for the status row so it matches exactly what UpdateWindowSize calculates.
         // Hidden by default (NVIM_WINUI_STATUSBAR=1 to show).
         _root.RowDefinitions.Add(new RowDefinition { Height = StatusBarVisible ? new GridLength(25, GridUnitType.Pixel) : new GridLength(0) });
         Grid.SetRow(GlyphCanvas, 0);
         Grid.SetRow(StatusText, 1);
         StatusText.Visibility = StatusBarVisible ? Visibility.Visible : Visibility.Collapsed;
         _root.Children.Add(GlyphCanvas);
         _root.Children.Add(StatusText);

        Content = _root;
        _root.KeyDown += OnKeyDown;
        // Mouse -> nvim_input_mouse (see MainWindow.mouse.cs). Handlers are attached to _root, NOT
        // GlyphCanvas: keyboard already proves _root receives routed input in this app, while the
        // Win2D canvas is a DirectComposition surface that may not get XAML pointer routing. Pointer
        // events bubble up the tree, so _root always sees them; GetCurrentPoint(GlyphCanvas) still
        // yields canvas-relative coords regardless of which element raised the event. MUST come
        // after `_root = new Grid` (a NRE here crashes natively in Microsoft.UI.Xaml.dll).
        _root.PointerPressed += OnGlyphCanvasPointerPressed;
        _root.PointerReleased += OnGlyphCanvasPointerReleased;
        _root.PointerMoved += OnGlyphCanvasPointerMoved;
        // PointerWheelChanged is a DIRECT event in XAML (no bubbling), so it must be attached to
        // the element under the pointer — the canvas itself. Verified: on _root it never fired.
        GlyphCanvas.PointerWheelChanged += OnGlyphCanvasPointerWheelChanged;
        // When the window resizes (or any layout pass occurs), re-render so the grid
        // recalculates its cell sizes and fills the new display area. RenderNow reads
        // _root.ActualWidth/Height at render time, so it adapts automatically to the new size.
        // Also sync the nvim-side grid: debounced (120ms) so a drag sends only one request;
        // nvim's grid_resize reply then snaps the window back to an exact cell boundary via
        // EnsureScreen -> UpdateWindowSize, which terminates the loop naturally.
        GlyphCanvas.SizeChanged += (s, e) => { ScheduleRender(); FlushRender(); ScheduleNvimResize(); };
        // The canvas has explicit Width/Height, so it does NOT resize with the window — its own
        // SizeChanged never fires on a user drag. Hook _root (fills the whole client area) instead:
        // every size change re-renders (RenderNow reads _root.Actual* and rescales cells) and
        // re-syncs the nvim grid (debounced; the equality check in SendNvimResize terminates
        // programmatic-resize loops).
        _root.SizeChanged += (s, e) => { if (_diagEnabled) LogStartup($"ROOT-SIZECHG root={_root.ActualWidth:F0}x{_root.ActualHeight:F0}"); ScheduleRender(); FlushRender(); ScheduleNvimResize(); };
        _root.Loaded += OnLoadedAsync;
        // IME: the native EDIT target needs a realized top-level HWND, so attach it once loaded and
        // hand it keyboard focus on every activation. `_root.Focus` is deliberately NOT used: the
        // EDIT is a separate top-level window, and focus must belong to it or the IME stops composing.
        _root.Loaded += (s, e) => ImeAttach();
        Activated += (s, e) => ImeFocusTarget("Activated");
        Closed += OnClosed;
        // Subclass the top-level HWND to see WM_ACTIVATEAPP. Without it a deactivation leaves no
        // trace in the log at all, so "input stopped until I clicked" has nothing to point at.
        ImeAttachActivationTrace();
    }

    // Win2D renders the whole grid every frame (GPU), so no per-cell last-rendered cache is needed.
    private sealed class Cell { public string Text = " "; public int Hl = -1; }
    // Reverse = nvim's `reverse` attribute (e.g. checkhealth.vim's healthSectionDelim sends
    // {reverse:true} with NO colors): swap fg/bg, falling back to Normal's color on any side
    // that has no explicit value — the standard terminal semantics neovide implements.
    // FgSet/BgSet record whether nvim sent an EXPLICIT color for that slot (HintColor forces an
    // opaque default for a missing foreground, so alpha alone can't tell "unset" from "set").
    private readonly record struct Hl(Color Fg, Color Bg, int Blend, bool Reverse = false, bool FgSet = false, bool BgSet = false, bool Italic = false, bool Bold = false, bool Underline = false, bool UnderCurl = false, bool UnderDouble = false, bool StrikeThrough = false, bool Dim = false);

    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // The OTHER keyboard path. When the IME host holds focus this never fires (the host is a
        // real Win32 child that takes the focus); when the XAML island holds focus instead, this
        // is what handles every key. Tracing which one runs is the only way to tell "the IME host
        // rejected the hotkey" from "the key never reached the IME host at all" -- and a Ctrl+Space
        // arriving HERE produces exactly the reported symptom (a literal space in the buffer),
        // because MapKey maps Space to a plain space with no hotkey concept at all.
        bool hostHadFocus = ImeHostHasFocus();
        ImeTrace("XAML-KEYDOWN key=" + e.Key + " composing=" + ImeIsComposing() +
                 " focusOnHost=" + hostHadFocus);
        // This path handling a key at all means the XAML island owns the keyboard, so the IME has
        // no target and cannot compose. Take the target back (measured in ime.log: after a grid
        // click focused it correctly, the next key still came here with focusOnHost=False).
        ImeReclaimFocusIfStolen();
        if (_client == null || _nvimProc == null) return;
        // While the IME is composing, keys belong to the IME (they are shaping the preedit), not
        // to nvim. Forwarding them would insert the romaji keystrokes that drive the IME as if they
        // were plain input, so swallow every key and let the EDIT's own proc feed the IME. Escape
        // falls through: it cancels the composition, and nvim should see it.
        if (ImeIsComposing() && e.Key != VirtualKey.Escape)
        {
            // Space/Enter commit; the IME turns them into WM_IME_CHAR plus the resulting synthetic
            // WM_CHAR on the EDIT, which forwards the resolved text exactly once.
            e.Handled = e.Key == VirtualKey.Space || e.Key == VirtualKey.Enter;
            return;
        }
        string? kv = MapModifierKey(e.Key) ?? MapKey(e.Key);
        // Ctrl+Space is the IME on/off hotkey, never a literal space. With the TSF host up the IME
        // normally consumes it first (this path would then see VK_PROCESSKEY, which maps to
        // nothing). If the key still arrives here, swallowing it is what keeps a stray space out of
        // the nvim buffer -- the exact symptom that was reported before TSF was wired up.
        if (_tsfHost != null && e.Key == VirtualKey.Space && (GetAsyncKeyState(0x11) & 0x8000) != 0)
        {
            ImeTrace("XAML Ctrl+Space swallowed (TSF IME hotkey)");
            e.Handled = true;
            return;
        }
        ImeTrace("XAML-SEND '" + (kv ?? "<null>") + "' for key=" + e.Key);
        if (kv != null)
        {
            e.Handled = true;
            // Fire-and-forget: the async-void handler returns to the UI loop at the await, so a
            // slow nvim reply can't stall key handling or rendering. Writes serialize on the
            // client's write semaphore (order preserved); each local TCP round-trip is ~1 ms, so
            // rapid typing just queues a few in-flight calls that drain quickly. No per-keystroke
            // coalescing: merging repeats via <N>x<N> proved lossy/off-by-one when the first send
            // had already landed (verified 2026-09-06).
            try { await _client.CallAsync("nvim_input", kv); }
            catch (Exception ex) { SetStatus($"input error: {ex.Message}"); }
        }
    }

    // Modifier combos (Ctrl/Alt). WinUI's KeyRoutedEventArgs carries no modifier state, so we poll
    // GetAsyncKeyState for the live Ctrl/Alt bits. Returns nvim notation like "<C-d>" / "<A-h>", or
    // null when no modifier is held (falls through to MapKey for plain keys). Shift is handled by
    // ToUnicodeEx inside MapKey (it already produces the shifted char), so it's not needed here.
    private static string? MapModifierKey(VirtualKey vk)
    {
        bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0; // VK_CONTROL
        bool alt  = (GetAsyncKeyState(0x12) & 0x8000) != 0; // VK_MENU
        if (!ctrl && !alt) return null;

        string baseName = vk switch
        {
            VirtualKey.A => "a", VirtualKey.B => "b", VirtualKey.C => "c",
            VirtualKey.D => "d", VirtualKey.E => "e", VirtualKey.F => "f",
            VirtualKey.G => "g", VirtualKey.H => "h", VirtualKey.I => "i",
            VirtualKey.J => "j", VirtualKey.K => "k", VirtualKey.L => "l",
            VirtualKey.M => "m", VirtualKey.N => "n", VirtualKey.O => "o",
            VirtualKey.P => "p", VirtualKey.Q => "q", VirtualKey.R => "r",
            VirtualKey.S => "s", VirtualKey.T => "t", VirtualKey.U => "u",
            VirtualKey.V => "v", VirtualKey.W => "w", VirtualKey.X => "x",
            VirtualKey.Y => "y", VirtualKey.Z => "z",
            VirtualKey.Number0 => "0", VirtualKey.Number1 => "1", VirtualKey.Number2 => "2",
            VirtualKey.Number3 => "3", VirtualKey.Number4 => "4", VirtualKey.Number5 => "5",
            VirtualKey.Number6 => "6", VirtualKey.Number7 => "7", VirtualKey.Number8 => "8",
            VirtualKey.Number9 => "9",
            VirtualKey.Left => "<Left>", VirtualKey.Right => "<Right>",
            VirtualKey.Up => "<Up>", VirtualKey.Down => "<Down>",
            _ => null
        };
        if (baseName == null) return null;

        // Ctrl takes precedence in the notation prefix order nvim expects: <C-...> then <A-...>.
        string inner = baseName;
        if (alt)  inner = "<A-" + inner + ">";
        if (ctrl) inner = "<C-" + inner + ">";
        return inner;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetKeyboardState(byte[] state);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint virt, uint scan, byte[] state, char[] buf, int cch, uint flags, IntPtr hkl);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);
}
