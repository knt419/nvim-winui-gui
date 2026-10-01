using System;
using Microsoft.UI.Input; // PointerPointProperties (button state, MouseWheelDelta) — WinUI's managed shim type
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using NvimCore;

namespace NvimWinUIGui;

public partial class MainWindow
{
    // ---- Mouse -> nvim_input_mouse ---------------------------------------------------------
    // WinUI pointer events on the glyph canvas are converted to screen-cell coordinates and sent
    // as nvim_input_mouse(button, action, modifier, grid_id=0, row, col) — same scheme as
    // neovide's mouse_manager.rs: "press"/"release"/"drag" for buttons, button="wheel" with action
    // up/down for scrolling. grid_id=0 means SCREEN coordinates (not "grid 1"), which is what we
    // want with ext_multigrid on: nvim picks the window/float under the point. MouseModifiers()
    // emits neovide's order S- C- M- (D- = super, unused); nvim accepts the trailing dash and the
    // empty string (verified against 0.12.5).
    // The call is a REQUEST (nvim replies with the number of characters consumed),
    // sent fire-and-forget like key input so a slow reply can't stall pointer handling; writes
    // serialize on NvimClient's write semaphore, so ordering vs nvim_input is preserved.

    private double _mouseX = -1, _mouseY = -1; // last known pointer pos in canvas coords (wheel anchor)

    private async void OnGlyphCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Clicking the grid must not steal focus from the IME target — the canvas is Focusable,
        // so without this the IME would lose its target and stop composing. Focus AFTER the click
        // has been processed (below) as well: the XAML island grabs focus itself as a side effect
        // of the click, and measured in ime.log the next keystroke then arrived via OnKeyDown
        // instead of the IME target.
        ImeFocusTarget("grid click");
        var pt = e.GetCurrentPoint(GlyphCanvas);
        string? button = MapMouseButton(pt.Properties);
        if (button == null) return;
        _mouseX = pt.Position.X; _mouseY = pt.Position.Y;
        // Capture so drag + release keep arriving even when the pointer leaves the canvas.
        try { GlyphCanvas.CapturePointer(e.Pointer); } catch { /* already captured */ }
        await SendMouseAsync(button, "press", pt.Position);
    }

    private async void OnGlyphCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(GlyphCanvas);
        string? button = MapMouseButton(pt.Properties);
        if (button == null) return;
        _mouseX = pt.Position.X; _mouseY = pt.Position.Y;
        try { GlyphCanvas.ReleasePointerCapture(e.Pointer); } catch { /* not captured */ }
        await SendMouseAsync(button, "release", pt.Position);
    }

    private async void OnGlyphCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(GlyphCanvas);
        _mouseX = pt.Position.X; _mouseY = pt.Position.Y;
        // Only forward movement while a button is held (drag). Plain hover has no nvim equivalent.
        if (!pt.Properties.IsLeftButtonPressed && !pt.Properties.IsRightButtonPressed
            && !pt.Properties.IsMiddleButtonPressed) return;
        string? button = MapMouseButton(pt.Properties);
        if (button == null) return;
        await SendMouseAsync(button, "drag", pt.Position);
    }

    private async void OnGlyphCanvasPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        // WinUI 3 has no WheelChanged/WheelEventArgs — wheel input arrives as a pointer event.
        var pt = e.GetCurrentPoint(GlyphCanvas);
        _mouseX = pt.Position.X; _mouseY = pt.Position.Y;
        string dir = pt.Properties.MouseWheelDelta > 0 ? "up" : (pt.Properties.MouseWheelDelta < 0 ? "down" : null);
        if (dir == null) return;
        await SendMouseAsync("wheel", dir, pt.Position);
    }

    // WinUI button state -> nvim button name (neovide mapping: left/right/middle/x1/x2).
    // WinUI 3's PointerPointProperties has no X-button flags, so x1/x2 come from GetAsyncKeyState
    // (same technique as MapModifierKey for Ctrl/Alt): VK_XBUTTON1=0x05, VK_XBUTTON2=0x06.
    private static string? MapMouseButton(PointerPointProperties p)
    {
        if (p.IsLeftButtonPressed) return "left";
        if (p.IsRightButtonPressed) return "right";
        if (p.IsMiddleButtonPressed) return "middle";
        if ((GetAsyncKeyState(0x05) & 0x8000) != 0) return "x1"; // VK_XBUTTON1 (back)
        if ((GetAsyncKeyState(0x06) & 0x8000) != 0) return "x2"; // VK_XBUTTON2 (forward)
        return null;
    }

    // Live modifier keys -> nvim modifier string, neovide's order: S- C- M- (D- = super, unused).
    private static string MouseModifiers()
    {
        bool shift = (GetAsyncKeyState(0x1A) & 0x8000) != 0; // VK_SHIFT
        bool ctrl  = (GetAsyncKeyState(0x11) & 0x8000) != 0; // VK_CONTROL
        bool alt   = (GetAsyncKeyState(0x12) & 0x8000) != 0; // VK_MENU
        string m = "";
        if (shift) m += "S-";
        if (ctrl) m += "C-";
        if (alt) m += "M-";
        return m;
    }

    private async Task SendMouseAsync(string button, string action, Point pos)
    {
        if (_client == null || _nvimProc == null) return;
        // Handlers live on _root (bigger than the grid: status row below), so ignore points outside
        // the canvas — clamping would turn a click in the status bar into a click on the last row.
        double cw = _cols * _cellW, ch = _rows * _cellH;
        if (pos.X < 0 || pos.Y < 0 || pos.X >= cw || pos.Y >= ch) return;
        // Canvas coords -> grid cells. The canvas is exactly cols*_cellW x rows*_cellH (RenderNow),
        // so a plain division lands on the right cell.
        int col = Math.Clamp((int)(pos.X / _cellW), 0, Math.Max(0, _cols - 1));
        int row = Math.Clamp((int)(pos.Y / _cellH), 0, Math.Max(0, _rows - 1));
        // App columns -> nvim grid columns: an emoji spans EmojiCells app cells but only 2 in nvim's
        // own grid, so a click on the span's tail must report the glyph's starting nvim column.
        if (_cells.Length > 0 && row < _rows) col = AppColToNvimCol(_cells, _cols, row, col);
        LogStartup($"MOUSE {button} {action} pos=({pos.X:F0},{pos.Y:F0}) -> cell ({row},{col}) mod={MouseModifiers()}");
        try
        {
            // grid_id=0 = SCREEN coordinates: "0 to let Nvim decide positioning of windows"
            // (api.txt nvim_input_mouse). This is correct even though we attach WITH ext_multigrid
            // and there are many grids — 0 is not "the main grid", it means screen-space, and nvim
            // resolves it to whichever window/float is under that point. Verified against live
            // nvim 0.12.5: a click inside a split and a click inside a float each moved
            // nvim_get_current_win() to the clicked window. (Corrected 2026-09-29: this comment
            // previously claimed we attach with ext_linegrid only and have a single grid, which is
            // false — ext_multigrid is on and per-window grids exist. The CODE was right; only the
            // stated reasoning was wrong.)
            await _client.CallAsync("nvim_input_mouse", button, action, MouseModifiers(), 0, row, col);
        }
        catch (Exception ex) { SetStatus($"mouse error: {ex.Message}"); LogCritical("MOUSE RPC FAILED: " + ex.Message); }
    }
}
