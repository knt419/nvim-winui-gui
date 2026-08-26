using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using System.Diagnostics;
using NvimCore;

namespace NvimWinUIGui;

public partial class MainWindow
{
    private void SetStatus(string s)
    {
        try { StatusText.Text = s; } catch { /* XAML already torn down (shutdown path); ignore */ }
    }

    private static string StartupLogPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NvimWinUIGui", "startup.log");

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        LogStartup("OnLoadedAsync ENTER (UI thread alive)");
        SetStatus("spawning nvim...");
        try
        {
            int port = FindFreePort();
            // --headless: without it, nvim's console-UI init path hangs startup when all stdio is
            // redirected (no real console) and the --listen socket never opens. Verified empirically 2026-08-24.
            var psi = new ProcessStartInfo(NvimPath, $"--listen 127.0.0.1:{port} --headless")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            _nvimProc = System.Diagnostics.Process.Start(psi);
            LogStartup("spawned nvim pid=" + _nvimProc.Id + " port=" + port);
            _nvimProc.ErrorDataReceived += (s, e) => { if (e.Data != null) LogStartup("NVIM-ERR " + e.Data); };
            _nvimProc.BeginErrorReadLine();
            var client = await ConnectWithRetryAsync(port, 5);
            LogStartup("tcp connect OK");
            _client = client;
            object? info = await _client.CallAsync("nvim_get_api_info");
            LogStartup("api_info returned (" + (info is null ? "null" : info.ToString()!.Length) + " chars)");
            SetStatus($"connected (port {port}), API v{ExtractApiMajor(info)}. attaching ui...");

            _client.OnNotification += OnNvimNotification;
            await _client.CallAsync("nvim_ui_attach", _cols, _rows, new Dictionary<string, object?> { ["rgb"] = true });
            EnsureScreen(_rows, _cols);
            ScheduleRender();
            SetStatus($"ui attached ({_cols}x{_rows}). typing forwards to nvim.");

            await _client.CallAsync("nvim_input", ":set nosplit<CR>ihello from nvim-winui-gui<Esc>:w<CR>");

            // Self-test: wait for redraw notifications to drain, then read back the buffer line
            // and count populated cells -> objective proof that (a) key input reached nvim
            // and executed, and (b) grid_line notifications decoded into the cell model.
            await Task.Delay(1000);
            try
            {
                object? buf = await _client.CallAsync("nvim_get_current_buf");
                var lines = await _client.CallAsync("nvim_buf_get_lines", buf, 0, 1, true);
                string line1 = (lines is object?[] la && la.Length > 0 && la[0] is string s) ? s : "<none>";
                int populated = 0;
                for (int i = 0; i < _cells.Length; i++)
                    if (_cells[i].Text.Length > 0) populated++;
                LogStartup($"SELFTEST line1=\"{line1}\" cells_total={_cells.Length} cells_populated={populated} " +
                            $"EXPECT line1=hello from nvim-winui-gui, populated>25 => input OK + grid decode OK");
            }
            catch (Exception ex) { LogStartup("SELFTEST FAILED: " + ex.Message); }
        }
        catch (Exception ex)
        {
            LogStartup($"\nSTARTUP FAILED: {ex}");
            SetStatus($"STARTUP FAILED: {ex.Message}");
        }
    }

    private static void LogStartup(string s)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(StartupLogPath)!;
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(StartupLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {s}{System.Environment.NewLine}");
        }
        catch { /* best effort */ }
    }

    private void OnNvimNotification(string method, object?[]? args)
    {
        // Notifications arrive on the IO thread; XAML must only be touched from the UI thread.
        var ctx = _uiSyncCtx;
        if (ctx is not null && !ReferenceEquals(ctx, System.Threading.SynchronizationContext.Current))
        {
            try { ctx.Post(_ => HandleNotification(method, args), null); return; } catch { /* fall through */ }
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
                    int col = args.Length > 1 ? ToInt(args[1]) : 0;
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
}
