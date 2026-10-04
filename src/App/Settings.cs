using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NvimWinUIGui;

// ---- Setting definitions ---------------------------------------------------------------------
// One user-editable setting. The Key IS the environment-variable name this app honored before the
// settings panel existed, so settings.json stays self-describing and an env var keeps overriding
// the file (see Settings.Resolve). Values are stored as strings: the file is meant to be readable
// and hand-editable, and every consumer parses what it needs.
public enum SettingKind { Bool, Int, Double, Text, Choice }

public sealed class SettingDef
{
    public string Key = "";
    public string Label = "";
    public string Group = "General";
    public SettingKind Kind = SettingKind.Text;
    public string Default = "";
    public double Min, Max, Step = 1;
    public string[] Choices = Array.Empty<string>();
    public bool Restart;   // only takes effect after a restart (nvim path/args, the status-bar row)
    public string Hint = "";
}

// The rows the settings panel shows, in display order, grouped by `Group`. Group order follows
// first appearance. Diagnostics are listed last and are exactly the env vars that stay documented
// as developer tools — they are here so the panel is a complete view of the app's knobs.
public static class SettingDefs
{
    public static readonly SettingDef[] All =
    {
        new() { Key = "NVIM_WINUI_NVIM", Label = "nvim.exe path", Kind = SettingKind.Text,
                Restart = true, Hint = "empty = PATH, then the default install dir" },
        new() { Key = "NVIM_WINUI_ARGS", Label = "Extra nvim args", Kind = SettingKind.Text,
                Restart = true, Hint = "appended to the nvim command line" },
        new() { Key = "NVIM_WINUI_LINESPACE", Label = "Line spacing trim", Kind = SettingKind.Int,
                Default = "0", Min = 0, Max = 12, Step = 1, Hint = "px trimmed from each row's pitch" },
        new() { Key = "NVIM_WINUI_SNAP", Label = "Snap window to cells", Kind = SettingKind.Bool,
                Default = "0", Hint = "on = window tracks the grid exactly" },
        new() { Key = "NVIM_WINUI_STATUSBAR", Label = "Show app status bar", Kind = SettingKind.Bool,
                Default = "0", Restart = true },
        new() { Key = "NVIM_WINUI_OPACITY", Label = "Window opacity", Kind = SettingKind.Double,
                Default = "1", Min = 0, Max = 1, Step = 0.05, Hint = "1 = opaque, 0 = invisible" },
        new() { Key = "NVIM_WINUI_FLOAT_OPACITY", Label = "Float opacity", Kind = SettingKind.Double,
                Default = "0.9", Min = 0, Max = 1, Step = 0.05, Hint = "multiplies nvim's winblend" },
        new() { Key = "NVIM_WINUI_FLOAT_BLUR", Label = "Float backdrop blur", Kind = SettingKind.Double,
                Default = "6", Min = 0, Max = 40, Step = 0.5, Hint = "DIP radius, 0 = off" },
        new() { Key = "NVIM_WINUI_IMEPREEDIT_HL", Label = "Preedit highlight", Kind = SettingKind.Choice,
                Default = "Normal", Choices = new[] { "Normal", "IncSearch", "Cursor", "Visual", "Pmenu" } },
        new() { Key = "NVIM_WINUI_SCROLL_MS", Label = "Smooth scroll (ms)", Kind = SettingKind.Int,
                Default = "120", Min = 0, Max = 1000, Step = 10, Hint = "0 = every scroll instant" },
        new() { Key = "NVIM_WINUI_DIAG", Label = "Diagnostic log", Kind = SettingKind.Bool,
                Default = "0", Group = "Diagnostics", Hint = "RPC/input trace to startup.log" },
        new() { Key = "NVIM_WINUI_SHOT", Label = "Canvas snapshots", Kind = SettingKind.Bool,
                Default = "0", Group = "Diagnostics", Hint = "fullshot.png every 30th render" },
        new() { Key = "NVIM_WINUI_SELFTEST", Label = "Startup self-test", Kind = SettingKind.Bool,
                Default = "0", Group = "Diagnostics", Restart = true, Hint = "types text; pollutes the session" },
        new() { Key = "NVIM_WINUI_IMEPREEDIT_TEST", Label = "Preedit test text", Kind = SettingKind.Text,
                Default = "", Group = "Diagnostics", Hint = "draws a fake composition" },
        new() { Key = "NVIM_WINUI_SCROLL_FREEZE", Label = "Scroll freeze phase", Kind = SettingKind.Text,
                Default = "", Group = "Diagnostics", Hint = "0..1, empty = animation runs" },
    };
}

// ---- Settings store --------------------------------------------------------------------------
// %LOCALAPPDATA%\NvimWinUIGui\settings.json, written by the in-app settings panel.
// Precedence (highest first): environment variable -> settings.json -> the definition's default.
// An existing env-var setup is therefore unaffected: the file only fills in what no env var sets,
// and the panel labels such a row "env" so it is obvious why an edit there looks ignored.
public static class Settings
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NvimWinUIGui");
    public static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly Dictionary<string, string> _file = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (string Value, string Source)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, SettingDef> _byKey = BuildIndex();
    private static bool _loaded;

    // Set by MainWindow so a failed load/save is visible in startup.log without Settings owning
    // that logger.
    public static Action<string>? Log;

    // Raised after Set()/ResetAll(). The key is the changed setting, or "" for "everything".
    public static event Action<string>? Changed;

    private static Dictionary<string, SettingDef> BuildIndex()
    {
        var m = new Dictionary<string, SettingDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in SettingDefs.All) m[d.Key] = d;
        return m;
    }

    public static SettingDef? Def(string key) => _byKey.TryGetValue(key, out var d) ? d : null;

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        Load();
    }

    public static void Load()
    {
        _file.Clear();
        _cache.Clear();
        try
        {
            if (File.Exists(FilePath))
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath));
                if (map != null)
                    foreach (var kv in map)
                        if (!string.IsNullOrWhiteSpace(kv.Key)) _file[kv.Key.Trim()] = kv.Value ?? "";
            }
        }
        catch (Exception ex) { Log?.Invoke("load failed: " + ex.Message); }
    }

    // Effective value + where it came from. Cached: this is read from the render path.
    public static (string Value, string Source) Resolve(string key, string def)
    {
        EnsureLoaded();
        if (_cache.TryGetValue(key, out var c)) return c;
        string? env = null;
        try { env = Environment.GetEnvironmentVariable(key); } catch { }
        var r = !string.IsNullOrEmpty(env) ? (env!, "env")
              : _file.TryGetValue(key, out var f) ? (f, "file")
              : (def, "default");
        _cache[key] = r;
        return r;
    }

    public static string Str(string key, string def = "")
        => Resolve(key, Def(key)?.Default ?? def).Value;

    public static bool Bool(string key, bool def = false)
        => ParseBool(Resolve(key, def ? "1" : "0").Value, def);

    public static double Num(string key, double def, double min, double max)
    {
        var v = Resolve(key, Def(key)?.Default ?? "").Value;
        if (double.TryParse(v, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var d))
        {
            // A 0..1 setting also accepts a bare percentage ("90" = 0.9) — the tolerance the opacity
            // parser always had, kept here so the file and the env var behave identically.
            if (max <= 1.0 && d > 1.0 && d <= 100.0) d /= 100.0;
            return Math.Clamp(d, min, max);
        }
        return def;
    }

    public static int Int(string key, int def, int min, int max)
        => (int)Math.Round(Num(key, def, min, max));

    public static string Source(string key) => Resolve(key, Def(key)?.Default ?? "").Source;
    public static bool IsEnvOverride(string key) => Source(key) == "env";

    private static bool ParseBool(string v, bool def)
    {
        if (string.IsNullOrWhiteSpace(v)) return def;
        switch (v.Trim().ToLowerInvariant())
        {
            case "1": case "true": case "on": case "yes": return true;
            case "0": case "false": case "off": case "no": return false;
            default: return def;
        }
    }

    // Write one value into settings.json (null/"" removes it, falling back to env/default) and
    // notify listeners. A row shadowed by an env var is still written, so the file is correct for
    // the next launch without that env var.
    public static void Set(string key, string? value)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(value)) _file.Remove(key);
        else _file[key] = value!;
        _cache.Remove(key);
        Save();
        Changed?.Invoke(key);
    }

    public static void ResetAll()
    {
        EnsureLoaded();
        _file.Clear();
        _cache.Clear();
        Save();
        Changed?.Invoke("");
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var ordered = new SortedDictionary<string, string>(_file, StringComparer.OrdinalIgnoreCase);
            string json = JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true });
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { Log?.Invoke("save failed: " + ex.Message); }
    }

    // How many settings the file itself supplies (the panel footer and the startup log line).
    public static int FileValueCount => _file.Count;

    // The value settings.json itself holds for a key, or null when the key is not in the file (so a
    // caller can restore exactly the prior file state instead of writing the effective value).
    public static string? FileValue(string key)
    {
        EnsureLoaded();
        return _file.TryGetValue(key, out var v) ? v : null;
    }

    // Display text for a row's value: bools read on/off, numbers are trimmed, and an empty string
    // shows a placeholder rather than nothing.
    public static string Display(string key)
    {
        var d = Def(key);
        var r = Resolve(key, d?.Default ?? "");
        if (d != null && d.Kind == SettingKind.Bool) return ParseBool(r.Value, false) ? "on" : "off";
        if (d != null && d.Kind == SettingKind.Double)
            return Num(key, 0, d.Min, d.Max).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return r.Value.Length == 0 ? "(empty)" : r.Value;
    }
}
