# nvim-winui-gui

WinUI 3 shell around `nvim --embed` (msgpack-RPC). Core + console harness are complete and verified; the WinUI host is in progress.

## Layout
- `src/NvimCore/` — msgpack-rpc codec (`MsgPackEncoder`, `MsgpackStreamDecoder`) and `NvimClient` (spawn nvim, handshake, request/response correlation, notifications).
- `tools/rpc-test/` — console harness that proves the core against a **real** nvim: handshake + `nvim_get_api_info`, `nvim_eval`, `nvim_ui_attach` with redraw notification capture. Byte logging for stream forensics (`NVIM_LOG_BYTES=1`).
- `src/App/` — WinUI 3 host (scaffold; renderer + key forwarding pending).

## Build & run
Requires .NET 8 SDK and Neovim v0.12.x on PATH (or set the path in the harness). On this machine the SDK is at `%USERPROFILE%\.dotnet-sdk-zip`:

```sh
export PATH="$HOME/.dotnet-sdk-zip:$PATH"   # Git Bash; adjust to your SDK location
dotnet build tools/rpc-test/RpcTest.csproj  # core + harness (0 errors expected)
dotnet tools/rpc-test/bin/Debug/net8.0/rpctest.dll
```

Expected output: `TEST1`/`TEST2` PASS, 6 redraw batches from `nvim_ui_attach`, `[rpc-test] DONE: SUCCESS`.

Stream capture for decoder debugging (writes every received byte):
```sh
NVIM_LOG_BYTES=1 NVIM_LOG_FILE=/path/to/capture.hex dotnet tools/rpc-test/bin/Debug/net8.0/rpctest.dll
```

## Verification status (2026-08-23)
- Decoder covers the full msgpack spec used by nvim: fixints, ints/uints all widths, str/bin 8/16/32, ext 8/16/32, float32/64 (big-endian), arrays/maps.
- Live capture of a full session (api_info + eval + ui_attach + redraws) = **52,165 bytes**, replays through the C# decoder to exactly **8 frames** with 0 pending bytes.
