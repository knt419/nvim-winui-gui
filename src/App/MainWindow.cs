using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private const string NvimPath = @"C:\Program Files\Neovim\bin\nvim.exe";

    private readonly Grid GlyphGrid;
    private readonly ScrollViewer Host;
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
    private int _curRow = -1;
    private int _curCol = -1;

    // Fonts applied from nvim's guifont/guifontwide. WinUI FontFamily has no width/weight
    // parameters, so the "wide" flag is honored by looking for a matching *Wide* family name
    // via GDI font enumeration (falls back to the base family if none exists).
    private string _narrowFont = "Cascadia Mono, Consolas";
    private double _narrowSize = 14;
    private string _wideFont = "Cascadia Mono, Consolas";
    private double _wideSize = 14;

    // Parse an nvim guifont/guifontwide setting of the form "FontName:Style:Size" into
    // family and size. Style/weight is ignored (WinUI FontFamily has no weight parameter);
    // if Size is missing, default to 14. If the string is empty/null, use defaults.
    private static void ParseNvimFont(string? setting, out string family, out double size)
    {
        if (setting == null || setting.Length == 0)
        {
            family = "Cascadia Mono, Consolas";
            size = 14;
            return;
        }
        var parts = setting.Split(':', StringSplitOptions.RemoveEmptyEntries);
        family = parts[0].Trim();
        size = 14;
        if (parts.Length >= 3 && int.TryParse(parts[2], out int sz)) size = sz;
    }

    public MainWindow()
    {
        Title = "nvim-winui-gui";
        try { AppWindow.Resize(new SizeInt32(760, 430)); } catch { }

        // Pure-C# unpackaged: Window.Dispatcher is not reliably populated here; capture the queue directly.
        _uiDq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        // Same thread, same moment: grab App.OnLaunched's dispatcher sync context for IO-thread -> UI-thread marshaling.
        _uiSyncCtx = System.Threading.SynchronizationContext.Current;

        GlyphGrid = new Grid();
        Host = new ScrollViewer { IsTabStop = false };
        Host.Content = GlyphGrid;
        StatusText = new TextBlock
        {
            Margin = new Thickness(8, 3, 8, 3),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x9A, 0xA0, 0xA6))
        };

        _root = new Grid { Background = new SolidColorBrush(_defBg) };
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(Host, 0);
        Grid.SetRow(StatusText, 1);
        _root.Children.Add(Host);
        _root.Children.Add(StatusText);

        Content = _root;
        _root.KeyDown += OnKeyDown;
        Host.Loaded += OnLoadedAsync;
        Activated += (s, e) => Host.Focus(FocusState.Programmatic);
        Closed += OnClosed;
    }

    // R*/fields cache the LAST-RENDERED state so RenderNow can skip unchanged cells entirely.
    private sealed class Cell { public string Text = " "; public int Hl = -1; public string RTxt = ""; public int RFg = -1; public int RBg = -1; public int RFontKey = 0; }
    private readonly record struct Hl(Color Fg, Color Bg);

    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_client == null || _nvimProc == null) return;
        string? kv = MapKey(e.Key);
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetKeyboardState(byte[] state);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint virt, uint scan, byte[] state, char[] buf, int cch, uint flags, IntPtr hkl);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);
}
