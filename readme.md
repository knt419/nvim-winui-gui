# nvim-winui-gui

A WinUI 3 shell around `nvim --embed` (msgpack-RPC). The app spawns a headless
Neovim, attaches the UI over RPC (`ext_linegrid` + `ext_multigrid`), and renders
the grid with a Win2D/Direct2D GPU canvas — no terminal emulator in between.

## Layout
- `src/NvimCore/` — msgpack-rpc codec (`MsgPackEncoder`, `MsgpackStreamDecoder`) and `NvimClient` (spawn nvim, handshake, request/response correlation, notifications).
- `src/App/` — WinUI 3 host (pure C#, no XAML markup):
  - `MainWindow.cs` — core state: grid buffers, the `Hl` record (`Fg`, `Bg`, `Blend`, `Reverse`, `FgSet`, `BgSet`), guifont/guifontwide parsing.
  - `MainWindow.render.cs` — Win2D rendering: cell backgrounds/text, highlight resolution (explicit colors, `reverse`, blend), wide CJK glyph fallback to guifontwide, color-emoji path (Segoe UI Emoji + `EnableColorFont`, sized to fit the cell).
  - `MainWindow.multigrid.cs` — `ext_multigrid`: per-window grid buffers composited onto the shared canvas; floating windows (`win_float_pos`) with z-order and focusable/mouse flags; message grid. Float/message cells get **synthesized hl ids** whose colors are pre-blended over the parent cell and marked fully resolved (`FgSet`/`BgSet=true`) so they never fall back to defaults.
  - `MainWindow.mouse.cs` — pointer events → `nvim_input_mouse` (press/release/drag, wheel), same scheme as neovide's mouse manager; fire-and-forget requests ordered by the client write semaphore.
  - `MainWindow.notify.cs` — RPC notification handling, nvim spawn (`--headless --listen`), guifont load at startup + re-read after ~1 s (lazy-loaded plugins), self-test.

## Build & run
Requires the .NET 8 SDK and Neovim v0.12.x. On this machine the SDK is at `%USERPROFILE%\.dotnet-sdk-zip`:

```sh
export PATH="$HOME/.dotnet-sdk-zip:$PATH"   # Git Bash; adjust to your SDK location
dotnet build src/App/NvimWinUIGui.csproj --nologo  # builds core + app (0 errors expected)
./src/App/bin/x64/Debug/net8.0-windows10.0.22621.0/win-x64/NvimWinUIGui.exe
```

`NvimWinUISolution.sln` additionally references the local-only `tools/rpc-test` harness (see below); on a fresh clone build the csproj directly, or restore `tools/` first.

## Local-only tools (`tools/`, git-ignored)
Diagnostic helpers kept out of version control; they live in this working copy only and are not needed to run the app:
- `rpc-test/` — console harness that proves NvimCore against a **real** nvim: handshake + `nvim_get_api_info`, `nvim_eval`, `nvim_ui_attach` with redraw notification capture. Byte logging for stream forensics (`NVIM_LOG_BYTES=1`). Expected output: `TEST1`/`TEST2` PASS, redraw batches from `nvim_ui_attach`, `[rpc-test] DONE: SUCCESS`.
- `hl-probe/` — highlight probe: attaches to a live session, captures `grid_line` events and reports per-row hl spans (colStart..lastCol, trailing-blank bg ids) to decide whether full-width bands come from nvim or need GUI-side extension.
- `CheckExeTimestamp.ps1` — one-off check that the built exe is newer than the sources it was compiled from (stale-exe diagnosis).

## Environment variables
| Variable | Default | Effect |
|---|---|---|
| `NVIM_WINUI_NVIM` | — | Explicit path to `nvim.exe`. If unset, nvim is resolved from `PATH`, then the default install dir. |
| `NVIM_WINUI_ARGS` | — | Extra arguments appended to the nvim command line (e.g. `+checkhealth blink.cmp`). |
| `NVIM_WINUI_DIAG` | off | Enable diagnostic logging (per-frame RPC trace + redraw/resize events) to `%LOCALAPPDATA%\NvimWinUIGui\`. Off by default; rare fatal errors are always logged regardless. |
| `NVIM_WINUI_SHOT` | off | With `NVIM_WINUI_DIAG=1`, save a full-canvas snapshot of the live composite (backgrounds + text) to `%LOCALAPPDATA%\NvimWinUIGui\fullshot.png` every 30th render — for pixel-level inspection without screen capture. |
| `NVIM_WINUI_SELFTEST` | off | Run the startup self-test (types text and creates a test buffer). Diagnostics only — pollutes your session, so keep it off in normal use. |
| `NVIM_WINUI_LINESPACE` | 1 | Extra line spacing in px between grid rows. |
| `NVIM_WINUI_STATUSBAR` | off | Show the app status bar row (hidden by default; set to `1`). |
| `NVIM_WINUI_FLOAT_BLUR` | 6.0 | Gaussian blur radius (DIP) applied to the parent layer while a floating window is up, so the float reads as focused foreground. `0` disables. |
| `NVIM_LOG_BYTES` / `NVIM_LOG_FILE` | off | Dump every received socket byte to a hex file for stream forensics. |

## Highlight model
Cell colors resolve through two helpers (`HlFg`/`HlBg`) that mirror neovide's semantics:

- **Explicit** fg/bg from the protocol are used as sent; `blend` alpha-composites the hl background over the parent cell's background (non-reversed path only).
- **`reverse`**: nvim sends `{reverse: true}` for groups like `healthSectionDelim`; the fill is an opaque swap of Normal's fg/bg (e.g. a full-width band in Normal-fg color), never blended.
- **No explicit value** → null, and the caller falls back to its default (`_defFg`/`_defBg`). This is why synthesized float/message hl ids must carry `FgSet`/`BgSet=true`: their colors are already fully resolved by blending over the parent cell, and marking them unresolved makes every glyph fall back to Normal fg (a uniform grey block).

## Verification status
- Decoder covers the full msgpack spec used by nvim: fixints, ints/uints all widths, str/bin 8/16/32, ext 8/16/32, float32/64 (big-endian), arrays/maps. Live capture of a full session replays through the C# decoder with 0 pending bytes.
- `:checkhealth blink.cmp` rendering matches neovide pixel-for-pixel on the reference case: row 1 explicit bg band (`0x44495E`) and row 2 `reverse` band (Normal fg `#63718B`), verified by protocol capture + screenshot pixel sampling (2026-09-26).
