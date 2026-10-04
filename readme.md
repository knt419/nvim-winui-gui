<img src="src/App/Assets/appicon.png" width="112" alt="nvim-winui-gui app icon">

# nvim-winui-gui

A WinUI 3 shell around `nvim --embed` (msgpack-RPC). The app spawns a headless
Neovim, attaches the UI over RPC (`ext_linegrid` + `ext_multigrid`), and renders
the grid with a Win2D/Direct2D GPU canvas — no terminal emulator in between.

## Requirements

- .NET 8 SDK
- Neovim v0.12.x on `PATH` (or set `NVIM_WINUI_NVIM` to an explicit path)

## Build & run

```sh
dotnet build src/App/NvimWinUIGui.csproj --nologo
./src/App/bin/x64/Debug/net8.0-windows10.0.22621.0/win-x64/NvimWinUIGui.exe
```

To build everything, including the `tools/rpc-test` diagnostic harness, build the
solution instead — every project it references is checked in:

```sh
dotnet build NvimWinUISolution.sln --nologo
```

## Fonts

The grid is drawn with whatever `guifont` / `guifontwide` your nvim config sets, so
fonts are configured in Vim, not in the app:

```vim
set guifont=Cascadia Mono:h14
set guifontwide=Yu Gothic:h14
```

Size tokens are in **points**, as in any nvim GUI: `h14`, or a bare `14`. The
canonical form is `Family:Style:Size` (`Cascadia Mono:hbold:12`), but `Style` may
be omitted, so `OperatorMono Nerd Font:h16` works too.

- `guifont` sets the normal (narrow) font, `guifontwide` the CJK font. If
  `guifontwide` is unset, `guifont` is used for both.
- The default is `Cascadia Mono` 14pt.
- `:set guifont=...` applies **live** — no restart needed. The app re-measures the
  cell and redraws, and the change is picked up by plugins that set `guifont` after
  startup too.
- Only the family and the size are read. `Style` is parsed but not applied, so
  bold/italic come from nvim's highlight groups rather than from `guifont`.
- If your config sets `guifont` in a lazily-loaded plugin, the app re-reads it
  about a second after startup to catch that.

Fallback chains are appended automatically so nothing renders as tofu (□):

| Purpose | Chain |
|---|---|
| narrow (`guifont`) | `Cascadia Mono, Consolas, Segoe UI Symbol` |
| wide (`guifontwide`) | `Cascadia Mono, Consolas, Yu Gothic, Meiryo, MS Gothic, Segoe UI Emoji, Segoe UI Symbol` |

Emoji-presentation cells (✅ ⚠ ❤ and any VS16/ZWJ sequence) bypass these chains
and take a dedicated colour-emoji path, because DirectWrite's automatic fallback
would render them monochrome and out of grid.

## Line spacing

Vertical row pitch is trimmed by an app-level setting, not by nvim's `linespace`:

```sh
NVIM_WINUI_LINESPACE=2   # trim 2px from each row
```

`0` (the default) keeps the font's natural box height. This is separate from nvim's
own `linespace`, which is reported to the UI and reflected in the grid geometry.

## Settings window

The gear button in the title bar — immediately left of the minimize button — opens the
in-app settings panel. Everything in the variable table below can be changed there, with no
environment variable needed. The panel writes
`%LOCALAPPDATA%\NvimWinUIGui\settings.json` (readable, one `"NAME": "value"` per setting,
hand-editable).

| Action | Key |
|---|---|
| Open / close | the gear button, `Esc` to close |
| Move the selection | `Up` / `Down`, or the mouse wheel |
| Change a value | `Left` / `Right`, `Enter` / `Space`, or click the arrows beside the value |
| Edit a text row (path, args) | `Enter`, type, `Enter` to save, `Esc` to cancel |
| Reset every stored setting | `Ctrl+R` |

Changes apply immediately, except rows marked `(restart)` — those are read once at startup.
A dot in front of a row's name means an environment variable is set for it, and that
variable **wins over the file**: the panel still stores your value, but it cannot take
effect until the variable is gone.

## Environment variables

Every variable below is also editable from the settings panel above.

| Variable | Default | Effect |
|---|---|---|
| `NVIM_WINUI_NVIM` | — | Explicit path to `nvim.exe`. If unset, nvim is resolved from `PATH`, then the default install dir. |
| `NVIM_WINUI_ARGS` | — | Extra arguments appended to the nvim command line (e.g. `+checkhealth blink.cmp`). |
| `NVIM_WINUI_LINESPACE` | 0 | Pixels trimmed from each row's vertical pitch (tighter line spacing). `0` keeps the natural box height. |
| `NVIM_WINUI_SNAP` | off | Set `1` to force the window to a whole number of cells. By default the window stays at whatever size you drag it to and the grid reflows to fit, so up to one cell of background shows at the right/bottom edge. |
| `NVIM_WINUI_STATUSBAR` | off | Show the app status bar row (hidden by default; set to `1`). |
| `NVIM_WINUI_OPACITY` | 1.0 | Opacity of the whole parent window, 0..1 (`1` = fully opaque, `0` = invisible). Applied at the Win32 level, so the desktop shows through. Accepts a bare percentage too (`90` = `0.9`). The default `1.0` leaves the window unlayered, costing nothing. |
| `NVIM_WINUI_FLOAT_OPACITY` | 0.9 | Opacity of floating windows only, 0..1. Default `0.9` = 10% see-through. **Multiplies** nvim's `winblend`: a float with `winblend=0` still shows 10% of the parent, and `winblend=100` stays fully transparent. |
| `NVIM_WINUI_FLOAT_BLUR` | 6.0 | Gaussian blur radius (DIP) applied to the parent layer while a floating window is up, so the float reads as focused foreground. `0` disables. |
| `NVIM_WINUI_IMEPREEDIT_HL` | `Normal` | nvim highlight group used for the app-drawn IME preedit (composition text, underline and caret). Any built-in group name resolves through nvim's own table, so `:hi` on that group, a `:hi link`, and colorscheme switches all reach the preedit. `Normal` follows the terminal convention and leaves the look unchanged; `IncSearch` / `Cursor` / `Visual` make the composition visually distinct. |
| `NVIM_WINUI_IMEPREEDIT_TEST` | off | Diagnostic: draw this text as the IME composition without a real IME session, so the inline preedit can be checked in scripts and screenshots (position, layer, colours). Input handling is unaffected — it keys off the real composition state, never this. |
| `NVIM_WINUI_SCROLL_MS` | 120 | Smooth-scrolling animation length in ms, driven by nvim's `win_viewport` `scroll_delta`. `0` disables it, so every scroll jumps like before. Scrolls longer than 6 lines stay instant regardless — nvim reports those deltas as approximate, and animating a screen-sized jump reads as a slide. |
| `NVIM_WINUI_SCROLL_FREEZE` | off | Diagnostic: hold every smooth-scroll animation at this phase (0..1) instead of letting it advance. The app's own `NVIM_WINUI_SHOT=1` capture only fires every 30th render, so this is what makes a mid-animation frame reproducible when checking the overlay's geometry. |
| `NVIM_WINUI_DIAG` | off | Diagnostic logging (RPC trace, redraw/resize events, every `nvim_input`) to `%LOCALAPPDATA%\NvimWinUIGui\startup.log`. Off by default; rare fatal errors are always logged regardless. |
| `NVIM_WINUI_SHOT` | off | With `NVIM_WINUI_DIAG=1`, save a full-canvas snapshot of the live composite to `%LOCALAPPDATA%\NvimWinUIGui\fullshot.png` every 30th render — for pixel-level inspection without screen capture. |
| `NVIM_WINUI_SELFTEST` | off | Run the startup self-test (types text and creates a test buffer). Diagnostics only — pollutes your session, so keep it off in normal use. |

## Troubleshooting

**nvim not found** — the status bar says so on launch. Set `NVIM_WINUI_NVIM` to the
full path of `nvim.exe`, or add its directory to `PATH`.

**Text renders as boxes (tofu)** — the configured font has no glyph for those
characters. Full-width Japanese needs `guifontwide` set to a CJK family (e.g.
`Yu Gothic`); the built-in fallback only covers what DirectWrite can substitute.

**Font size does not change** — the app reads `guifont` at startup, again about a
second later, and on every `option_set`. If your config sets it later than that
(lazily-loaded plugin, or on a `BufEnter` autocmd), use `:set guifont=...` once by
hand; from then on changes are live.

**The window is see-through** — `NVIM_WINUI_OPACITY` / `NVIM_WINUI_FLOAT_OPACITY`
are multipliers, not overrides. Check that they are not set to something below `1.0`.

**A setting changed in the panel does nothing** — an environment variable is set for it
(the row shows a dot in front of its name, and the footer of the panel says so). The
environment variable overrides `settings.json`, so unset it, or the row stays ineffective.
Rows marked `(restart)` additionally need a relaunch.

For architecture, protocol coverage, and development notes, see
[development.md](development.md).
