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
using Windows.UI;
using Windows.System;
using NvimCore;

namespace NvimWinUIGui;

/// <summary>
/// PoC host window: spawns nvim --listen, connects via NvimClient, attaches the UI,
/// renders grid notifications into a TextBlock cell grid, forwards key input to nvim_input.
/// </summary>
public partial class MainWindow : Window
{
    private const string NvimPath = @"C:\Program Files\Neovim\bin\nvim.exe";

    private NvimClient? _client;
    private Process? _nvimProc;

    // Redraw protocol state.
    private int _cols = 80;
    private int _rows = 24;
    private Cell[] _cells = Array.Empty<Cell>();
    private readonly Dictionary<int, Hl> _hlDefs = new();
    private Color _defFg = Color.FromArgb(0xFF, 0xDC, 0xDC, 0xDC);
    private Color _defBg = Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E);
    private int _curRow = -1;
    private int _curCol = -1;

    private bool _renderQueued;

    public MainWindow()
    {
        InitializeComponent();
        Unloaded += OnUnloadedCleanup;
    }

    
    private sealed class Cell { public string Text = " "; public int Hl = -1; }
    private readonly record struct Hl(Color Fg, Color Bg);

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        SetStatus("spawning nvim...");
        try
        {
            int port = FindFreePort();
            var psi = new ProcessStartInfo(NvimPath, $"--listen 127.0.0.1:{port}")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            _nvimProc = Process.Start(psi);

            var client = await ConnectWithRetryAsync(port, 5);
            _client = client;
            object? info = await _client.CallAsync("nvim_get_api_info");
            SetStatus($"connected (port {port}), API v{ExtractApiMajor(info)}. attaching ui...");

            _client.OnNotification += OnNvimNotification;
            await _client.CallAsync("nvim_ui_attach", _cols, _rows, new object?[] { "rgb" });
            EnsureScreen(_rows, _cols);
            ScheduleRender();
            SetStatus($"ui attached ({_cols}x{_rows}). typing forwards to nvim.");

            await _client.CallAsync("nvim_input", ":set nosplit<CR>ihello from nvim-winui-gui<Esc>:w<CR>");
        }
        catch (Exception ex)
        {
            SetStatus($"STARTUP FAILED: {ex.Message}");
        }
    }

    private void OnNvimNotification(string method, object?[]? args)
    {
        if (!Dispatcher.HasThreadAccess)
        {
            Dispatcher.RunAsync(DispatcherQueuePriority.Normal, () => HandleNotification(method, args));
            return;
        }
        HandleNotification(method, args);
    }

    private void HandleNotification(string method, object?[]? args)
    {
        if (args is null) return;
        try
        {
            switch (method)
            {
                case "grid_resize":
                    _rows = ToInt(args[0]); _cols = ToInt(args[1]);
                    EnsureScreen(_rows, _cols);
                    ScheduleRender();
                    break;
                case "grid_line":
                {
                    int row = ToInt(args[0]);
                    int col = ToInt(args.Length > 1 ? args[1] : 0);
                    var cellsArr = args.Length > 2 && args[2] is object?[] ca ? ca : Array.Empty<object?>();
                    for (int k = 0; k < cellsArr.Length; k++)
                    {
                        if (cellsArr[k] is not string txt) continue;
                        int c = col + k;
                        if (row < 0 || row >= _rows || c < 0 || c >= _cols) continue;
                        var cell = _cells[row * _cols + c];
                        cell.Text = Widen(txt);
                        if (args.Length > 3 && args[3] is not null) cell.Hl = ToInt(args[3]);
                    }
                    ScheduleRender();
                    break;
                }
                case "cursor_position":
                    _curRow = ToInt(args[0]); _curCol = ToInt(args[1]);
                    ScheduleRender();
                    break;
                case "highlight_define":
                    if (args.Length >= 2 && args[0] is object?[] ids)
                        for (int i = 0; i < ids.Length && i + 1 < args.Length; i++)
                            _hlDefs[ToInt(ids[i])] = ParseHl(args[i + 1]);
                    ScheduleRender();
                    break;
                case "default_colors_set":
                    if (args.Length >= 2) { _defFg = HintColor(1, ToInt(args[0])); _defBg = HintColor(2, ToInt(args[1])); }
                    ScheduleRender();
                    break;
            }
        }
        catch (Exception ex)
        {
            SetStatus($"notify error: {ex.Message}");
        }
    }
    private void EnsureScreen(int rows, int cols)
    {
        if (_cells.Length == rows * cols && _screenRows == rows && _screenCols == cols) return;
        var c = new Cell[rows * cols];
        for (int i = 0; i < c.Length; i++) c[i] = new Cell();
        _cells = c;
        _screenRows = rows;
        _screenCols = cols;
    }

    private int _screenRows, _screenCols;

    private void ScheduleRender()
    {
        if (_renderQueued) return;
        _renderQueued = true;
        Dispatcher.RunAsync(DispatcherQueuePriority.Render, RenderNow);
    }

    private void RenderNow()
    {
        _renderQueued = false;
        GlyphGrid.Rows = _screenRows;
        GlyphGrid.Columns = _screenCols;
        var children = GlyphGrid.Children;
        int total = _screenRows * _screenCols;
        while (children.Count > total) children.RemoveAt(children.Count - 1);
        for (int i = 0; i < total; i++)
        {
            TextBlock t;
            if (i >= children.Count) { t = new TextBlock(); children.Add(t); }
            else t = (TextBlock)children[i];
            int r = i / _screenCols, c = i % _screenCols;
            var cell = _cells[i];
            bool isCur = r == _curRow && c == _curCol;
            t.Text = cell.Text.Length > 0 ? cell.Text : " ";
            Color fg = cell.Hl >= 0 && _hlDefs.TryGetValue(cell.Hl, out var h) ? h.Fg : _defFg;
            Color bg = cell.Hl >= 0 && _hlDefs.TryGetValue(cell.Hl, out var h2) ? h2.Bg : Colors.Transparent;
            if (isCur) { fg = _defBg; bg = _defFg; }
            t.Foreground = new SolidColorBrush(fg);
            t.Background = isCur ? new SolidColorBrush(bg) : Brushes.Transparent;
        }
    }
    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_client == null || _nvimProc == null) return;
        string? kv = MapKey(e.Key);
        if (kv != null)
        {
            e.Handled = true;
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
    };

    private static string EscapeChar(char ch) => ch < 0x20 ? $"<{((int)ch).ToString("X")}>" : ch.ToString();
    private static string Widen(string s) => s.Length > 0 ? s : " ";

    private static int ToInt(object? v) => v switch
    {
        null => -1,
        byte b => b, short s2 => s2, int i => i, long l => (int)l,
        double d => (int)d,
        _ => -1
    };

    private static int ExtractApiMajor(object? info)
    {
        if (info is object[] arr && arr.Length > 0 && arr[0] is Dictionary<string, object?> m)
            return ToInt(m.TryGetValue("version", out var v) ? v : null);
        return -1;
    }

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

    private static Hl ParseHl(object? v)
    {
        if (v is Dictionary<string, object?> m)
            return new Hl(HintColor(1, ToInt(m.TryGetValue("foreground", out var f) ? f : null)),
                          HintColor(2, ToInt(m.TryGetValue("background", out var b) ? b : null)));
        return default;
    }

    private static Color HintColor(int slot, int value)
    {
        if (value < 0 || value > 16777215)
            return slot == 1 ? Color.FromArgb(0xFF, 0xDC, 0xDC, 0xDC) : Colors.Transparent;
        byte a = (byte)(value >> 24 & 0xFF), r = (byte)(value >> 16 & 0xFF);
        byte g = (byte)(value >> 8 & 0xFF), b = (byte)(value & 0xFF);
        return Color.FromArgb(a, r, g, b);
    }

    private void SetStatus(string s) => StatusText.Text = s;

    private void OnUnloadedCleanup(object sender, RoutedEventArgs e)
    {
        try { _client?.Dispose(); } catch { }
        try { if (_nvimProc is not null && !_nvimProc.HasExited) _nvimProc.Kill(true); } catch { }
    }

}
