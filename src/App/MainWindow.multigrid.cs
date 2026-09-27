using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI;
using NvimCore;

namespace NvimWinUIGui;

public partial class MainWindow
{
    // ---- Multigrid (ext_multigrid) state -------------------------------
    // With ext_multigrid, nvim splits the screen into per-window grids. Grid 1 is the
    // outer frame (tabline/statusline/quickfix); each window gets its own grid whose
    // content arrives in LOCAL coordinates and is placed by win_pos [grid, win, row, col, w, h].
    // We keep a buffer per grid and composite them onto the shared _cells at render time.

    private class MGrid
    {
        public int Id;
        public int Cols = 1;
        public int Rows = 1;
        public Cell[] Cells = Array.Empty<Cell>();
        // Placement within the outer frame (from win_pos / msg_set_pos / win_float_pos).
        public int PosRow, PosCol;
        public bool IsMessageGrid;
        public int ZIndex;          // draw order: higher = on top (floating windows > normal)
        public bool Focusable;      // win_float_pos mouse_enabled/focusable — input-capable float
        public int LastHl = -1;   // hl inheritance across grid_line tuples for this grid
    }

    private readonly Dictionary<int, MGrid> _mgrid = new();
    private bool _multigridActive;

    private IEnumerable<MGrid> MGrids => _mgrid.Values;

    // A grid that renders ON TOP of the window stack and triggers the parent-layer blur. A float
    // qualifies when it is a genuine floating window (zindex > 0) AND either:
    //   1. it currently holds the cursor (interactive float like a picker — grid_cursor_goto moved in), or
    //   2. nvim is in an input mode (cmdline/search) — covers non-focusable cmdline UIs like
    //      tiny-cmdline where focusable=false and the cursor stays in the parent window.
    // The message grid is a bottom-row surface, not a popup. Focusable-but-idle floats in normal
    // mode (progress notifications like checkhealth) do NOT blur the parent. Guarded with
    // MGridHasContent so a stale empty float can't hold the blur.
    private bool MGridIsOverlay(MGrid g)
    {
        if (g.IsMessageGrid || g.PosRow >= int.MaxValue || g.ZIndex <= 0) return false;
        if (g.Id == _curGridId && g.Focusable) return true; // cursor inside an interactive float
        return MIsInputMode(); // cmdline/search float (may be non-focusable)
    }

    // True when the cursor currently belongs to a sharp-layer grid (a floating window or the
    // message surface). Such a cursor must be drawn in the sharp foreground pass, never in the
    // blurred parent, or it would appear blurred.
    private bool CursorIsInSharpLayer()
    {
        if (!_multigridActive || _curGridId < 0) return false;
        if (!_mgrid.TryGetValue(_curGridId, out var g)) return false;
        if (!MGridResolveCursor(_curGridId, _curLocalRow, _curLocalCol, out int r, out int c)) return false;
        return r >= g.PosRow && r < g.PosRow + g.Rows && c >= g.PosCol && c < g.PosCol + g.Cols && MGridIsSharpLayer(g);
    }

    // Input modes where a floating cmdline/search UI should blur the parent behind it.
    private bool MIsInputMode() =>
        _modeName.StartsWith("cmdline", StringComparison.Ordinal) ||
        _modeName.Equals("search", StringComparison.Ordinal) ||
        _modeName.Equals("incsearch", StringComparison.Ordinal);

    // Sharp layer drawn AFTER the parent blur: floats (also trigger the blur) and the message
    // grid. The message grid is never part of the blurred base — blurring the status/cmd line
    // under a float looks wrong, so it is composited into the base only for the flat path and
    // redrawn sharp here when the blur fires.
    private static bool MGridIsSharpLayer(MGrid g) => g.PosRow < int.MaxValue && (g.IsMessageGrid || g.ZIndex > 0);

    // True when the grid carries at least one visible glyph (any non-whitespace cell).
    private static bool MGridHasContent(MGrid g)
    {
        for (int i = 0; i < g.Cells.Length; i++)
        {
            var t = g.Cells[i].Text;
            if (t.Length > 0 && !string.IsNullOrWhiteSpace(t)) return true;
        }
        return false;
    }

    private void MGridReset()
    {
        _mgrid.Clear();
        _multigridActive = false;
    }

    // Shared glyph writer: write one grid_line cell array into an arbitrary Cell[] row buffer,
    // wide-glyph aware (CJK/emoji advance 2 columns and mark their tail cell covered-blank).
    private void WriteCellArray(Cell[] buf, int bufCols, int rowIdx, int colStart, object?[] cellArray, ref int lastHl)
    {
        if (rowIdx < 0 || colStart < 0) return;
        int maxRow = buf.Length / bufCols;
        if (rowIdx >= maxRow) return;
        int col = colStart;
        foreach (var cellRaw in cellArray)
        {
            string txt;
            int hl = lastHl; // inherit most-recently-seen hl_id when this cell omits it
            int repeatCount = 1;

            if (cellRaw is string s2)
                txt = s2;
            else if (cellRaw is object?[] ce && ce.Length > 0 && ce[0] is string cs)
            {
                txt = cs;
                if (ce.Length > 1 && ToInt(ce[1]) >= 0) { hl = ToInt(ce[1]); lastHl = hl; } // highlight ID
                if (ce.Length > 2) { int rc = ToInt(ce[2]); if (rc > 1) repeatCount = rc; }
            }
            else continue;

            for (int r = 0; r < repeatCount && col < bufCols; r++)
            {
                int p = 0;
                while (p < txt.Length)
                {
                    string g;
                    if (char.IsHighSurrogate(txt[p]) && p + 1 < txt.Length && char.IsLowSurrogate(txt[p + 1]))
                        { g = txt.Substring(p, 2); p += 2; }   // full surrogate pair (emoji/astral)
                    else
                        { g = txt[p].ToString(); p += 1; }    // BMP code point
                    if (col >= bufCols) break;
                    var head = buf[rowIdx * bufCols + col];
                    head.Text = g;                            // the glyph (never empty here)
                    head.Hl = hl >= 0 ? hl : -1;              // only apply valid highlight IDs
                    int w = IsWideGlyph(g) ? 2 : 1;           // display width in cells
                    col++;
                    for (int k = 1; k < w && col < bufCols; k++)
                    {
                        var tail = buf[rowIdx * bufCols + col];
                        tail.Text = "";                       // covered by the wide glyph -> render blank, no tofu
                        tail.Hl = hl >= 0 ? hl : -1;
                        col++;
                    }
                }
            }
        }
    }

    private MGrid GetOrCreateMGrid(int id)
    {
        if (!_mgrid.TryGetValue(id, out var g))
        {
            g = new MGrid { Id = id };
            _mgrid[id] = g;
        }
        return g;
    }

    // grid_resize [grid_id, cols, rows] for a per-window grid (id != 1).
    private void MGridResize(int id, int cols, int rows)
    {
        if (cols <= 0 || rows <= 0) return;
        var g = GetOrCreateMGrid(id);
        _multigridActive = true;
        // Preserve existing content on resize where possible.
        var old = g.Cells; int oc = g.Cols, orow = g.Rows;
        g.Cols = cols; g.Rows = rows;
        g.Cells = new Cell[cols * rows];
        for (int i = 0; i < g.Cells.Length; i++) { g.Cells[i] = new Cell { Text = " ", Hl = -1 }; }
        int copyR = Math.Min(orow, rows), copyC = Math.Min(oc, cols);
        for (int r = 0; r < copyR; r++)
            for (int c = 0; c < copyC; c++)
                if (r * oc + c < old.Length) g.Cells[r * cols + c] = old[r * oc + c];
    }

    // grid_line [grid_id, row, col_start, cells] into a per-window grid buffer.
    private void MGridLine(int id, int rowIdx, int colStart, object?[] cellArray)
    {
        if (!_mgrid.TryGetValue(id, out var g)) return;
        if (rowIdx < 0 || rowIdx >= g.Rows) return;
        WriteCellArray(g.Cells, g.Cols, rowIdx, colStart, cellArray, ref g.LastHl);
    }

    // grid_clear [grid_id] for a per-window grid.
    private void MGridClear(int id)
    {
        if (!_mgrid.TryGetValue(id, out var g)) return;
        for (int i = 0; i < g.Cells.Length; i++) { g.Cells[i].Text = " "; g.Cells[i].Hl = -1; }
    }

    // grid_scroll [grid_id, top, bot, left, right, rows(, cols)] within a per-window grid.
    private void MGridScroll(int id, int top, int bot, int left, int right, int rows)
    {
        if (!_mgrid.TryGetValue(id, out var g)) return;
        if (rows == 0) return;
        if (g.Cells.Length != g.Rows * g.Cols) return; // never sized: no storage to shift
        top   = Math.Max(0, Math.Min(top, g.Rows));
        bot   = Math.Max(0, Math.Min(bot, g.Rows));
        left  = Math.Max(0, Math.Min(left, g.Cols));
        right = Math.Max(0, Math.Min(right, g.Cols));
        if (top >= bot || left >= right) return;

        int regionW = right - left;
        var srcTxt = new string[(bot - top) * regionW];
        var srcHl  = new int[(bot - top) * regionW];
        for (int r = top; r < bot; r++)
            for (int c = left; c < right; c++)
            {
                var cc = g.Cells[r * g.Cols + c];
                srcTxt[(r - top) * regionW + (c - left)] = cc.Text;
                srcHl[(r - top) * regionW + (c - left)]  = cc.Hl;
            }

        for (int dr = top; dr < bot; dr++)
        {
            int sr = dr + rows;
            for (int c = left; c < right; c++)
            {
                var cell = new Cell();
                if (sr >= top && sr < bot)
                {
                    int si = (sr - top) * regionW + (c - left);
                    cell.Text = srcTxt[si];
                    cell.Hl   = srcHl[si];
                }
                g.Cells[dr * g.Cols + c] = cell;
            }
        }
    }

    // win_pos [grid_id, win_handle, start_row, start_col, width, height] — place a window grid.
    // A grid that previously floated (ZIndex > 0 from win_float_pos) must drop back to a normal
    // window order when nvim repositions it with win_pos — otherwise MGridIsOverlay stays true
    // and the blur never turns off after a float closes/reverts.
    private void MWinPos(int id, int handle, int row, int col, int w, int h)
    {
        var g = GetOrCreateMGrid(id);
        g.PosRow = row; g.PosCol = col;
        g.ZIndex = 0;
        g.Focusable = false;
        if (w > 0 && h > 0 && (g.Cols != w || g.Rows != h)) MGridResize(id, w, h);
    }

    // msg_set_pos [grid_id, row, scrolled, sep_char, zindex, compindex] — place the message grid.
    private void MMsgSetPos(int id, int row, int zindex)
    {
        var g = GetOrCreateMGrid(id);
        g.IsMessageGrid = true;
        g.PosRow = row; g.PosCol = 0;
        g.ZIndex = zindex;   // message grid sits above normal windows (nvim sends ~200)
    }

    // win_float_pos [grid_id, win_handle, anchor, anchor_grid, anchor_row, anchor_col,
    //                mouse_enabled/focusable, zindex, compindex, screen_row, screen_col]
    // nvim 0.12+ positions FLOATING windows with this event (not win_pos). Two modes:
    //   1. nvim-computed: draw directly at (screen_row, screen_col) — preferred when valid.
    //   2. manual anchor: resolve against the anchor grid's own position.
    private void MWinFloatPos(int id, int handle, object? anchor, int anchorGrid, double aRow, double aCol, bool focusable, int zindex, int compIndex, double sRow, double sCol)
    {
        var g = GetOrCreateMGrid(id);
        if (sRow >= 0 && sCol >= 0)
        {
            g.PosRow = (int)sRow; g.PosCol = (int)sCol;
        }
        else if (_mgrid.TryGetValue(anchorGrid, out var ag))
        {
            g.PosRow = ag.PosRow + (int)aRow; g.PosCol = ag.PosCol + (int)aCol;
        }
        else
        {
            g.PosRow = (int)aRow; g.PosCol = (int)aCol;
        }
        g.ZIndex = zindex;   // floating windows carry a high zindex (~50) -> drawn on top
        g.Focusable = focusable;
    }

    // win_hide [grid_id] / win_close [grid_id].
    private void MWinHide(int id) { if (_mgrid.TryGetValue(id, out var g)) g.PosRow = int.MaxValue; }
    private void MWinClose(int id) { _mgrid.Remove(id); }

    // Build the frame to draw: outer-frame (grid 1) content + all window grids composited on top,
    // into a scratch buffer so _cells stays clean. Window grids fully cover their region (blanks
    // included), which is correct in multigrid — each grid is an independent surface placed by
    // win_pos; the outer frame only shows where no window covers it (gaps / statusline row).
    private Cell[]? _renderScratch;

    // --- Per-cell alpha compositing for floating windows -------------------------------
    // nvim signals a float's transparency through its highlight background: bg=-1 -> fully
    // transparent (A=0), an ARGB value with 0<A<255 -> semi-transparent. Without handling this,
    // BuildRenderCells hard-overwrites the parent frame and the swap-chain clear color (_defBg)
    // shows through — i.e. floats are always opaque. We composite per cell instead:
    //   A=255  -> hard overwrite (fast path, unchanged behavior)
    //   A<255  -> blend top bg over the already-composited base; register a synthetic hl so the
    //             render loop paints the blended bg and draws the float's fg on top. This makes
    //             transparent floats reveal the parent window (neovide behavior) while keeping
    //             their text visible.
    private int _nextBlendHlId = 2_000_000_000; // well above any real nvim hl id
    private readonly Dictionary<(int topHl, int basePacked, int fgPacked, bool parentGlyph), int> _blendCache = new();

    // Effective bg color of a cell for compositing: its highlight bg if opaque-ish, else the
    // swap-chain clear color (what a transparent region ultimately shows).
    private Color ResolveCellBgColor(Cell cell)
    {
        if (cell.Hl >= 0 && _hlDefs.TryGetValue(cell.Hl, out var h))
        {
            var b = HlBg(h);
            if (b is not null && b.Value.A > 0) return b.Value;
        }
        return _defBg;
    }

    private Color ResolveCellFgColor(Cell cell)
    {
        if (cell.Hl >= 0 && _hlDefs.TryGetValue(cell.Hl, out var h))
        {
            var f = HlFg(h);
            if (f is not null && f.Value.A > 0) return f.Value;
        }
        return _defFg;
    }

    // Standard "source over" alpha blend of top onto bottom. Result is opaque when bottom is.
    private static Color AlphaBlend(Color top, Color bottom)
    {
        if (top.A == 0xFF) return top;
        byte ta = top.A;
        if (ta == 0) return bottom;
        int ia = 255 - ta;
        byte r = (byte)((top.R * ta + bottom.R * ia) / 255);
        byte g = (byte)((top.G * ta + bottom.G * ia) / 255);
        byte b = (byte)((top.B * ta + bottom.B * ia) / 255);
        return Color.FromArgb(0xFF, r, g, b);
    }

    // Raw highlight bg of a cell with NO fallback to _defBg. Returns null when the cell has no
    // hl (treated as a fully transparent surface). This is what tells us whether a float cell is
    // opaque / semi-transparent / transparent — ResolveCellBgColor can't, because it falls back
    // to _defBg for transparent cells and would mask them as opaque.
    // A non-zero `blend` attribute (winblend/pumblend) modulates the alpha: blend=0 -> opaque
    // (A=255), blend=100 -> fully transparent (A=0, the parent shows exactly). The stored hl color
    // is left untouched so the same attr id keeps rendering correctly on non-floating surfaces.
    private Color? GetRawHlBg(Cell cell)
    {
        if (cell.Hl >= 0 && _hlDefs.TryGetValue(cell.Hl, out var h))
        {
            var b = HlBg(h);
            if (b is null) return null; // no explicit bg and not reversed -> transparent surface
            if (h.Blend > 0 && !h.Reverse && b.Value.A > 0)
                b = Color.FromArgb((byte)(255 * (100 - h.Blend) / 100), b.Value.R, b.Value.G, b.Value.B);
            return b;
        }
        return null;
    }

    // Return the hl id to store in a scratch cell for `top` composited over base color `baseBg`.
    // `rawFg` is the foreground of the glyph actually drawn (the float's own, or — for a blank float
    // cell that lets the parent show through — the base cell's). Layer separation: the float's own
    // glyphs keep (100-blend)% of their color (strong, they're on top), while preserved parent
    // glyphs keep only blend% — they fade in exactly as the float becomes more transparent, so the
    // two windows never render at the same weight/readability.
    private int GetBlendHlId(Cell top, Color? rawTopBg, Color baseBg, Color rawFg, bool parentGlyph)
    {
        var key = (top.Hl, PackColor(baseBg), PackColor(rawFg), parentGlyph);
        if (_blendCache.TryGetValue(key, out var id)) return id;
        id = _nextBlendHlId++;
        // Alpha from the float's own blend (already applied by GetRawHlBg for the bg).
        int blend = _hlDefs.TryGetValue(top.Hl, out var th) ? th.Blend : 0;
        Color blendedFg;
        if (blend > 0 && rawFg.A > 0)
        {
            int keepPct = parentGlyph ? blend : 100 - blend; // client-server chain: preserves layer contrast
            byte a = (byte)(255 * keepPct / 100);
            blendedFg = AlphaBlend(Color.FromArgb(a, rawFg.R, rawFg.G, rawFg.B), baseBg);
        }
        else blendedFg = rawFg;
        // Blend the float's own bg over the parent. A=0 / no hl -> fully transparent: parent shows.
        Color blended = (rawTopBg is null || rawTopBg.Value.A == 0) ? baseBg : AlphaBlend(rawTopBg.Value, baseBg);
        // Both colors are already fully resolved here (opaque fg over the composited bg), so mark
        // them explicit: HlFg/HlBg treat FgSet/BgSet=false as "nvim sent no color" and return null,
        // which would make every float glyph fall back to _defFg — a uniform gray box.
        _hlDefs[id] = new Hl(blendedFg, blended, 0, false, true, true);
        _blendCache[key] = id;
        return id;
    }

    // Drop synthetic blend entries. Must run whenever real hl ids may be redefined (theme change)
    // or the default bg changes, so cached blends don't reference stale colors.
    private void InvalidateBlendCache()
    {
        _blendCache.Clear();
        foreach (var k in _hlDefs.Keys.Where(k => k >= 2_000_000_000).ToList()) _hlDefs.Remove(k);
        _nextBlendHlId = 2_000_000_000;
    }

    private Cell[] BuildRenderCells(bool skipOverlayLayers = false)
    {
        int rows = _screenRows, cols = _screenCols;
        if (rows <= 0 || cols <= 0) return _cells;
        if (_renderScratch == null || _renderScratch.Length != rows * cols)
            _renderScratch = new Cell[rows * cols];
        for (int i = 0; i < rows * cols; i++) _renderScratch[i] = _cells[i]; // outer frame base
        if (!_multigridActive) return _renderScratch;
        // Draw in ascending zindex so higher-z surfaces (floating windows, message grid) land on top.
        foreach (var g in _mgrid.Values.OrderBy(g => g.ZIndex))
        {
            if (g.PosRow >= int.MaxValue) continue; // hidden
            if (skipOverlayLayers && MGridIsSharpLayer(g)) continue; // float/msg → sharp layer after blur
            // A positioned-but-never-sized grid (win_pos/msg_set_pos arrived before any
            // grid_resize, e.g. message grid 0 at startup under --embed) has no cell storage
            // yet — indexing it would throw and blank the whole frame. Nothing to draw: skip.
            if (g.Cells.Length == 0) continue;
            for (int r = 0; r < g.Rows; r++)
            {
                int tr = g.PosRow + r;
                if (tr < 0 || tr >= rows) continue;
                for (int c = 0; c < g.Cols; c++)
                {
                    int tc = g.PosCol + c;
                    if (tc < 0 || tc >= cols) continue;
                    var top = g.Cells[r * g.Cols + c];
                    // Window grids (zindex <= 0, not the message surface) are opaque independent
                    // surfaces: their cells — blanks and no-background highlights included — fully
                    // replace the outer frame beneath them. Only floats / the message grid may
                    // blend with the base (winblend, transparent floats). Without this gate, a
                    // window cell whose hl has no background (A<255) fell into the transparent
                    // path and stale frame content bled through — e.g. an old statusline left in
                    // _cells at row 22 when a cmdheight toggle moved the statusline up/down,
                    // showing as a phantom second statusline band above the real one.
                    if (g.ZIndex <= 0 && !g.IsMessageGrid)
                    {
                        _renderScratch[tr * cols + tc] = top;
                        continue;
                    }
                    // Fast path: opaque float cell (hl bg A=255) fully covers the base.
                    Color? rawBg = GetRawHlBg(top);
                    if (rawBg is not null && rawBg.Value.A == 0xFF)
                    {
                        _renderScratch[tr * cols + tc] = top;
                    }
                    else
                    {
                        // Semi/transparent: blend over whatever is already composited below.
                        // A=0 / no hl keeps the base bg and just overlays the float's fg (text stays visible).
                        // Where the float cell is BLANK we keep the base cell's glyph, so the parent
                        // window's content stays readable through a translucent float (neovide behavior);
                        // the preserved glyph is dimmed with the float's own blend rate, not left full-bright.
                        int idx = tr * cols + tc;
                        var baseCell = _renderScratch[idx];
                        var baseBg = ResolveCellBgColor(baseCell);
                        bool parentGlyph = top.Text.Trim().Length == 0;
                        string outText = parentGlyph ? baseCell.Text : top.Text;
                        Color rawFg = parentGlyph ? ResolveCellFgColor(baseCell) : ResolveCellFgColor(top);
                        _renderScratch[idx] = new Cell { Text = outText, Hl = GetBlendHlId(top, rawBg, baseBg, rawFg, parentGlyph) };
                    }
                }
            }
        }
        return _renderScratch;
    }

    // Resolve a cursor position (grid_id, row, col) to outer-frame coordinates for rendering.
    private bool MGridResolveCursor(int gridId, int row, int col, out int oRow, out int oCol)
    {
        oRow = row; oCol = col;
        if (_mgrid.TryGetValue(gridId, out var g))
        {
            oRow = g.PosRow + row;
            oCol = g.PosCol + col;
            return true;
        }
        return false; // grid 1 / unknown -> already outer-frame coords
    }
}
