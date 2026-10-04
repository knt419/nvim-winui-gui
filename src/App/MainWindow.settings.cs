using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml.Input;
using NvimCore;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace NvimWinUIGui;

// ---- Settings panel + the caption-strip gear button -------------------------------------------
// Why this is drawn by hand instead of built from XAML controls: this app runs without PRI/XBF
// resources, so a templated control (TextBox measured 0x80004005) cannot be constructed here. The
// panel is therefore painted by the same Win2D pass as the grid and hit-tested against rectangles
// that the draw pass publishes — the same approach the cursor, the IME preedit and the scroll
// overlay already use. No new build requirements, no new dependencies.
//
// Placement: content extends into the title bar, so the system caption buttons sit in the top-right
// of the client area and AppWindow.TitleBar.RightInset is exactly their width. The gear takes the
// strip immediately to their left, so it reads as another caption button.
public partial class MainWindow
{
    private bool _settingsOpen;
    private bool _settingsEditing;              // a Text row is in inline edit mode
    private string _settingsEditBuf = "";
    private int _settingsSel;                   // index into the SELECTABLE rows (headers skipped)
    private bool _settingsHoverBtn;
    private int _settingsHoverRow = -1;

    // Geometry published by the draw pass and consumed by the hit test. Both run on the UI thread.
    private Rect _settingsBtnRect;
    private Rect _settingsPanelRect;
    private readonly List<(Rect Rect, string Key, int Zone)> _settingsHit = new();
    private CanvasTextFormat? _tfPanel;
    private string _tfPanelKey = "";            // "<family>@<size>" the panel format was built for
    private CanvasTextFormat? _tfGear;
    private string _tfGearKey = "";
    private double _settingsNatH = -1;          // natural line height of the panel font (cached)

    private static string BaseFamily(string chain)
    {
        int i = chain.IndexOf(',');
        return (i > 0 ? chain.Substring(0, i) : chain).Trim();
    }

    // Verification only: read the layered alpha back after ApplyParentOpacity, so "the panel changed the
    // window opacity" is an observed fact rather than an assumption. Returns false when the window is
    // not layered at all — which is the expected state at alpha 1.0.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out int crKey, out byte bAlpha, out uint dwFlags);

    // ---- Font: everything in the panel follows guifont ------------------------------------------
    // The panel is drawn in the font nvim reported — the guifont family at the guifont size, the same
    // pair the grid uses, instead of a fixed 13 DIP.
    // Keyed on "<family>@<size>": RefreshGuifontAsync / option_set move _narrowFont/_narrowSize, and
    // the next draw rebuilds these formats, so a live `:set guifont=…` reaches the panel as well. The
    // floor only exists so a config asking for a very small font stays legible.
    private const double PanelMinFontSize = 10.0;
    private double PanelFontSize => Math.Max(PanelMinFontSize, _narrowSize);
    // FIRST family only — same rule as the grid's MakeTf: DirectWrite takes a single family name and
    // does NOT accept a comma list (it treats the whole string as one name, finds nothing and falls
    // back to a default font). Measured on this box: "JetBrainsMono NFM" alone draws the Nerd Font's
    // U+F423 gear, while the comma chain "JetBrainsMono NFM, Cascadia Mono, …" draws a tofu box.
    // Everything the primary family lacks (CJK, symbols) is covered by DirectWrite's own automatic
    // fallback, which is the same mechanism the grid relies on.
    private string PanelFamily => BaseFamily(_narrowFont);

    private CanvasTextFormat PanelTf()
    {
        string key = PanelFamily + "@" + PanelFontSize;
        if (_tfPanel == null || _tfPanelKey != key)
        {
            _tfPanel = new CanvasTextFormat
            {
                FontFamily = PanelFamily,
                FontSize = (float)PanelFontSize,
                WordWrapping = CanvasWordWrapping.NoWrap,
            };
            _tfPanelKey = key;
            _settingsNatH = -1;   // the cached line height belongs to the previous font
        }
        return _tfPanel;
    }

    // Measured natural line height of the panel font, keyed on "<family>@<size>" so a font change
    // invalidates it here too — not only inside PanelTf (a caller may ask for the height before any
    // draw has rebuilt the format).
    private string _natHKey = "";
    private double PanelLineH()
    {
        string key = PanelFamily + "@" + PanelFontSize;
        if (_settingsNatH < 0 || _natHKey != key)
        {
            _settingsNatH = MeasureNatLineH(PanelFamily, PanelFontSize);
            _natHKey = key;
        }
        return _settingsNatH;
    }

    // Rough monospace advance of the panel font. Used only to keep text inside the panel and to space
    // the title's two parts apart — the font is monospace, so one width per character is enough.
    private double PanelTextW(string text) => text.Length * PanelFontSize * 0.55;

    // Truncate to a pixel budget with an ellipsis (a long nvim path must not run under the value box).
    private string Fit(string text, double widthPx)
    {
        double maxChars = Math.Max(4.0, widthPx / (PanelFontSize * 0.55));
        return text.Length <= maxChars ? text : text.Substring(0, (int)maxChars - 1) + "\u2026";
    }

    // The settings glyph: U+F423, the Nerd Font (Octicons) gear — the character this button was asked
    // for. Drawn from the guifont chain, so it appears in whatever Nerd Font the user runs; the chain's
    // fallbacks still apply if the primary family lacks it. Size tracks guifont but is capped by the
    // caption strip, which is a fixed OS metric rather than a font metric.
    private const string GearGlyph = "\uF423";
    // Fallback when the family has no U+F423 (see GearGlyphAvailable).
    private const string SettingsLabel = "Settings";
    private double GearFontSize => Math.Clamp(Math.Min(CaptionStripHeight() * 0.55, PanelFontSize), 9.0, 26.0);

    // Centre-aligned: DrawText with a Rect honours both alignments, which is what centres the glyph.
    private CanvasTextFormat GearTf()
    {
        string key = PanelFamily + "@" + GearFontSize;
        if (_tfGear == null || _tfGearKey != key)
        {
            _tfGear = new CanvasTextFormat
            {
                FontFamily = PanelFamily,
                FontSize = (float)GearFontSize,
                WordWrapping = CanvasWordWrapping.NoWrap,
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Center,
            };
            _tfGearKey = key;
        }
        return _tfGear;
    }

    // Does the active family actually map U+F423? A non-Nerd-Font guifont has no such glyph and
    // DirectWrite then draws a .notdef box, so the button falls back to the word "Settings" (and grows
    // to fit it). Asked once per family through GDI: GetGlyphIndicesW with GGI_MARK_NONEXISTING_GLYPHS
    // returns 0xFFFF for a codepoint the font does not map, and an unfindable family is substituted by
    // GDI, which also has no PUA glyph — either way the answer is "not available", which is the safe
    // direction (the label always reads).
    private const uint GGI_MARK_NONEXISTING_GLYPHS = 0x0001;
    private string _gearProbeFamily = "";
    private bool _gearProbeOk;
    private bool GearGlyphAvailable()
    {
        string fam = PanelFamily;
        if (_gearProbeFamily == fam) return _gearProbeOk;
        _gearProbeFamily = fam;
        _gearProbeOk = false;
        IntPtr hdc = IntPtr.Zero, font = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            hdc = GetDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero) return _gearProbeOk;
            font = CreateFontW(0, 0, 0, 0, 400, 0, 0, 0, 1 /*DEFAULT_CHARSET*/, 0, 0, 0, 0, fam);
            if (font == IntPtr.Zero) return _gearProbeOk;
            old = SelectObject(hdc, font);
            var idx = new ushort[1];
            if (GetGlyphIndicesW(hdc, GearGlyph, 1, idx, GGI_MARK_NONEXISTING_GLYPHS) != 0xFFFFFFFFu)
                _gearProbeOk = idx[0] != 0xFFFF && idx[0] != 0;
        }
        catch { }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(hdc, old);
            if (font != IntPtr.Zero) DeleteObject(font);
            if (hdc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdc);
        }
        LogImportant($"SETTINGS-GEAR family='{fam}' U+F423 {( _gearProbeOk ? "present -> glyph" : "missing -> \"Settings\" label")}");
        return _gearProbeOk;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [System.Runtime.InteropServices.DllImport("gdi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, uint italic,
        uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetGlyphIndicesW(IntPtr hdc, string text, int count, ushort[] indices, uint flags);

    // Caption strip height: a fixed OS metric (32 DIP by default), NOT a font metric.
    private double CaptionStripHeight()
    {
        try
        {
            var tb = AppWindow.TitleBar;
            if (tb != null && tb.Height > 0) return tb.Height;
        }
        catch { }
        return 32.0;
    }

    // Fg/bg blend used for every panel colour, so the panel follows whatever theme nvim reported
    // instead of hardcoding a dark palette.
    private Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return Color.FromArgb(0xFF,
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    // Push a colour AWAY from `bg`, i.e. more contrast: toward white on a dark background, toward
    // black on a light one. The panel needs this because the theme's Normal foreground is a mid grey
    // in most colorschemes and reads dark once it sits on a translucent panel over a blurred parent.
    // Deriving the direction from the background keeps a light colorscheme legible.
    private Color Contrast(Color c, Color bg, double t)
    {
        int lum = (bg.R * 299 + bg.G * 587 + bg.B * 114) / 1000;
        Color pole = lum < 128 ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                               : Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
        return Mix(c, pole, t);
    }

    // Display order: group header rows interleaved with the settings they contain.
    private static List<(bool Header, string? Key, string Text)> SettingsDisplayRows()
    {
        var rows = new List<(bool, string?, string)>();
        string? group = null;
        foreach (var d in SettingDefs.All)
        {
            if (!string.Equals(d.Group, group, StringComparison.Ordinal))
            {
                group = d.Group;
                rows.Add((true, null, group));
            }
            rows.Add((false, d.Key, d.Label));
        }
        return rows;
    }

    private static List<string> SettingsSelectableKeys()
    {
        var keys = new List<string>(SettingDefs.All.Length);
        foreach (var d in SettingDefs.All) keys.Add(d.Key);
        return keys;
    }

    private int SelectedIndex(string key)
    {
        var keys = SettingsSelectableKeys();
        int i = keys.IndexOf(key);
        return i < 0 ? 0 : i;
    }

    // ---- Geometry -------------------------------------------------------------------------------
    // Recomputed whenever it is needed (draw AND click), so a click that lands before the first
    // frame is still hit-tested against the real strip.
    private void UpdateSettingsGeometry()
    {
        double stripH = CaptionStripHeight();
        double rightInset = 0;
        try
        {
            var tb = AppWindow.TitleBar;
            if (tb != null && tb.RightInset > 0) rightInset = tb.RightInset;
        }
        catch { /* TitleBar unavailable: fall back to the default strip */ }

        double canvasW = GlyphCanvas.Width > 0 ? GlyphCanvas.Width : Math.Max(1, _cols * _cellW);
        double canvasH = GlyphCanvas.Height > 0 ? GlyphCanvas.Height : Math.Max(1, _rows * _cellH);
        // SIGNED, not clamped at zero: the canvas is centred in the star row, and when the grid is
        // wider/taller than the content area (it can be, before nvim's grid sync settles) the canvas
        // overflows on BOTH sides, so the client area's origin sits at a negative canvas offset.
        // Clamping to 0 here would shift the button half the overflow away from the caption buttons.
        double offX = (_root.ActualWidth - canvasW) / 2;
        double offY = (_root.ActualHeight - StatusTextHeight - canvasH) / 2;

        // Glyph mode keeps a caption-button footprint; label mode widens to fit the word in the panel
        // font, so "Settings" is never clipped.
        double bw = GearGlyphAvailable()
            ? Math.Min(46, Math.Max(28, stripH + 14))
            : Math.Clamp(PanelTextW("Settings") + 18, 60, 260);
        double x = _root.ActualWidth - rightInset - bw - offX;
        double y = Math.Max(0, -offY);
        _settingsBtnRect = new Rect(x, y, bw, stripH);
    }

    private bool SettingsPointerPressed(Point p)
    {
        UpdateSettingsGeometry();
        if (_settingsBtnRect.Contains(p))
        {
            ToggleSettings();
            return true;
        }
        if (!_settingsOpen) return false;
        foreach (var h in _settingsHit)
        {
            if (!h.Rect.Contains(p)) continue;
            _settingsSel = SelectedIndex(h.Key);
            if (h.Zone != 0) StepSetting(h.Key, h.Zone);
            else if (Settings.Def(h.Key)?.Kind == SettingKind.Text) { /* click selects; Enter edits */ }
            ScheduleRender(); FlushRender();
            return true;
        }
        if (_settingsPanelRect.Contains(p)) return true;  // inside the panel, not on a control
        CloseSettings();                                  // click outside closes (and is swallowed)
        return true;
    }

    private void SettingsPointerMoved(Point p)
    {
        UpdateSettingsGeometry();
        bool hoverBtn = _settingsBtnRect.Contains(p);
        int hoverRow = -1;
        if (_settingsOpen)
        {
            for (int i = 0; i < _settingsHit.Count; i++)
                if (_settingsHit[i].Rect.Contains(p)) { hoverRow = i; break; }
        }
        if (hoverBtn == _settingsHoverBtn && hoverRow == _settingsHoverRow) return;
        _settingsHoverBtn = hoverBtn;
        _settingsHoverRow = hoverRow;
        ScheduleRender();
        FlushRender();
    }

    private void ToggleSettings()
    {
        _settingsOpen = !_settingsOpen;
        _settingsEditing = false;
        _settingsEditBuf = "";
        // Always-logged (not DIAG-gated): this is the one line that proves the gear click reached the
        // app, and it is rare by construction (one per user action).
        LogImportant($"SETTINGS-PANEL {(_settingsOpen ? "open" : "close")} sel={_settingsSel} " +
                     $"gear={(GearGlyphAvailable() ? "glyph" : "text")} file={Settings.FilePath} values={Settings.FileValueCount} btn=({_settingsBtnRect.X:F0},{_settingsBtnRect.Y:F0},{_settingsBtnRect.Width:F0}x{_settingsBtnRect.Height:F0})");
        ScheduleRender();
        FlushRender();
    }

    private void CloseSettings()
    {
        if (!_settingsOpen) return;
        _settingsOpen = false;
        _settingsEditing = false;
        LogImportant("SETTINGS-PANEL close (click outside)");
        ScheduleRender();
        FlushRender();
    }

    // ---- Painting -------------------------------------------------------------------------------
    // Called from the Draw handler AFTER the scroll overlay, so the panel is the top-most layer.
    private void DrawSettingsOverlay(CanvasDrawingSession ds, ICanvasResourceCreator rc)
    {
        UpdateSettingsGeometry();
        DrawSettingsButton(ds, rc);
        _settingsHit.Clear();
        if (!_settingsOpen) return;
        DrawSettingsPanel(ds, rc);
    }

    // The settings button: the U+F423 gear glyph, drawn from the same font as the grid (see GearTf),
    // centred in the caption strip immediately left of the system caption buttons.
    private void DrawSettingsButton(CanvasDrawingSession ds, ICanvasResourceCreator rc)
    {
        var r = _settingsBtnRect;
        if (r.Width <= 1 || r.Height <= 1) return;
        bool hot = _settingsHoverBtn || _settingsOpen;
        if (hot) ds.FillRoundedRectangle(r, 4, 4, Mix(EffBg(), _defFg, 0.18));
        // Dim while idle, full theme foreground when hovered or open. Glyph when the font has it, the
        // word "Settings" when it does not (same font, same size, centred in the same strip).
        var ink = Mix(_defFg, EffBg(), hot ? 0.0 : 0.14);
        ds.DrawText(GearGlyphAvailable() ? GearGlyph : SettingsLabel, r, ink, GearTf());
    }

    private void FillArrow(CanvasDrawingSession ds, ICanvasResourceCreator rc, Rect r, int dir, Color c)
    {
        var pb = new CanvasPathBuilder(rc);
        if (dir < 0)
        {
            pb.BeginFigure(new Vector2((float)r.X, (float)(r.Y + r.Height / 2)));
            pb.AddLine(new Vector2((float)(r.X + r.Width), (float)r.Y));
            pb.AddLine(new Vector2((float)(r.X + r.Width), (float)(r.Y + r.Height)));
        }
        else
        {
            pb.BeginFigure(new Vector2((float)(r.X + r.Width), (float)(r.Y + r.Height / 2)));
            pb.AddLine(new Vector2((float)r.X, (float)r.Y));
            pb.AddLine(new Vector2((float)r.X, (float)(r.Y + r.Height)));
        }
        pb.EndFigure(CanvasFigureLoop.Closed);
        using var tri = CanvasGeometry.CreatePath(pb);
        ds.FillGeometry(tri, c);
    }

    // Up/down variant, used for the list's scroll indicators.
    private void FillArrowV(CanvasDrawingSession ds, ICanvasResourceCreator rc, Rect r, bool up, Color c)
    {
        var pb = new CanvasPathBuilder(rc);
        if (up)
        {
            pb.BeginFigure(new Vector2((float)(r.X + r.Width / 2), (float)r.Y));
            pb.AddLine(new Vector2((float)(r.X + r.Width), (float)(r.Y + r.Height)));
            pb.AddLine(new Vector2((float)r.X, (float)(r.Y + r.Height)));
        }
        else
        {
            pb.BeginFigure(new Vector2((float)(r.X + r.Width / 2), (float)(r.Y + r.Height)));
            pb.AddLine(new Vector2((float)(r.X + r.Width), (float)r.Y));
            pb.AddLine(new Vector2((float)r.X, (float)r.Y));
        }
        pb.EndFigure(CanvasFigureLoop.Closed);
        using var tri = CanvasGeometry.CreatePath(pb);
        ds.FillGeometry(tri, c);
    }

    private void DrawSettingsPanel(CanvasDrawingSession ds, ICanvasResourceCreator rc)
    {
        double canvasW = GlyphCanvas.Width > 0 ? GlyphCanvas.Width : Math.Max(1, _cols * _cellW);
        double canvasH = GlyphCanvas.Height > 0 ? GlyphCanvas.Height : Math.Max(1, _rows * _cellH);
        var rows = SettingsDisplayRows();
        // Metrics follow the panel font, not just the text: at guifont h16 (~21 DIP) rows have to grow
        // or the labels would clip inside their own row.
        double lhm = PanelLineH();
        double rowH = Math.Ceiling(lhm) + 8, hdrH = Math.Ceiling(lhm) + 5;
        double titleH = Math.Ceiling(lhm) + 12, footerH = Math.Ceiling(lhm) * 2 + 18;
        double contentH = titleH + footerH;
        foreach (var r in rows) contentH += r.Header ? hdrH : rowH;
        // Centre on the VISIBLE part of the canvas (the canvas can overflow the container), and keep
        // the panel inside the canvas so it is never clipped by the surface edge.
        double offX = (_root.ActualWidth - canvasW) / 2;
        double visW = Math.Min(canvasW, Math.Max(1, _root.ActualWidth));
        // Width follows the font too: the longest label plus its value box has to fit at this size.
        double minW = Math.Max(300.0, Math.Min(visW - 8, lhm * 20 + 110));
        double maxW = Math.Max(minW, Math.Min(860.0, visW - 4));
        double pw = Math.Clamp(visW * 0.9, minW, maxW);
        double ph = Math.Min(contentH, Math.Max(140, canvasH - 16));
        double px = Math.Round(Math.Clamp(offX + (visW - pw) / 2, 2, Math.Max(2, canvasW - pw - 2)));
        double py = Math.Round(Math.Max(8, (canvasH - ph) / 2));
        _settingsPanelRect = new Rect(px, py, pw, ph);

        // The panel is a floating surface, so its FILLS carry the float opacity and the parent behind
        // it is blurred by the render path (RenderCore blurs while the panel is open, exactly as it
        // does for a floating window). Text, arrows and the env dot stay opaque — the same rule a
        // float follows: backgrounds fade, glyphs stay readable.
        Color bg = ScaleAlpha(Mix(EffBg(), _defFg, 0.08), _floatOpacity);
        Color border = ScaleAlpha(Mix(EffBg(), _defFg, 0.30), _floatOpacity);
        // Inks are lifted away from the background (see Contrast): panel text at 30% toward the
        // contrast pole, secondary tone (group headers, hints) only a step down from it, and unselected
        // rows barely dimmed at all — the selection already reads from the highlight bar.
        Color fg = Contrast(_defFg, EffBg(), 0.30);
        Color dim = Mix(fg, EffBg(), 0.30);
        Color hi = ScaleAlpha(Mix(EffBg(), fg, 0.18), _floatOpacity);
        var tf = PanelTf();
        double lh = lhm;

        ds.FillRoundedRectangle(_settingsPanelRect, 6, 6, bg);
        ds.DrawRoundedRectangle(_settingsPanelRect, 6, 6, border, 1);
        ds.DrawLine((float)px, (float)(py + titleH - 1), (float)(px + pw), (float)(py + titleH - 1), border, 1);
        ds.DrawText("Settings", (float)(px + 14), (float)(py + (titleH - lh) / 2), fg, tf);
        ds.DrawText(Fit("settings.json   stored: " + Settings.FileValueCount, pw - 28 - PanelTextW("Settings") - 18),
                    (float)(px + 14 + PanelTextW("Settings") + 18), (float)(py + (titleH - lh) / 2), dim, tf);

        double y = py + titleH;
        double contentBottom = py + ph - footerH;
        // The list SCROLLS: a default-sized window cannot show all 15 rows, so the slice drawn is the
        // window of items that fits around the selection, and the selected row is always inside it
        // (fill downward from the selection first, then upward with whatever space is left).
        double avail = contentBottom - y;
        int selItem = 0, seen = -1;
        for (int i = 0; i < rows.Count; i++)
            if (!rows[i].Header) { seen++; if (seen == _settingsSel) { selItem = i; break; } }
        int start = selItem;
        double used = 0;
        for (int i = selItem; i < rows.Count; i++)
        {
            double hh = rows[i].Header ? hdrH : rowH;
            if (used + hh > avail) break;
            used += hh;
        }
        while (start > 0)
        {
            double hh = rows[start - 1].Header ? hdrH : rowH;
            if (used + hh > avail) break;
            used += hh;
            start--;
        }
        int keyIdx = -1;
        for (int i = 0; i < start; i++) if (!rows[i].Header) keyIdx++;
        string selKey = "";
        int lastDrawn = start - 1;
        for (int ri = start; ri < rows.Count; ri++)
        {
            var r = rows[ri];
            double h = r.Header ? hdrH : rowH;
            if (y + h > contentBottom) break;   // out of room: the list scrolls instead of clipping
            lastDrawn = ri;
            if (r.Header)
            {
                ds.DrawText(r.Text, (float)(px + 12), (float)(y + (h - lh) / 2), dim, tf);
                y += h;
                continue;
            }
            keyIdx++;
            string key = r.Key!;
            var def = Settings.Def(key)!;
            bool sel = keyIdx == _settingsSel;
            if (sel) { selKey = key; ds.FillRectangle((float)(px + 1), (float)y, (float)(pw - 2), (float)h, hi); }
            if (Settings.IsEnvOverride(key))
                ds.FillRoundedRectangle(new Rect(px + 8, y + h / 2 - 3, 6, 6), 3, 3, Mix(fg, EffBg(), 0.20));
            double ty = y + (h - lh) / 2;
            bool steppable = def.Kind != SettingKind.Text;
            // The row is laid out from the RIGHT edge inwards, so the step arrows can never spill past
            // the panel: [ label … ] [ ◀ ] [ value box ] [ ▶ ] and a 14 DIP margin after the last one.
            double triW = Math.Max(7.0, lh * 0.36), triH = Math.Max(9.0, lh * 0.42);
            double rightEdge = px + pw - 14;
            double arrowR = steppable ? rightEdge - triW : rightEdge;
            double boxW = Math.Min(Math.Max(140.0, lhm * 7.5), pw * 0.40);
            double boxRight = steppable ? arrowR - 10 : rightEdge;
            double boxX = boxRight - boxW;
            double arrowL = boxX - 10 - triW;
            string label = def.Label + (def.Restart ? "   (restart)" : "");
            ds.DrawText(Fit(label, (steppable ? arrowL : boxX) - (px + 22) - 14), (float)(px + 22), (float)ty,
                        sel ? fg : Mix(fg, EffBg(), 0.06), tf);

            string value = _settingsEditing && sel ? _settingsEditBuf + "_" : Settings.Display(key);
            Color vc = sel ? fg : Mix(fg, EffBg(), 0.10);
            if (_settingsEditing && sel) vc = Mix(_defFg, EffBg(), 0.0);
            ds.DrawText(Fit(value, boxW + 6), (float)boxX, (float)ty, vc, tf);
            if (steppable)
            {
                FillArrow(ds, rc, new Rect(arrowL, y + h / 2 - triH / 2, triW, triH), -1, sel ? fg : dim);
                FillArrow(ds, rc, new Rect(arrowR, y + h / 2 - triH / 2, triW, triH), +1, sel ? fg : dim);
                _settingsHit.Add((new Rect(arrowL - 6, y, triW + 6, h), key, -1));
                _settingsHit.Add((new Rect(arrowR - 4, y, triW + 8, h), key, +1));
            }
            _settingsHit.Add((new Rect(px + 1, y, pw - 2, h), key, 0));
            y += h;
        }

        // Scroll indicators: an up arrow when rows are hidden above, a down arrow when below.
        if (start > 0) FillArrowV(ds, rc, new Rect(px + pw - 18, py + titleH + 3, 9, 7), up: true, fg);
        if (lastDrawn < rows.Count - 1) FillArrowV(ds, rc, new Rect(px + pw - 18, contentBottom - 11, 9, 7), up: false, fg);
        double fy = contentBottom + 6;
        ds.DrawLine((float)px, (float)(contentBottom + 1), (float)(px + pw), (float)(contentBottom + 1), border, 1);
        string hint = _settingsEditing
            ? "editing: type, Backspace, Enter = save, Esc = cancel"
            : (Settings.Def(selKey) is { } sd && sd.Hint.Length > 0 ? sd.Hint : "Up/Down select   Left/Right or Enter change");
        ds.DrawText(Fit(hint, pw - 28), (float)(px + 14), (float)fy, dim, tf);
        ds.DrawText(Fit("Esc close   Ctrl+R reset all   \u2022 = env var wins", pw - 28),
                    (float)(px + 14), (float)(fy + lh + 3), dim, tf);
    }

    // ---- Input ----------------------------------------------------------------------------------
    // The panel is modal for the keyboard. There are TWO keyboard paths in this app — the XAML
    // OnKeyDown handler (used when the XAML island holds focus) and the IME host's ForwardToNvim exit
    // (used whenever the IME target owns focus, i.e. during normal typing) — so both funnel into ONE
    // implementation here, keyed on the same nvim-notation string the nvim path speaks. Handling the
    // panel only in OnKeyDown would leave it deaf in the state the app is usually in.
    private bool SettingsKeyDown(KeyRoutedEventArgs e)
    {
        if (!_settingsOpen) return false;
        // MapModifierKey/MapKey are the grid's own mappings, so "<Esc>"/"<CR>"/"<Up>"/"<C-r>"/"a"
        // mean exactly the same thing here as they do on the way to nvim.
        SettingsConsumeNvimKey(MapModifierKey(e.Key) ?? MapKey(e.Key) ?? "");
        return true;   // modal: even an unmapped key is swallowed
    }

    // Called with the same notation the nvim path uses. "" means "a key with no mapping".
    private void SettingsConsumeNvimKey(string text)
    {
        try
        {
            if (_settingsEditing) SettingsEditConsume(text);
            else
            {
                var keys = SettingsSelectableKeys();
                _settingsSel = Math.Clamp(_settingsSel, 0, keys.Count - 1);
                string key = keys[_settingsSel];
                var def = Settings.Def(key);
                switch (text)
                {
                    case "<Up>":
                        _settingsSel = (_settingsSel - 1 + keys.Count) % keys.Count;
                        break;
                    case "<Down>":
                        _settingsSel = (_settingsSel + 1) % keys.Count;
                        break;
                    case "<Left>":
                        StepSetting(key, -1);
                        break;
                    case "<Right>":
                        StepSetting(key, +1);
                        break;
                    case "<CR>":
                    case " ":
                        if (def != null && def.Kind == SettingKind.Text) BeginEdit(key);
                        else StepSetting(key, +1);
                        break;
                    case "<Esc>":
                        CloseSettings();
                        return;
                    case "<C-r>":
                        Settings.ResetAll();
                        LogImportant("SETTINGS-RESET every value cleared from settings.json (env/defaults apply again)");
                        break;
                    default:
                        break;   // anything else is swallowed: the panel is modal
                }
            }
            ScheduleRender();
            FlushRender();
        }
        catch (Exception ex) { LogCritical("SETTINGS key handling failed: " + ex.Message); }
    }

    private void BeginEdit(string key)
    {
        var d = Settings.Def(key);
        _settingsEditing = true;
        _settingsEditBuf = Settings.Resolve(key, d?.Default ?? "").Value;
        LogImportant($"SETTINGS-EDIT begin {key} value='{_settingsEditBuf}'");
    }

    // Inline text editing: the typed characters arrive as plain text on the same notation channel
    // (WM_CHAR in the IME path, ToChar in the XAML path), so an IME composition can be committed
    // straight into the field.
    private void SettingsEditConsume(string text)
    {
        switch (text)
        {
            case "<Esc>":
                _settingsEditing = false;
                _settingsEditBuf = "";
                LogImportant("SETTINGS-EDIT cancel");
                return;
            case "<CR>":
                CommitEdit();
                return;
            case "<BS>":
                if (_settingsEditBuf.Length > 0)
                    _settingsEditBuf = _settingsEditBuf.Substring(0, _settingsEditBuf.Length - 1);
                return;
            case "":
                return;
        }
        // Real text only: command notation (anything containing '<') is not editable content.
        if (text.Length > 0 && !text.Contains('<')) _settingsEditBuf += text;
    }

    private void CommitEdit()
    {
        var keys = SettingsSelectableKeys();
        string key = keys[Math.Clamp(_settingsSel, 0, keys.Count - 1)];
        string v = _settingsEditBuf.Trim();
        _settingsEditing = false;
        Settings.Set(key, v);
        LogImportant($"SETTINGS-EDIT commit {key} = '{v}'");
    }

    // Mouse wheel over the open panel moves the selection (the list is longer than a small window).
    private void SettingsWheel(int delta)
    {
        if (!_settingsOpen || delta == 0) return;
        var keys = SettingsSelectableKeys();
        _settingsSel = Math.Clamp(_settingsSel + (delta > 0 ? -1 : 1), 0, keys.Count - 1);
        ScheduleRender();
        FlushRender();
    }

    // One step of a setting's value: bools toggle, choices cycle, numbers move by their Step (clamped
    // to Min/Max). Text rows are edited, not stepped.
    private void StepSetting(string key, int dir)
    {
        var d = Settings.Def(key);
        if (d == null) return;
        string cur = Settings.Resolve(key, d.Default).Value;
        string next;
        switch (d.Kind)
        {
            case SettingKind.Bool:
                next = Settings.Bool(key) ? "0" : "1";
                break;
            case SettingKind.Choice:
            {
                int n = d.Choices.Length;
                if (n == 0) return;
                int i = Array.IndexOf(d.Choices, cur);
                if (i < 0) i = 0;
                next = d.Choices[((i + dir) % n + n) % n];
                break;
            }
            case SettingKind.Int:
            case SettingKind.Double:
            {
                double v = Math.Clamp(Settings.Num(key, 0, d.Min, d.Max) + dir * d.Step, d.Min, d.Max);
                next = d.Kind == SettingKind.Int
                    ? ((int)Math.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                break;
            }
            default:
                return;
        }
        Settings.Set(key, next);
    }

    // Settings.Changed -> here, on the UI thread (the panel is the only writer). Re-reads the knobs
    // the render path caches in fields, then repaints. An empty key means "everything" (reset/reload).
    private void ApplySettingChange(string key)
    {
        try
        {
            bool all = key.Length == 0;
            if (all || key == "NVIM_WINUI_OPACITY") _parentOpacity = ParseOpacity("NVIM_WINUI_OPACITY", 1.0);
            if (all || key == "NVIM_WINUI_FLOAT_OPACITY") _floatOpacity = ParseOpacity("NVIM_WINUI_FLOAT_OPACITY", 0.9);
            if (all || key == "NVIM_WINUI_FLOAT_BLUR") _floatBlurAmount = ParseFloatBlur();
            if (all || key == "NVIM_WINUI_LINESPACE")
            {
                _linePitchReduce = ParseLinePitch();
                MeasureRefCell();                              // re-derive the row pitch from the new trim
                UpdateWindowSize(_screenCols, _screenRows);
            }
            if (all || key == "NVIM_WINUI_DIAG") NvimClient.DiagEnabled = Settings.Bool("NVIM_WINUI_DIAG");
            ScheduleRender();
            FlushRender();
            LogImportant($"SETTINGS-APPLY {key} opacity={_parentOpacity:F2} floatOpacity={_floatOpacity:F2} " +
                         $"blur={_floatBlurAmount:F1} linespace={_linePitchReduce:F0}");
        }
        catch (Exception ex) { LogCritical("SETTINGS-APPLY failed: " + ex.Message); }
    }

    // ---- Self-test (verification hook, OFF by default) ------------------------------------------
    // A synthetic MOUSE click cannot be driven on this box (SendInput mouse input never reaches the
    // real cursor from an agent session), so the gear-click path is exercised in-process through the
    // very handler the pointer event calls. Gated off by default: it opens the panel and steps a
    // setting, which would be visible in a normal session. It restores whatever the file held before
    // stepping, and it writes one PNG of the panel (the app's own DIAG shot only fires on a render
    // counter, so it cannot be relied on to catch the panel).
    // Collapse a JSON blob onto one log line.
    private static string OneLine(string s) =>
        s.Replace("\r\n", " ").Replace("\n", " ").Replace("\t", " ");

    // Await a call but never let a blocked nvim wedge the self-test: a hit-enter or swap-file prompt
    // makes nvim stop answering requests (measured here — the request is sent, the reader keeps
    // receiving redraws, and the response simply never arrives). Returns null on timeout/error.
    private static async System.Threading.Tasks.Task<object?> CallOrNull(NvimClient? client, int timeoutMs,
                                                                        string method, params object?[] args)
    {
        if (client == null) return null;
        try
        {
            var call = client.CallAsync(method, args);
            if (await System.Threading.Tasks.Task.WhenAny(call, System.Threading.Tasks.Task.Delay(timeoutMs)) != call)
                return null;
            return await call;
        }
        catch { return null; }
    }

    private void SettingsSelfTest()
    {
        if (!Settings.Bool("NVIM_WINUI_SETTINGS_TEST")) return;
        _ = SettingsSelfTestAsync();
    }

    private async System.Threading.Tasks.Task SettingsSelfTestAsync()
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(1500);   // let the first grid render settle
            UpdateSettingsGeometry();
            double inset = 0, strip = 0;
            try { inset = AppWindow.TitleBar.RightInset; strip = AppWindow.TitleBar.Height; } catch { }
            LogImportant($"SETTINGS-TEST geometry btn=({_settingsBtnRect.X:F0},{_settingsBtnRect.Y:F0}," +
                         $"{_settingsBtnRect.Width:F0}x{_settingsBtnRect.Height:F0}) inset={inset:F0} strip={strip:F0} " +
                         $"root={_root.ActualWidth:F0}x{_root.ActualHeight:F0} canvas={GlyphCanvas.Width:F0}x{GlyphCanvas.Height:F0} " +
                         $"dpi={_dpiScale:F2}");

            LogImportant($"SETTINGS-TEST font glyph=U+{(int)GearGlyph[0]:X4} gearSize={GearFontSize:F2} " +
                         $"panelFamily='{BaseFamily(PanelFamily)}' panelSize={PanelFontSize:F2} natH={PanelLineH():F1} " +
                         $"grid={_narrowFont.Split(',')[0]}@{_narrowSize:F2} wide={_wideFont.Split(',')[0]}@{_wideSize:F2}");
            var c = new Point(_settingsBtnRect.X + _settingsBtnRect.Width / 2,
                              _settingsBtnRect.Y + _settingsBtnRect.Height / 2);
            // Gear-only frame first: the panel would cover the button, and the button's position
            // relative to the (system-drawn) caption buttons is exactly what needs checking. The pair
            // (no-overlay / overlay) is what a pixel diff consumes.
            SettingsTestCapture("settings-nogear", withOverlay: false);
            SettingsTestCapture("settings-gear");
            bool consumed = SettingsPointerPressed(c);
            LogImportant($"SETTINGS-TEST gear-click consumed={consumed} open={_settingsOpen} hitRects={_settingsHit.Count}");

            // Snapshot settings.json first: this test WRITES (that is the point), so it must put the file
            // back byte-identical whether or not the user had one.
            string filePath = Settings.FilePath;
            string original = System.IO.File.Exists(filePath) ? System.IO.File.ReadAllText(filePath) : "";
            string? opacityBefore = Settings.FileValue("NVIM_WINUI_OPACITY");
            string? blurBefore = Settings.FileValue("NVIM_WINUI_FLOAT_BLUR");
            LogImportant($"SETTINGS-TEST snapshot exists={System.IO.File.Exists(filePath)} " +
                         $"values={Settings.FileValueCount} text={OneLine(original)}");
            // Everything below writes; the restore lives in `finally` so no early exit can leave the
            // user's settings.json modified.
            try
            {

            // (1) Step a numeric row through the same code path the arrows use, then OBSERVE the Win32
            //     alpha rather than assuming it landed.
            StepSetting("NVIM_WINUI_OPACITY", -3);              // 1.00 -> 0.85
            ApplyParentOpacity();                               // what RenderNow does on the UI thread
            bool layeredRead = GetLayeredWindowAttributes(GetTopLevelHwnd(), out _, out byte alpha, out _);
            LogImportant($"SETTINGS-TEST opacity-step value={_parentOpacity:F2} layered={_parentLayered} " +
                         $"readback={layeredRead} alpha={alpha}");

            // (2) A second, independent row, to prove it reaches the render field too.
            StepSetting("NVIM_WINUI_FLOAT_BLUR", +1);
            LogImportant($"SETTINGS-TEST step-up NVIM_WINUI_FLOAT_BLUR -> {_floatBlurAmount:F1}");

            // (3) The file really was written and is parseable.
            LogImportant($"SETTINGS-TEST file exists={System.IO.File.Exists(filePath)} values={Settings.FileValueCount} " +
                         $"text={System.IO.File.ReadAllText(filePath).Replace("\r\n", " ").Replace("\n", " ")}");

            // (4) Back to opaque: this is the path that used to early-return and leave the window
            //     translucent, so assert the alpha was released and WS_EX_LAYERED cleared.
            Settings.Set("NVIM_WINUI_OPACITY", opacityBefore);
            Settings.Set("NVIM_WINUI_FLOAT_BLUR", blurBefore);
            ApplyParentOpacity();
            bool layeredRead2 = GetLayeredWindowAttributes(GetTopLevelHwnd(), out _, out byte alpha2, out _);
            LogImportant($"SETTINGS-TEST opacity-reset value={_parentOpacity:F2} layered={_parentLayered} " +
                         $"readback={layeredRead2} alpha={alpha2}");

            SettingsTestCapture("settings-shot");
            // Hit rects are published by a DRAW pass, so they exist only after the capture above. This is
            // the check that the panel's row hitboxes (and its arrow zones) are real geometry.
            LogImportant($"SETTINGS-TEST hitRects={_settingsHit.Count} " +
                         $"panel=({_settingsPanelRect.X:F0},{_settingsPanelRect.Y:F0},{_settingsPanelRect.Width:F0}x{_settingsPanelRect.Height:F0})");
            var arrow = _settingsHit.Find(h => h.Zone == 1);
            var arrowCenter = new Point(arrow.Rect.X + arrow.Rect.Width / 2, arrow.Rect.Y + arrow.Rect.Height / 2);
            bool arrowHit = SettingsPointerPressed(arrowCenter);
            LogImportant($"SETTINGS-TEST arrow-click key={arrow.Key} consumed={arrowHit} value={Settings.Resolve(arrow.Key, "").Value}");

            // (6) The keyboard path, through the same function the two key sources call. Keys are
            //     notation strings, so this is exactly what a real Down/Right press produces.
            SettingsConsumeNvimKey("<Down>");
            SettingsConsumeNvimKey("<Down>");
            SettingsConsumeNvimKey("<Right>");
            LogImportant($"SETTINGS-TEST key-path sel={_settingsSel} " +
                         $"value={Settings.Display(SettingsSelectableKeys()[_settingsSel])} " +
                         $"fileValues={Settings.FileValueCount}");
            SettingsConsumeNvimKey("<Up>");
            SettingsConsumeNvimKey("<Up>");
            LogImportant($"SETTINGS-TEST key-path sel-back={_settingsSel}");

            // (7) The panel font follows guifont — verified LIVE: change it through nvim (exactly what a
            //     user's `:set guifont=` does), let the app re-measure, and capture the panel again. The
            //     two captures then differ only by the font. Every call is bounded: a blocking prompt in
            //     nvim (hit-enter / swap-file dialog) makes it stop answering requests, which must not
            //     wedge the self-test.
            string? guifontBefore = null;
            {
                var cl = _client;
                // Same call shape the guifont refresh itself uses (nvim_get_option_value + empty opts).
                object? got = await CallOrNull(cl, 4000, "nvim_get_option_value", "guifont", new Dictionary<string, object?>());
                guifontBefore = got as string;
                LogImportant($"SETTINGS-TEST font-read raw='{guifontBefore}'");
                if (string.IsNullOrEmpty(guifontBefore))
                {
                    LogImportant("SETTINGS-TEST font-change SKIPPED (nvim did not answer — blocking prompt?)");
                }
                else
                {
                    // A font that IS installed and IS a Nerd Font, so the U+F423 gear can actually be
                    // drawn — the test environment has no OperatorMono Nerd Font.
                    await CallOrNull(cl, 4000, "nvim_set_option_value", "guifont", "JetBrainsMono NFM:h12", new Dictionary<string, object?>());
                    await System.Threading.Tasks.Task.Delay(1200);   // option_set -> RefreshGuifontAsync
                    LogImportant($"SETTINGS-TEST font-change guifont='{guifontBefore}' -> now " +
                                 $"panelFamily='{BaseFamily(PanelFamily)}' panelSize={PanelFontSize:F2} " +
                                 $"natH={PanelLineH():F1} gearSize={GearFontSize:F2}");
                    // The panel covers the button, so capture a clean gear-only frame in the new font
                    // first (close/reopen), then the panel itself.
                    CloseSettings();
                    UpdateSettingsGeometry();
                    LogImportant($"SETTINGS-TEST gear-mode {(GearGlyphAvailable() ? "glyph" : "text")} " +
                                 $"btn=({_settingsBtnRect.X:F0},{_settingsBtnRect.Y:F0},{_settingsBtnRect.Width:F0}x{_settingsBtnRect.Height:F0}) " +
                                 $"family='{PanelFamily}'");
                    SettingsTestCapture("settings-gear-nerd");
                    SettingsTestCapture("settings-glyph", glyphProbe: true);
                    ToggleSettings();
                    SettingsTestCapture("settings-shot-small");
                    await CallOrNull(cl, 4000, "nvim_set_option_value", "guifont", guifontBefore, new Dictionary<string, object?>());
                    await System.Threading.Tasks.Task.Delay(900);
                    LogImportant($"SETTINGS-TEST font-restored panelSize={PanelFontSize:F2} " +
                                 $"natH={PanelLineH():F1} gearSize={GearFontSize:F2}");
                }
            }

            }
            finally
            {
                // (5) Leave settings.json exactly as found (byte-identical, including "did not exist").
                try
                {
                    if (original.Length == 0) { if (System.IO.File.Exists(filePath)) System.IO.File.Delete(filePath); }
                    else System.IO.File.WriteAllText(filePath, original);
                }
                catch (Exception ex) { LogCritical("SETTINGS-TEST restore failed: " + ex.Message); }
                Settings.Load();
            }
            // Left OPEN on purpose so the panel is on screen for a screenshot run.
            LogImportant($"SETTINGS-TEST done (panel left open) file exists={System.IO.File.Exists(filePath)} values={Settings.FileValueCount}");
        }
        catch (Exception ex) { LogCritical("SETTINGS-TEST failed: " + ex.GetType().Name + ": " + ex.Message); }
    }

    // Render the current frame + the panel into an offscreen target and save it, the same way the DIAG
    // full-shot does. Proves what the panel actually looks like without a screen capture.
    private void SettingsTestCapture(string name, bool withOverlay = true, bool glyphProbe = false)
    {
        try
        {
            float w = (float)Math.Round(GlyphCanvas.Width), h = (float)Math.Round(GlyphCanvas.Height);
            if (w <= 1 || h <= 1) { LogImportant("SETTINGS-TEST shot skipped (canvas not sized)"); return; }
            var rcd = (ICanvasResourceCreatorWithDpi)GlyphCanvas;
            using var rt = new Microsoft.Graphics.Canvas.CanvasRenderTarget(rcd, w, h, rcd.Dpi);
            using (var ds = rt.CreateDrawingSession())
            {
                RenderCore(ds, GlyphCanvas);
                // withOverlay:false gives the same frame WITHOUT the gear (and without the panel), so a
                // pixel diff of the pair proves exactly which pixels the overlay added and where.
                if (withOverlay) DrawSettingsOverlay(ds, GlyphCanvas);
            if (glyphProbe)
            {
                // Diagnostic: the icon glyph at 3x, centred, so its SHAPE is legible in the capture —
                // the real button draws it at ~16 DIP, which is too small to judge by eye or by model.
                using var fmt = new CanvasTextFormat
                {
                    FontFamily = PanelFamily,
                    FontSize = (float)(GearFontSize * 3),
                    WordWrapping = CanvasWordWrapping.NoWrap,
                    HorizontalAlignment = CanvasHorizontalAlignment.Center,
                    VerticalAlignment = CanvasVerticalAlignment.Center,
                };
                ds.DrawText(GearGlyph, new Rect(0, 0, w, h), Mix(_defFg, EffBg(), 0.0), fmt);
            }
            }
            string path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NvimWinUIGui", name + ".png");
            _ = SaveRtAsync(rt, path);
            LogImportant($"SETTINGS-TEST shot queued -> {path} ({w:F0}x{h:F0})");
        }
        catch (Exception ex) { LogCritical("SETTINGS-TEST shot failed: " + ex.Message); }
    }
}
