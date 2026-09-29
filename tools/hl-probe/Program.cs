using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
// Probe: for :checkhealth blink.cmp, capture grid_line events and report per row:
//   - the column span nvim actually sends (colStart .. lastCol)
//   - whether cells PAST the text (trailing blanks) carry a background hl id
// This decides whether full-width bands need GUI-side extension or come from nvim.

class Program
{
    private const int Port = 41298;
    static int _cols = 0, _rows = 0;
    // grid -> row -> (colStart, lastColSent, cells[(col,txt,hl)])
    static readonly Dictionary<int, Dictionary<int, RowData>> grids = new();
    static readonly Dictionary<int,(bool hasBg,string hex)> hlInfo = new();
    class RowData { public int ColStart; public int LastCol; public List<(int col,string txt,int hl)> Cells = new(); }

    private static async Task<int> Main(string[] args)
    {
        string healthCmd = args.Length > 0 ? args[0] : "checkhealth blink.cmp";
        var psi = new ProcessStartInfo("nvim")
        {
            Arguments = $"--headless --listen 127.0.0.1:{Port}",
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        Process? proc = null;
        try
        {
            proc = Process.Start(psi)!;
            var outTask = Task.Run(() => { try { return proc.StandardOutput.ReadToEnd(); } catch { return ""; } });
            NvimCore.NvimClient? client = null;
            for (int a = 1; ; a++)
            {
                if (a > 60) throw new TimeoutException("no listener");
                try { client = await NvimCore.NvimClient.ConnectAsync("127.0.0.1", Port); break; }
                catch (SocketException) { await Task.Delay(250); }
            }
            using var nvim = client!;
            await Task.Delay(1500);
            var uiOptions = new Dictionary<string, object?> { ["rgb"] = true, ["ext_linegrid"] = true, ["ext_multigrid"] = true };

            // (hlInfo is a static field, populated in the notification handler.)
            nvim.OnNotification += (method, a) =>
            {
                if (method != "redraw") return;
                if (a is not object?[] evts) return;
                foreach (var e in evts)
                {
                    if (e is not object?[] pair || pair.Length < 2) continue;
                    string name = pair[0]?.ToString() ?? "";
                    switch (name)
                    {
                        case "grid_resize":
                            int gid = ToInt(pair[1]); _cols = ToInt(pair[2]); _rows = ToInt(pair[3]);
                            break;
                        case "hl_attr_define":
                            for (int pi = 1; pi < pair.Length; pi++)
                            {
                                if (pair[pi] is not object?[] tt || tt.Length < 2) continue;
                                int id = ToInt(tt[0]);
                                bool hasBg = false; string hex = "-";
                                if (tt[1] is Dictionary<string, object?> m && m.TryGetValue("background", out var b))
                                {
                                    int bv = ToInt(b);
                                    if (bv >= 0) { hasBg = true; hex = "0x" + bv.ToString("X6"); }
                                }
                                hlInfo[id] = (hasBg, hex);
                            }
                            break;
                        case "grid_line":
                            int grid = ToInt(pair[1]); int row = ToInt(pair[2]); int colStart = ToInt(pair[3]);
                            if (pair.Length < 5 || pair[4] is not object?[] cells) break;
                            if (!grids.TryGetValue(grid, out var rows)) { rows = grids[grid] = new(); }
                            var rd = rows[row]; // fresh per event: nvim resends the row region
                            int col = colStart; int lastHl = -1;
                            foreach (var cr in cells)
                            {
                                string txt; int hl = lastHl; int rep = 1;
                                if (cr is string s2) txt = s2;
                                else if (cr is object?[] ce && ce.Length > 0 && ce[0] is string cs)
                                {
                                    txt = cs;
                                    if (ce.Length > 1 && ToInt(ce[1]) >= 0) { hl = ToInt(ce[1]); lastHl = hl; }
                                    if (ce.Length > 2) { int rc = ToInt(ce[2]); if (rc > 1) rep = rc; }
                                }
                                else continue;
                                for (int r = 0; r < rep && col < _cols; r++)
                                {
                                    rd.Cells.Add((col, txt, hl));
                                    int w = IsWide(txt) ? 2 : 1;
                                    if (w == 2) rd.Cells.Add((col + 1, "", hl)); // covered tail
                                    col += w;
                                }
                            }
                            rd.ColStart = colStart;
                            rd.LastCol = col - 1;
                            rows[row] = rd;
                            break;
                    }
                }
            };

            await nvim.NotifyAsync("nvim_ui_attach", 120, 40, uiOptions);
            try { await nvim.CallAsync("nvim_command", healthCmd); } catch (Exception ex) { Console.WriteLine($"cmd: {ex.Message}"); }
            for (int i = 0; i < 16; i++) await Task.Delay(500);

            Console.WriteLine($"cols={_cols} rows={_rows} grids=[{string.Join(",", grids.Keys.OrderBy(x=>x))}]");
            foreach (var g in grids.Keys.OrderBy(x => x))
            {
                var rows = grids[g];
                int totalCells = rows.Values.Sum(r => r.Cells.Count);
                Console.WriteLine($"\n=== grid {g}: {rows.Count} rows, {totalCells} cells ===");
                foreach (var row in rows.Keys.OrderBy(x => x))
                {
                    var rd = rows[row];
                    // last non-blank text col and its hl; then check trailing blanks' hl
                    int lastTextCol = -1; int lastTextHl = -1;
                    for (int i = 0; i < rd.Cells.Count; i++)
                        if (!string.IsNullOrWhiteSpace(rd.Cells[i].txt)) { lastTextCol = rd.Cells[i].col; lastTextHl = rd.Cells[i].hl; }
                    // trailing blanks after the text: do they carry a bg hl?
                    int trailBgHl = -1; bool anyTrailBlank = false;
                    foreach (var c in rd.Cells)
                        if (c.col > lastTextCol && string.IsNullOrWhiteSpace(c.txt)) { anyTrailBlank = true; if (HasBg(c.hl)) trailBgHl = c.hl; }
                    // leading blanks before text with bg?
                    int leadBgHl = -1;
                    foreach (var c in rd.Cells)
                        if (c.col < lastTextCol && string.IsNullOrWhiteSpace(c.txt) && HasBg(c.hl)) leadBgHl = c.hl;

                    bool interesting = anyTrailBlank || leadBgHl >= 0 || HasBg(lastTextHl);
                    if (!interesting) continue;
                    string txtPreview = "";
                    foreach (var c in rd.Cells.Where(c => !string.IsNullOrWhiteSpace(c.txt)).Take(6)) txtPreview += c.txt;
                    Console.WriteLine($"  row {row,3}: sent[{rd.ColStart}..{rd.LastCol}] lastText@{lastTextCol}(hl={HlName(lastTextHl)}) " +
                        $"trailBlankBg={(anyTrailBlank ? HlName(trailBgHl) : "-")} leadBlankBg={(leadBgHl>=0?HlName(leadBgHl):"-")} | {txtPreview}");
                }
            }
            return 0;
        }
        catch (Exception ex) { Console.WriteLine($"FATAL: {ex.Message}"); return 1; }
        finally { try { if (proc != null && !proc.HasExited) proc.Kill(true); } catch { } }
    }

    static bool HasBg(int hl) => hl >= 0 && hlInfo.TryGetValue(hl, out var i) && i.hasBg;
    static string HlName(int hl)
    {
        if (hl < 0) return "none";
        if (!hlInfo.TryGetValue(hl, out var i)) return $"id{hl}";
        return i.hasBg ? $"{i.hex}" : $"id{hl}(nobg)";
    }
    static bool IsWide(string s) => s.Length > 0 && (s[0] >= 0x1100 && (s[0] <= 0x115F || s[0] == 0x2329 || s[0] == 0x232A ||
        (s[0] >= 0x2E80 && s[0] <= 0xA4CF) || (s[0] >= 0xAC00 && s[0] <= 0xD7A3) || (s[0] >= 0xF900 && s[0] <= 0xFAFF) ||
        (s[0] >= 0xFE10 && s[0] <= 0xFE19) || (s[0] >= 0xFE30 && s[0] <= 0xFE6F) || (s[0] >= 0xFF00 && s[0] <= 0xFF60) ||
        (s[0] >= 0xFFE0 && s[0] <= 0xFFE6)));
    static int ToInt(object? v) => v switch { null => -1, byte b => b, short s => s, int i => i, long l => (int)l, double d => (int)d, _ => -1 };
}
