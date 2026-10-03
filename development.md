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
  - `Tsf/TsfCompositionHost.cs` — the IME target: a *transitory* TSF context
    (`ITfContextOwner` + `ITfContextOwnerCompositionSink` + `ITfTextEditSink`), shaped like
    Windows Terminal's `src/tsf/Implementation.cpp`. Attach/focus/detach, composition
    termination, and reading the composition text out of the context all live here.
  - `MainWindow.tsfhost.cs` — IME wiring: attaching to the input-site HWND, the mode policy,
    preedit → inline render, commit → `nvim_input`.
  - `MainWindow.ime.cs` — legacy IMM32 / RICHEDIT50W composition target, now only the fallback
    path for a machine where TSF cannot be created (see "Input and IME").

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

## Input and IME

The grid is a Win2D surface with no text-input concept, so the OS IME needs a real target. Since
commit `299ff94` that target is a **TSF composition host** shaped like Windows Terminal's
(`src/tsf/Implementation.cpp`): a *transitory* `ITfContext` on the window that actually receives
keys, with no native text control involved at all.

`MainWindow.ime.cs` still carries the older IMM32 / RICHEDIT50W path, but only as a fallback for a
machine where TSF cannot be created. RICHEDIT50W never composed here: with the 1x1 RichEdit target
focused, typing produced `WM_KEYDOWN` with no `WM_IME_STARTCOMPOSITION`/`WM_IME_COMPOSITION`, and the
romaji landed in the control as plain text.

Everything below lives in `Tsf/TsfCompositionHost.cs` (the host) and `MainWindow.tsfhost.cs` (the
wiring: preedit → inline render, commit → `nvim_input`, mode policy).

### The TSF shape

`CoCreateInstance(CLSID_TF_ThreadMgr)` → `ITfThreadMgrEx::ActivateEx` → `CreateDocumentMgr` →
`CreateContext` (transitory) → `AdviseSink(ITfContextOwner)` + `AdviseSink(ITfTextEditSink)` →
`Push`, then `AssociateFocus(hwnd, docMgr)` + `SetFocus(docMgr)`. The host implements
`ITfContextOwner`, `ITfContextOwnerCompositionSink`, `ITfTextEditSink` and one `ITfEditSession`;
composition callbacks request an edit session (`TF_ES_READWRITE | TF_ES_ASYNC`) and read the text
out of the context there.

Three facts are load-bearing:

- **No `ITextStoreACP`.** Windows Terminal does not implement one either, and the hand-rolled .NET
  store is what used to crash. With a transitory context TSF owns the text: we read the composition
  out of the context (`GetStart` → `ShiftEnd(LONG_MAX)` → `GetText`) and erase it
  (`SetText(ec, 0, NULL, 0)`) once it has been handed to nvim. Erasing after every commit is what
  makes "text in the context" mean "the current composition", so WT's `GUID_PROP_COMPOSING`
  bookkeeping is not needed.
- **`GetStatus` must report `TS_SS_TRANSITORY | TS_SS_NOHIDDENTEXT`.** The transitory flag turns on
  CUAS (the IMM32 emulation layer); without it the IME does not engage at all.
- **`ActivateEx` returning `S_FALSE` is success, not failure.** The thread manager is a per-thread
  singleton and is usually already activated; treating 1 as an error is why an earlier host
  silently never attached.

Vtable slot numbers come from the SDK header, extracted mechanically — never from memory
(`Windows Kits\10\Include\<ver>\um\msctf.h`; `IUnknown` occupies slots 0..2, so each interface's
first listed method is slot 3). Interfaces that are only *called* are driven through raw slots
(`Marshal.GetDelegateForFunctionPointer`), which sidesteps `[ComImport]` marshalling.

**The CCW rule that cost a process per attempt: an interface that a managed class IMPLEMENTS must
not declare `QueryInterface`/`AddRef`/`Release`.** The CLR builds a CCW's IUnknown slots itself, so
redeclaring them shifts every method by three — TSF then calls the wrong slot and the process dies
on the first inbound call, with no managed trace to show for it.

### Which HWND

A WinUI 3 desktop app has two handles and only one of them receives keys: the top-level window
(`WinUIDesktopWin32WindowClass`) and the child that actually takes input (`InputSiteWindowClass`).
`GetFocus()` normally returns the child but intermittently reports the top-level — notably on the
focus blip when a floating grid (the completion popup) appears — and TSF associated with the
top-level leaves the app receiving no keys until the grid is clicked again. `TsfResolveInputHwnd()`
caches the child and never falls back to the top-level while it is alive, and `FocusOn()` is a no-op
when the target is unchanged, so the per-keystroke focus reclaim in the XAML key path no longer
costs a synchronous `AssociateFocus` + `SetFocus` per key.

### Mode gating

The IME may only own the keyboard in modes that take text — `insert*`, `replace*`, `cmdline*` (so
`/日本語` and `:e 日本語` work). Everything else (`normal`, `operator`, `visual`, `select`,
`terminal`) detaches it; otherwise `d`, `i`, `a`, `:` are eaten and leaving insert with the IME in
kana garbles the first command keystroke. Detaching is real rather than cosmetic: a live composition
is terminated via `ITfContextOwnerCompositionServices::TerminateComposition(NULL)` (slot **7** —
that interface derives from `ITfContextComposition`, whose four methods come first), then
`AssociateFocus(hwnd, NULL)` + `SetFocus(NULL)` leaves msctf with no focused document manager, so it
stops routing the thread's keys into the IME. The IME's own あ/A state is deliberately left alone so
returning to insert keeps the user's kana choice.

Ctrl+Space is the IME's own ON/OFF hotkey and TSF delivers it to the IME; the app only guards against
it arriving as a literal space, which would insert a space into nvim.

#### Notes from the retired IMM32 target

Kept because they are IME state-machine facts a future IMM32 path would hit again: one delivery
point per physical press (`WM_KEYDOWN` *and* `WM_CHAR` both arrive); committed text must come from
`GCS_RESULTSTR`, never from the `WM_CHAR` that `DefWindowProc` derives from `WM_IME_CHAR` (measured:
it carries only the **high byte** of the code point, so U+3042/U+65E5/U+3044 arrived as
0x30/0x65/0x30 — printable ASCII, which reads as plausible text in a log rather than as a failure);
`WM_IME_CHAR` must chain under its own id; Esc must abandon the composition; an input context
released by a cancel must be re-associated, because the IME does not restore it; and `imm32.dll` is
the DLL to load, not `ime32.dll`.

### Inline preedit

`DrawImePreedit` (in `MainWindow.render.cs`, called from `RenderCore`) paints the composition at the
cursor cell in the grid's own font, with the composition underline and a caret. Drawn after the
cursor so it sits on top. Three rules came out of using it:

- **Per character, by the glyph's own cell width.** Kana/kanji are 2 cells and ASCII 1, and the
  wide/narrow text format is picked per character. Laying a whole preedit out in one narrow-format
  run draws kana at half advance, so `ああ` overlapped into what read as "あ with a dakuten".
- **Occupy the cells.** Each covered cell is filled with its own background before the glyph is
  drawn, so `list`/`listchars` markers (the `eol:` ↲ sitting under the cursor) no longer show
  through the composition. Per cell, so a CursorLine or a coloured band keeps its exact colours.
- **An empty result still clears.** Backspacing the last preedit character ends the composition with
  *no text*, so the clear must not be conditional on a non-empty commit, or the last preedit stays on
  screen forever.

### Verification status

IME is verified end to end. An out-of-tree probe (`--selftest`) drives the real path and produces
`PREVIEW 'ｎ' → 'に' → 'にほ' → … → 'にほんご'` while typing, then `COMMIT 'にほんご'` on Enter, with
`WM_IME_NOTIFY` traffic and `VK_PROCESSKEY` (0xE5) showing the keystrokes being consumed. In the app
itself `%LOCALAPPDATA%\NvimWinUIGui\ime.log` shows the attach sequence (`CoCreateInstance` … `Push` …
`AssociateFocus`/`SetFocus`), the mode-policy transitions, and `COMMIT` → `SENT-TO-NVIM`. A Japanese
IME must be installed: on Windows 11 it lives in `C:\Windows\System32\IME\IMEJP\IMJPTIP.DLL` (the
`System32\msime.tsf` this file used to reference no longer exists).

**Two earlier conclusions here were wrong and are corrected.** Synthetic input is *not* blocked on
this box — the harness that "proved" it was itself broken: its `INPUT` struct was 24 bytes instead of
the 40 x64 requires (the union is sized by `MOUSEINPUT`, not `KEYBDINPUT`), so `SendInput` returned 0
and nothing was ever injected (the same bug also dropped letters, because a letter's `wVk` is the
UPPERCASE code, `VK_N` = 0x4E, not `'n'` = 0x6E). And "the IME never composes" was a property of the
RICHEDIT50W target, not of the machine: the IME composes as soon as it has a transitory TSF context
to drive.

## API coverage (nvim 0.12.5, `--api-info`: 261 functions / 10 ui_options / 69 ui_events)

- **attach options**: `rgb`, `ext_linegrid`, `ext_multigrid`. The cmdline, completion
  menu, tabline and messages are deliberately NOT externalized (no `ext_cmdline` /
  `ext_popupmenu` / `ext_tabline` / `ext_messages`), so nvim draws them into the grid
  and they need no widget code.
- **ui events handled**: the 15 grid/multigrid/highlight events (`grid_resize`,
  `grid_line`, `grid_clear`, `grid_scroll`, `grid_cursor_goto`, `grid_destroy`,
  `hl_attr_define`, `default_colors_set`, `mode_change`, `mode_info_set`, `win_pos`,
  `win_float_pos`, `win_hide`, `win_close`, `msg_set_pos`) plus `flush` and `option_set`.
- **`grid_destroy` frees state that `win_close` does not** (measured on 0.12.5). nvim
  destroys the *message* grid at every startup and on reflow **carrying live placement
  state** and sends no `win_close`/`win_hide` for it:

  ```
  MSG-POS g=3 row=24 z=200
  GRID-DESTROY g=3 [pos=24 z=200 msg=True focus=False] (grid buffers now 2)
  ```

  Cleanup keyed only on `win_close` therefore left that entry in `_mgrid`, and the render
  composited a ghost message surface over the bottom rows for the rest of the session.
  `MGridDestroy` drops the cell buffer **and** the placement state
  (`PosRow`/`ZIndex`/`Focusable`/`IsMessageGrid`), because grid ids are reused and a stale
  zindex or message flag would put a later window in the wrong layer. Only grid 1 (the outer
  frame, which has no per-grid buffer here) is exempt — grid **0** is tracked for real
  (`msg_set_pos` for grid 0 arrives from nvim), so an `id <= 1` guard would silently skip
  releasing it. The handler logs the state it released: the ordinary split/float/tab
  destroys report `[no live buffer]` because `win_close` ran first, which is exactly why the
  message-grid case is invisible without that field.
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

## Window sizing

The window and the grid convert through two formulas that have to agree, or the window creeps by a
fraction of a pixel on every drag instead of settling:

- `SendNvimResize` counts the grid: `cols = floor((W - chromeW) / cellWidth)`,
  `rows = floor((H - chromeH - statusRow) / rowPitch)`
- `UpdateWindowSize` sets the window: `ceil(cols * cellWidth + chromeW)` x
  `ceil(rows * rowPitch + statusRow + chromeH)`

Three things about that are load-bearing:

- **One cell height.** Both directions use `_rowPitch`. They used to share the *name* `_refCellH`
  while it was re-measured independently for each, so the round trip did not close: solving the
  logged numbers showed the implied per-row height drifting between 29.25 and 30.50 px within a
  single drag. `_refCellH` (the raw measured font box) only seeds `_rowPitch`.
- **The chrome is fractional.** `_chromeW`/`_chromeH` are doubles. At 100% the frame is 16.0 DIP,
  but at 150% it is 13.33, and rounding it to 13 cost a third of a DIP on every width and height
  computation. It is also added *before* the ceiling, not after: `Ceil(cols*cw) + 16.0` and
  `Ceil(cols*cw + 16.0)` agree at 100% and differ as soon as the frame is a fraction.
- **`ceil`, not `round`, when snapping.** A `cols*cw` that rounds down reads back as `cols-1`
  through the `floor` above, and each `grid_resize` reply then shrinks the window by one more cell.

Snapping itself is off by default (`NVIM_WINUI_SNAP=1` turns it on). With it off,
`UpdateWindowSize` never resizes the window and the canvas is sized to the content area instead of
the grid, so the cells past the end of the grid are simply unpainted. With it on, the window is
forced to the nearest cell boundary, which means a drag to 1060x430 lands on 1053x414 — 81 columns
plus the 16px frame. That is the snap working, not a shrink.

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
- IME verified in the app on 2026-10-03 on the TSF path: `nihongo` composes with the preedit drawn
  inline at the cursor and commits to nvim as `にほんご`; the IME is attached only in
  insert/replace/cmdline modes and detached — live composition terminated — everywhere else; typing
  continues straight through the completion popup appearing, which previously needed a mouse click
  because `GetFocus()` had reported the top-level window instead of the `InputSiteWindowClass` child
  TSF must be associated with.
