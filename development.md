# Development notes

Architecture, protocol coverage, and implementation detail for
[nvim-winui-gui](readme.md). For installing, configuring fonts, and the environment
variables, see the README.

## Layout

- `src/NvimCore/` — msgpack-rpc codec (`MsgPackEncoder`, `MsgpackStreamDecoder`) and
  `NvimClient` (spawn nvim, handshake, request/response correlation, notifications).
- `src/App/` — WinUI 3 host (pure C#, no XAML markup):
  - `MainWindow.cs` — core state: grid buffers, the `Hl` record (`Fg`, `Bg`, `Blend`,
    `Reverse`, `FgSet`, `BgSet`, `Italic`, `Bold`), guifont/guifontwide parsing.
  - `MainWindow.render.cs` — Win2D rendering: cell backgrounds/text, highlight
    resolution (explicit colors, `reverse`, blend), wide CJK glyph fallback to
    guifontwide, color-emoji path (Segoe UI Emoji + `EnableColorFont`, sized to fit the
    cell). Text style (`italic`/`bold`) is resolved per hl and drawn through a
    lazily-built `CanvasTextFormat` cache (normal reuses the base narrow/wide formats;
    italic/bold/bold-italic are synthesized by DirectWrite, so they work with any
    guifont — same as neovide's simulated oblique).
  - `MainWindow.multigrid.cs` — `ext_multigrid`: per-window grid buffers composited onto
    the shared canvas; floating windows (`win_float_pos`) with z-order and
    focusable/mouse flags; message grid. Float/message cells get **synthesized hl ids**
    whose colors are pre-blended over the parent cell and marked fully resolved
    (`FgSet`/`BgSet=true`) so they never fall back to defaults.
  - `MainWindow.mouse.cs` — pointer events → `nvim_input_mouse` (press/release/drag,
    wheel), same scheme as neovide's mouse manager; fire-and-forget requests ordered by
    the client write semaphore.
  - `MainWindow.notify.cs` — RPC notification handling, nvim spawn (`--headless
    --listen`), guifont load at startup + re-read after ~1 s (lazy-loaded plugins),
    self-test.
  - `MainWindow.ime.cs` — hidden `EDIT` IME target: create/subclass, input-context
    association, key forwarding, composition handling.

## Highlight model

Cell colors resolve through two helpers (`HlFg`/`HlBg`) that mirror neovide's semantics:

- **Explicit** fg/bg from the protocol are used as sent; `blend` alpha-composites the hl
  background over the parent cell's background (non-reversed path only).
- **`reverse`**: nvim sends `{reverse: true}` for groups like `healthSectionDelim`; the
  fill is an opaque swap of Normal's fg/bg (e.g. a full-width band in Normal-fg color),
  never blended.
- **No explicit value** → null, and the caller falls back to its default
  (`_defFg`/`_defBg`). This is why synthesized float/message hl ids must carry
  `FgSet`/`BgSet=true`: their colors are already fully resolved by blending over the
  parent cell, and marking them unresolved makes every glyph fall back to Normal fg (a
  uniform grey block).
- **Text style**: the protocol's per-hl `italic`/`bold` flags are parsed into the `Hl`
  record and applied at draw time. A text run breaks on a style change as well as a
  color/font change, so mixed-style lines render correctly; italic uses DirectWrite
  oblique synthesis (true italic face when the family has one), bold maps to weight 700.

## Cursor

The cursor block and its glyph are computed from the **exact cell the cursor is on**,
resolved at render time from that cell's own effective fg/bg, with `reverse` already
applied. `_curLocalRow`/`_curLocalCol` are deliberately not used for the lookup: with
`ext_multigrid` they can name a different grid than the one being drawn, which is what
made the cursor pick up the previous cell's or previous row's colors.

## Input

Keyboard focus lives on a hidden native `EDIT` created in `MainWindow.ime.cs`, because
WinUI has no IME story of its own. The rules that matter, each learned from a bug:

- **One delivery point.** Windows sends both `WM_KEYDOWN` and `WM_CHAR` for a single
  physical press. Forward from exactly one. `VK_RETURN` and `VK_TAB` are the two keys
  where the character maps to the same command as the virtual key, so those are not
  mapped from `WM_KEYDOWN`; every other command key stays on that path.
- **Consume, don't suppress the bell.** Windows rings for a keystroke a control refuses,
  and an `EDIT` emptied after every keystroke can never usefully act on one. Processed
  keys are not chained to the original proc. `WM_IME_CHAR` is the sole exception: it
  passes through under its own id because the IME state machine keys off the message id.
- **Never call back into the control from inside its own proc.** `ImmNotifyIME` posts
  the composition string *into* the control and so re-enters the subclass proc; doing
  that made the control stop accepting compositions entirely.
- **`ime32.dll` may be absent.** It is loaded lazily and probed once; the risky call
  lives in its own `try` so a missing DLL cannot skip the `SetFocus` that the IME needs.

## API coverage (nvim 0.12.5, `--api-info`: 261 functions / 10 ui_options / 69 ui_events)

- **attach options**: `rgb`, `ext_linegrid`, `ext_multigrid`. The cmdline, completion
  menu, tabline and messages are deliberately NOT externalized (no `ext_cmdline` /
  `ext_popupmenu` / `ext_tabline` / `ext_messages`), so nvim draws them into the grid
  and they need no widget code.
- **ui events handled**: the 14 grid/multigrid/highlight events (`grid_resize`,
  `grid_line`, `grid_clear`, `grid_scroll`, `grid_cursor_goto`, `hl_attr_define`,
  `default_colors_set`, `mode_change`, `mode_info_set`, `win_pos`, `win_float_pos`,
  `win_hide`, `win_close`, `msg_set_pos`) plus `flush` and `option_set`.
- **flush-gated rendering**: nvim may send several `redraw` batches before the screen is
  consistent and marks only the last with `flush` (api-ui-events.txt), so
  `HandleNotification` paints on flush rather than after every batch. A 250 ms watchdog
  (`FLUSH-WATCHDOG`) forces a paint if no flush arrives, so the screen can never go
  stale.
- **`option_set`** is the live path for `:set guifont` / `guifontwide`: those trigger a
  font re-measure (not just a repaint), while `linespace`/`showtabline` only need a
  repaint or grid resync.
- **`nvim_set_client_info`** announces `name=nvim-winui-gui type=ui` after connect, so
  `nvim_get_chan_info().client` identifies this frontend.
- `cursor_position` was removed from the redraw handler: it is not in nvim's `ui_events`
  (it belonged to the legacy cell-based grid) and is never sent when `ext_linegrid` is on.
- Mouse events use `grid_id=0`, which means SCREEN coordinates ("0 to let Nvim decide
  positioning of windows"), so clicks resolve to the right split/float even though
  `ext_multigrid` is active.
- `nvim_ui_attach` is sent as a notification purely to avoid blocking startup on a
  round-trip. It is NOT a void function — nvim 0.12.5 declares no `void` returns in
  `--api-info`, and sending it as a request returns `error=null` and attaches
  identically.

## Opacity

Two independent multipliers, both read once at startup:

- `NVIM_WINUI_OPACITY` (default `1.0`) applies to the whole top-level window via
  `WS_EX_LAYERED` + `SetLayeredWindowAttributes`. The extended style has to be added
  first — calling `SetLayeredWindowAttributes` alone does nothing on a WinUI 3 window.
  The style is removed and alpha reset on close.
- `NVIM_WINUI_FLOAT_OPACITY` (default `0.9`) applies per cell during float compositing,
  **multiplying** the alpha nvim already computed from `winblend` rather than replacing
  it. So `winblend=0` at `0.9` gives 10% see-through, and `winblend=100` stays fully
  transparent whatever the opacity is.

## App icon

`src/App/Assets/` holds the mark: `appicon.ico` (16/24/32/48/64/128/256, embedded into
the exe by `<ApplicationIcon>` and copied next to it so the window can set it on the
`AppWindow` at startup), `appicon.png` (256px master) and `appicon.svg` (vector twin).

The design merges both parents: the **Neovim "N"** (blue left stem + green body, geometry
measured from the official logo — every edge a 45° bevel) on a **Fluent/WinUI squircle
tile** carrying a Windows-blue → Neovim-green gradient. The generator ships fifteen
alternates, `v1`–`v15`; `v15` is the installed one.

Notable geometry: `v10`–`v15` flank the N with white `< >` brackets whose arms sit at
`dx/dy = 0.591`, measured from the WinUI reference. `v10` uses optical sizing — at ≤24px
the brackets are dropped and the N is enlarged (0.68 vs 0.54 of tile height) so the
strokes stay legible at 16×16; `v13`–`v15` follow the same rule while `v12` keeps the
brackets at every size for comparison. With vertical arm ends the cut's height is
`thickness / slope` (0.075 / 0.591 ≈ 0.13 of the tile in `v10`). Measured at 256px: 0
white pixels outside the tile, 0 N/bracket overlap pixels, N↔bracket clearance 23px
(`v10`) / 13px (`v14`) at the closest point, and ≥2px at 32px.

`v7`/`v8`/`v9` reuse v5's tile and split the N along the measured lines: the green
diagonal is the band between L1 (`(57,105)`→`(409,641)`) and L2 (`(155,5)`→`(407,390)`),
clipped at the right stem's left edge `x=407` so stems and diagonal never overlap; the
hollow variant strokes the union silhouette `N_OUTLINE`.

Regenerate or switch variant (Pillow + numpy via `uv`; no other toolchain needed):

```sh
uv run --with pillow --with numpy --python 3.12 python tools/make_icon.py --install v15    # -> src/App/Assets/*
uv run --with pillow --with numpy --python 3.12 python tools/make_icon.py --preview ./out   # contact sheet, all variants
```

The generator is tracked in git, so a fresh clone can regenerate the icon assets. Only its
build output is ignored (`bin/`, `obj/`, `__pycache__/`).

## Development tools (`tools/`)

Diagnostic helpers kept in version control. None of them are needed to build or run the
app, and none of them are referenced by the app itself — `NvimWinUISolution.sln` does
reference `rpc-test`, so building the solution builds it too. Their `bin/`, `obj/` and
`__pycache__` output is git-ignored:

- `rpc-test/` — console harness that proves NvimCore against a **real** nvim: handshake +
  `nvim_get_api_info`, `nvim_eval`, `nvim_ui_attach` with redraw notification capture.
  Byte logging for stream forensics (`NVIM_LOG_BYTES=1`, `NVIM_LOG_FILE`). Expected
  output: `TEST1`/`TEST2` PASS, redraw batches from `nvim_ui_attach`,
  `[rpc-test] DONE: SUCCESS`.
- `hl-probe/` — highlight probe: attaches to a live session, captures `grid_line` events
  and reports per-row hl spans (colStart..lastCol, trailing-blank bg ids) to decide
  whether full-width bands come from nvim or need GUI-side extension. Tracked, but not
  listed in the solution — build it directly with
  `dotnet run --project tools/hl-probe/HlProbe.csproj`.
- `CheckExeTimestamp.ps1` — one-off check that the built exe is newer than the sources it
  was compiled from (stale-exe diagnosis).

## Verification status

- Decoder covers the full msgpack spec used by nvim: fixints, ints/uints all widths,
  str/bin 8/16/32, ext 8/16/32, float32/64 (big-endian), arrays/maps. Live capture of a
  full session replays through the C# decoder with 0 pending bytes.
- `:checkhealth blink.cmp` rendering matches neovide pixel-for-pixel on the reference
  case: row 1 explicit bg band (`0x44495E`) and row 2 `reverse` band (Normal fg
  `#63718B`), verified by protocol capture + screenshot pixel sampling (2026-09-26).
- Italic/bold/bold-italic rendering verified on a live session: whole-line and mixed-span
  highlight groups render with the correct slant/weight per span, confirmed by
  full-canvas snapshot inspection (2026-09-26).
- API-coverage work verified against live nvim 0.12.5 (2026-09-29): `:set
  guifont=Consolas:h20` reaches the app as `option_set` and is applied (font size 21.33 →
  26.67 in the log); 191 `flush` events handled with 0 watchdog trips and 0 criticals
  across a scroll/split/input stress run; the rendered canvas measured 19.5%
  non-background pixels with correctly aligned glyphs.
- Opacity verified end-to-end: parent alpha read back cross-process matches the setting
  (8 cases), and float `winblend=30` yields alpha 178/160/89 at opacity 1.0/0.9/0.5 while
  `winblend=100` stays 0.
- Cursor verified on a live reverse-video band: the block renders as the band's own
  background with the glyph inverted, matching the adjacent-row background.
- Input verified against a live nvim 0.12.5 with the user's config: `hello` round-trips,
  Backspace removes one character, Enter opens exactly one line, Tab forwards once, Esc
  leaves insert mode once, and IME commits arrive exactly once each — in runs, interleaved
  with plain keys, and across repeated compose-then-Esc cycles. nvim itself emits zero
  `bell`/`visual_bell` for any of them, so the sound was never Neovim's.
