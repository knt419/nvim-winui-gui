// WT-shaped TSF composition host — wiring.
//
// This is the path that actually composes. Verified end-to-end in an out-of-tree probe on this
// machine, same session, same IME:
//
//   CoCreateInstance(CLSID_TF_ThreadMgr) -> ActivateEx (S_FALSE is fine: the thread manager is a
//   per-thread singleton and is usually already activated) -> CreateDocumentMgr -> CreateContext
//   -> AdviseSink(ITfContextOwner) + AdviseSink(ITfTextEditSink) -> Push -> AssociateFocus -> SetFocus
//
//   typing "nihongo"  =>  PREVIEW 'ｎ' 'に' 'にｈ' 'にほ' 'にほｎ' 'にほんｇ' 'にほんご'
//   pressing Enter    =>  COMMIT 'にほんご'
//
// The load-bearing differences from the earlier, non-working attempt:
//   * a PLAIN window is the IME target. A RICHEDIT50W child never composed here at all.
//   * the CCW interfaces are declared WITHOUT the IUnknown methods. Redeclaring them shifts every
//     vtable slot by three, which is what made TSF's first inbound call kill the process.
//   * GetStatus reports TS_SS_TRANSITORY | TS_SS_NOHIDDENTEXT (CUAS / IMM32 emulation on).
//
// The context is never allowed to accumulate text: after a commit we erase it, so "text in the
// context" is always exactly the current composition.

using System;
using System.Runtime.InteropServices;

namespace NvimWinUIGui;

public partial class MainWindow
{
    private TsfCompositionHost? _tsfHost;

    /// <summary>Attach the TSF composition host to the top-level window (no-op if already up).</summary>
    private void TsfHostAttach()
    {
        if (_tsfHost != null) return;
        try
        {
            IntPtr hwnd = GetTopLevelHwnd();
            if (hwnd == IntPtr.Zero) { ImeTrace("TSFHOST: no top-level HWND yet"); return; }

            var host = new TsfCompositionHost(hwnd, TsfHostCaretRect, TsfHostViewportRect);
            host.Trace += m => ImeTrace("TSFHOST " + m);
            host.PreviewChanged += TsfHostPreview;
            host.TextCommitted += TsfHostCommit;

            if (!host.Attach()) { ImeTrace("TSFHOST: attach failed, staying on the IMM32 path"); return; }
            _tsfHost = host;
            ImeTrace("TSFHOST: attached to hwnd=0x" + hwnd.ToString("X"));
            TsfImeApplyModePolicy();     // the nvim mode decides whether the IME may take keys
        }
        catch (Exception ex) { ImeTrace("TSFHOST attach threw " + ex.GetType().Name + ": " + ex.Message); }
    }

    // ---- IME vs nvim mode ----------------------------------------------------------------------
    // The IME may only own the keyboard in modes that actually take text: insert / replace and the
    // command line (so `/日本語` and `:e 日本語` work). In every other mode (normal, operator, visual,
    // select, terminal) msctf must not intercept keys, otherwise `d`, `i`, `a`, `:` … get eaten by
    // the IME and the cursor shows kana while the user is issuing commands.
    //
    // mode_change reports the mode_info_set names, measured on this build: "normal",
    // "cmdline_normal", "insert", "visual". Others nvim can send: "operator", "select", "replace",
    // "insert_complete", "cmdline_insert", "cmdline_replace", "terminal".
    private bool _tsfImeAllowed;
    private bool _tsfFocusApplied;   // is the IME currently attached to the keyboard? (avoids churn)

    private bool ImeModeAllowsInput()
    {
        string m = _modeName ?? "";
        return m.StartsWith("insert", StringComparison.Ordinal)
            || m.StartsWith("replace", StringComparison.Ordinal)
            || m.StartsWith("cmdline", StringComparison.Ordinal);
    }

    /// <summary>Re-apply the "may the IME own the keyboard in this mode" decision.</summary>
    private void TsfImeApplyModePolicy()
    {
        try
        {
            if (_tsfHost == null) return;
            if (ImeModeAllowsInput())
            {
                if (!_tsfImeAllowed) ImeTrace("IME POLICY: mode '" + _modeName + "' -> IME enabled");
                _tsfImeAllowed = true;
                TsfHostFocus();
            }
            else
            {
                if (_tsfImeAllowed) ImeTrace("IME POLICY: mode '" + _modeName + "' -> IME disabled");
                _tsfImeAllowed = false;
                if (_tsfFocusApplied) { _tsfFocusApplied = false; _tsfHost.Unfocus(); }  // terminates a live composition and drops TSF focus
                if (_imePreedit.Length > 0)
                {
                    _imePreedit = "";
                    _imePreeditCursor = -1;
                    _imeComposing = false;
                    ScheduleRender();
                    FlushRender();
                }
            }
        }
        catch { }
    }

    /// <summary>Re-assert TSF focus (window activated / IME target re-focused).</summary>
    private void TsfHostFocus()
    {
        try
        {
            if (_tsfHost == null) return;
            // The mode has the final say: a focus change must not re-attach the IME in normal mode.
            if (!_tsfImeAllowed) { if (_tsfFocusApplied) { _tsfFocusApplied = false; _tsfHost.Unfocus(); } return; }
            // Associate with the HWND that actually owns the keyboard on this thread. WinUI 3 hosts
            // its content in a child site-bridge window, so the top-level HWND is not always the
            // window TSF must be told about. GetFocus is thread-local, which is exactly right here.
            IntPtr focus = GetFocus();
            IntPtr top = GetTopLevelHwnd();
            _tsfHost.FocusOn(focus != IntPtr.Zero ? focus : top);
            _tsfFocusApplied = true;
        }
        catch { }
    }

    /// <summary>Release TSF focus so a composition cannot survive into another app.</summary>
    private void TsfHostUnfocus() { try { _tsfHost?.Unfocus(); } catch { } }

    // While a composition runs this is the inline preedit, drawn at the cursor exactly like the
    // IMM32 path's preedit, so the render side needs no knowledge of which path produced it.
    private void TsfHostPreview(string text)
    {
        _imePreedit = text;
        _imePreeditCursor = text.Length;
        _imeComposing = text.Length > 0;
        ScheduleRender();
        FlushRender();
    }

    // The commit is already-final text: hand it to the same nvim_input path the IMM32 commits use,
    // so nvim never sees a half-written word.
    private void TsfHostCommit(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _imePreedit = "";
        _imePreeditCursor = -1;
        _imeComposing = false;
        ScheduleRender();
        FlushRender();
        ImeTrace("TSFHOST COMMIT '" + text + "'");
        CommitImeText(text);
    }

    // Caret cell in SCREEN pixels — this is what places the IME's candidate window. Same cell math
    // as ImeTrackCursor, inlined here because MainWindow.tsf.cs (which held the old helper) is not
    // part of the build.
    private TsfRect TsfHostCaretRect()
    {
        try
        {
            int row = _curLocalRow, col = _curLocalCol;
            if (_multigridActive) MGridResolveCursor(_curGridId, _curLocalRow, _curLocalCol, out row, out col);
            if (row < 0 || col < 0) return new TsfRect();

            int cx = (int)Math.Round(col * _cellW);
            int cy = (int)Math.Round(row * _cellH);
            int cw = Math.Max(1, (int)Math.Round(_cellW));
            int ch = Math.Max(1, (int)Math.Round(_cellH));

            IntPtr hwnd = GetTopLevelHwnd();
            if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out RECT wr))
            {
                cx += wr.L; cy += wr.T;
            }
            return new TsfRect { Left = cx, Top = cy, Right = cx + cw, Bottom = cy + ch };
        }
        catch { return new TsfRect(); }
    }

    private TsfRect TsfHostViewportRect()
    {
        try
        {
            IntPtr hwnd = GetTopLevelHwnd();
            if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out RECT wr))
                return new TsfRect { Left = wr.L, Top = wr.T, Right = wr.R, Bottom = wr.B };
        }
        catch { }
        return new TsfRect();
    }
}
