# nvim-winui-gui

A WinUI 3 shell around `nvim --embed` (msgpack-RPC). The app spawns a headless
Neovim, attaches the UI over RPC (`ext_linegrid`), and renders the grid with
native XAML — no terminal emulator in between.

## Layout
- `src/NvimCore/` — msgpack-rpc codec (`MsgPackEncoder`, `MsgpackStreamDecoder`) and `NvimClient` (spawn nvim, handshake, request/response correlation, notifications).
- `tools/rpc-test/` — console harness that proves the core against a **real** nvim: handshake + `nvim_get_api_info`, `nvim_eval`, `nvim_ui_attach` with redraw notification capture. Byte logging for stream forensics (`NVIM_LOG_BYTES=1`).
- `src/App/` — WinUI 3 host (pure C#, no XAML markup): linegrid renderer, key forwarding (with Ctrl/Alt modifiers), window resize, and nvim path resolution.

## Build & run
Requires the .NET 8 SDK and Neovim v0.12.x. On this machine the SDK is at `%USERPROFILE%\.dotnet-sdk-zip`:

```sh
export PATH="$HOME/.dotnet-sdk-zip:$PATH"   # Git Bash; adjust to your SDK location
dotnet build NvimWinUISolution.sln --nologo  # builds core + app + harness (0 errors expected)
./src/App/bin/x64/Debug/net8.0-windows10.0.22621.0/win-x64/NvimWinUIGui.exe
```

The console harness alone:
```sh
dotnet build tools/rpc-test/RpcTest.csproj  # core + harness (0 errors expected)
dotnet tools/rpc-test/bin/Debug/net8.0/rpctest.dll
```
Expected output: `TEST1`/`TEST2` PASS, redraw batches from `nvim_ui_attach`, `[rpc-test] DONE: SUCCESS`.

## Environment variables
| Variable | Default | Effect |
|---|---|---|
| `NVIM_WINUI_NVIM` | — | Explicit path to `nvim.exe`. If unset, nvim is resolved from `PATH`, then the default install dir. |
| `NVIM_WINUI_DIAG` | off | Enable diagnostic logging (per-frame RPC trace + redraw/resize events) to `%LOCALAPPDATA%\NvimWinUIGui\`. Off by default; rare fatal errors are always logged regardless. |
| `NVIM_WINUI_SELFTEST` | off | Run the startup self-test (types text and creates a test buffer). Diagnostics only — pollutes your session, so keep it off in normal use. |
| `NVIM_LOG_BYTES` / `NVIM_LOG_FILE` | off | Dump every received socket byte to a hex file for stream forensics. |

## Verification status (2026-08-23)
- Decoder covers the full msgpack spec used by nvim: fixints, ints/uints all widths, str/bin 8/16/32, ext 8/16/32, float32/64 (big-endian), arrays/maps.
- Live capture of a full session (api_info + eval + ui_attach + redraws) = **52,165 bytes**, replays through the C# decoder to exactly **8 frames** with 0 pending bytes.
