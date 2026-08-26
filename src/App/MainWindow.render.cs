using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using NvimCore;

namespace NvimWinUIGui;

public partial class MainWindow
{

    private int _screenRows, _screenCols;
    private bool _renderQueued;
    private static readonly Color TransparentColor = default;

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
    }
private void ScheduleRender()
    {
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
        while (GlyphGrid.RowDefinitions.Count < rows) GlyphGrid.RowDefinitions.Add(new RowDefinition());
        while (GlyphGrid.RowDefinitions.Count > rows) GlyphGrid.RowDefinitions.RemoveAt(GlyphGrid.RowDefinitions.Count - 1);
        while (GlyphGrid.ColumnDefinitions.Count < cols)
            GlyphGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        while (GlyphGrid.ColumnDefinitions.Count > cols) GlyphGrid.ColumnDefinitions.RemoveAt(GlyphGrid.ColumnDefinitions.Count - 1);
        var children = GlyphGrid.Children;
        int total = rows * cols;
        while (children.Count > total) children.RemoveAt(children.Count - 1);
        for (int i = 0; i < total; i++)
        {
            Border box;
            if (i >= children.Count)
            {
                var t = new TextBlock();
                t.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                t.FontSize = 14;
                box = new Border { Child = t };
                children.Add(box);
            }
            else box = (Border)children[i];
            var glyph = (TextBlock)box.Child;
            int r = i / cols, c = i % cols;
            Grid.SetRow(box, r);
            Grid.SetColumn(box, c);
            var cell = _cells[i];
            bool isCur = r == _curRow && c == _curCol;
            glyph.Text = cell.Text.Length > 0 ? cell.Text : " ";
            Color fg = cell.Hl >= 0 && _hlDefs.TryGetValue(cell.Hl, out var h) ? h.Fg : _defFg;
            Color bg = cell.Hl >= 0 && _hlDefs.TryGetValue(cell.Hl, out var h2) ? h2.Bg : TransparentColor;
            if (isCur) { fg = _defBg; bg = _defFg; }
            glyph.Foreground = new SolidColorBrush(fg);
            box.Background = isCur ? new SolidColorBrush(bg) : null;
        }
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
        if (value < 0 || value > 16777215)
            return slot == 1 ? Color.FromArgb(0xFF, 0xDC, 0xDC, 0xDC) : TransparentColor;
        byte a = (byte)(value >> 24 & 0xFF), r = (byte)(value >> 16 & 0xFF);
        byte g = (byte)(value >> 8 & 0xFF), b = (byte)(value & 0xFF);
        return Color.FromArgb(a, r, g, b);
    }
private void OnClosed(object sender, object e)
    {
        try { _client?.Dispose(); } catch { }
        try { if (_nvimProc is not null && !_nvimProc.HasExited) _nvimProc.Kill(true); } catch { }
    }
}
