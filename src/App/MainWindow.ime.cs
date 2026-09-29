// IME (input method editor) support.
//
// The grid is painted on a Win2D CanvasControl — a DirectComposition surface with no notion of
// text input. The OS IME needs a real "target window" to compose into and to anchor its candidate
// list to; a canvas gives it neither, so before this, Japanese input produced nothing at all.
//
// Two XAML-based approaches were tried first; both are dead ends in THIS build:
//
//  1. Subclassing the WinUI 3 window proc to intercept WM_IME_*. Crashes with
//     STATUS_STOWED_EXCEPTION (0xC000027B) inside Microsoft.UI.Xaml.dll: WinUI routes its own
//     native input through a managed window proc, and native<->managed transitions through a XAML
//     window proc are not supported.
//
//  2. A hidden 1x1 XAML TextBox as the IME target. Sound in a normal WinUI app, but this project
//     builds pure-C# (EnableCoreMrtTooling=false, zero XAML items), so no PRI/XBF pipeline ever
//     compiles WinUI's Themes/generic.xaml. Any control that resolves a template via
//     DefaultStyleKey therefore throws COMException 0x80004005 "要素が見つかりません" (element not
//     found) the moment it is constructed, and new XamlControlsResources() throws the same way
//     because it is itself a templated resource lookup. Verified by bisect: TextBlock, Grid, Border,
//     ContentControl and Button all construct fine; only template-backed controls fault.
//
// So the target is a plain Win32 EDIT control, created as a child of the window's own top-level
// HWND — the same trick conhost and other terminal emulators use. It needs no XAML templates, and
// the OS IME treats it as a first-class composition target: composition, the candidate window and
// the commit all work natively, and the IME anchors its candidate list to the edit control's own
// caret, so the list follows the cursor position with no manual tracking.
//
// It is a child of the *top-level HWND*, not of the XAML island: a child of a real Win32 window is
// an ordinary child control and takes real keyboard focus, whereas anything inside the XAML island
// It is 1x1 px and hidden under the canvas, so it is never actually seen.
//
// Because the EDIT owns keyboard focus, the XAML KeyDown path never fires while it is attached, so
// the subclassed proc also forwards ordinary nvim keys (see ForwardNvimKey). And because the EDIT's
// caret is 1x1 at the origin, the target window itself is moved to the nvim cursor on every
// cursor/metrics change (ImeTrackCursor) — the OS anchors the candidate list to the focused target,
// so moving the target is what makes the list follow the cursor.
//
// We harvest committed text through the EDIT's own (subclassed) window proc:
//
//   - While a composition is in flight the control holds preedit text; we must not forward that,
//     and keys must not double-insert the keystrokes that drove the IME.
//   - Text reaches us twice: WM_IME_CHAR, and the synthetic WM_CHAR the EDIT generates from it.
//     WM_CHAR is the single delivery point, so a commit lands exactly once.
//   - After forwarding we clear the control, so each composition starts from empty.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace NvimWinUIGui;

public partial class MainWindow
{
    private const int ImeEditId = 0x1F0E;

    private IntPtr _imeEdit;                    // HWND of the native EDIT control
    private IntPtr _imeOwner;                   // top-level HWND that owns it
    private IntPtr _imeEditPrevProc;            // the EDIT's original wndproc
    private ImeEditProc? _imeEditProcKeepAlive; // keep the delegate alive; a collected thunk crashes
    private bool _imeAttachTried;               // never retry: a failed attach would leak HWNDs
    private bool _imeComposing;                 // IME is mid-composition
    private string _imeLastForwarded = "";      // guards against re-forwarding the same commit
    private int _imeTrackedX = -1, _imeTrackedY = -1; // last SetWindowPos, to skip redundant moves

    // Win32 messages / notifications / styles.
    private const int WM_SETTEXT = 0x000C;
    private const int WM_CHAR = 0x0102;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_NCDESTROY = 0x0082;
    private const int WM_IME_COMPOSITION = 0x0284;
    private const int WM_IME_CHAR = 0x0286;
    private const int WM_IME_ENDCOMPOSITION = 0x028E;
    private const int EM_SETSEL = 0x00B1;
    private const int GCS_RESULTSTR = 0x0001;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_TABSTOP = 0x00010000;
    // ES_MULTILINE (0x0004): see ImeAttach — a single-line EDIT bells on the IME's commit Enter.
    private const int ES_MULTILINE = 0x0004;
    private const int EM_SETLIMITTEXT = 0x00C5;   // (WM_USER + 45)
    private const uint SWP_NOZORDER = 0x0004;   // keep the EDIT behind the canvas, not reordered
    private const uint SWP_NOACTIVATE = 0x0010; // moving it must not steal/steal-back focus
    private const int GWLP_WNDPROC = -4;

    private delegate IntPtr ImeEditProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string? className, string? windowName,
        uint style, int x, int y, int w, int h, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int index, IntPtr newLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProcW(IntPtr prev, IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    // --- Opacity ------------------------------------------------------------------------------
    // Two Win32 details decide whether this works at all, and both were found by measurement:
    //
    //  1. WS_EX_LAYERED must be set BEFORE SetLayeredWindowAttributes. Without it the call
    //     succeeds but does nothing: the window stays non-layered and fully opaque
    //     (GetLayeredWindowAttributes then fails outright). Verified on the live window —
    //     adding the style first made alpha 230/200/128 all read back exactly.
    //  2. SetLayeredWindowAttributes is the supported knob even though the window is a
    //     DirectComposition target. The compositor path (ICompositorInterop::SetWindowAlpha) is
    //     NOT reachable from here: DCompositionCreateDevice rejects that IID with E_NOINTERFACE
    //     (0x80004002), so it can never be used from an unpackaged app.
    //
    // LWA_ALPHA scales the whole window's alpha. LWA_COLORKEY is only paired in at alpha 0, where
    // a 0-alpha window would otherwise still show its non-transparent pixels.
    private const uint LWA_COLORKEY = 0x00000001;
    private const uint LWA_ALPHA = 0x00000002;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, int crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLongPtr(IntPtr hWnd, int nIndex, int dwNewLong);

    // Set the whole window's opacity (0..1).
    private bool SetWindowAlphaSafe(IntPtr hwnd, double alpha)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return false;
            int a = (int)Math.Clamp(Math.Round(alpha * 255.0), 0, 255);
            // Step 1: the window must be layered, or the attribute below is a no-op.
            int ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            if ((ex & WS_EX_LAYERED) == 0)
            {
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED);
                _parentLayered = true;
            }
            // Step 2: now the attribute sticks. At alpha 0, also color-key so the window really
            // disappears instead of leaving a ghost of its own pixels.
            return SetLayeredWindowAttributes(hwnd, 0, (byte)a, LWA_ALPHA | (a == 0 ? LWA_COLORKEY : 0));
        }
        catch (Exception ex) { if (_diagEnabled) LogStartup("opacity: SetWindowAlpha failed: " + ex.Message); return false; }
    }
    private bool _parentLayered;

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    // --- IME input context (OPTIONAL, loaded dynamically) ---------------------------------
    // ime32.dll is ABSENT on some Windows installs (verified on this box: not in System32 nor
    // SysWOW64). A static [DllImport] then throws DllNotFoundException on FIRST USE — which, in
    // the original code, happened inside the focus path and silently skipped SetFocus entirely,
    // leaving the app with no IME target at all. Worse, the exception was swallowed, so the only
    // symptom was "the IME does not work".
    //
    // So ime32 is loaded LAZILY and probed once. When it is missing, the app says so and keeps
    // working: the synthetic WM_CHAR that the EDIT generates for a committed character is the
    // single delivery point to nvim, so committed text still arrives even without the context.
    // Only the preedit/result-string read (WM_IME_COMPOSITION GCS_RESULTSTR) degrades.
    private static class Ime32
    {
        // ImmGetContext / ImmAssociateContext / ImmReleaseContext / ImmGetCompositionStringW
        internal delegate IntPtr ImmGetContextFn(IntPtr hWnd);
        internal delegate IntPtr ImmAssociateContextFn(IntPtr hWnd, IntPtr hIMC);
        internal delegate bool ImmReleaseContextFn(IntPtr hIMC);
        internal delegate int ImmGetCompositionStringWFn(IntPtr hIMC, int index, StringBuilder? buf, int len);

        internal static readonly ImmGetContextFn? GetContext;
        internal static readonly ImmAssociateContextFn? AssociateContext;
        internal static readonly ImmReleaseContextFn? ReleaseContext;
        internal static readonly ImmGetCompositionStringWFn? GetCompositionStringW;
        internal static readonly bool Available;

        static Ime32()
        {
            try
            {
                IntPtr h = System.Runtime.InteropServices.NativeLibrary.Load("ime32.dll");
                // Hold the module handle for the process lifetime: the delegates below point INTO
                // it, so unloading it would leave them jumping into freed memory.
                _module = h;
                GetContext = Marshal.GetDelegateForFunctionPointer<ImmGetContextFn>(
                    System.Runtime.InteropServices.NativeLibrary.GetExport(h, "ImmGetContext"));
                AssociateContext = Marshal.GetDelegateForFunctionPointer<ImmAssociateContextFn>(
                    System.Runtime.InteropServices.NativeLibrary.GetExport(h, "ImmAssociateContext"));
                ReleaseContext = Marshal.GetDelegateForFunctionPointer<ImmReleaseContextFn>(
                    System.Runtime.InteropServices.NativeLibrary.GetExport(h, "ImmReleaseContext"));
                GetCompositionStringW = Marshal.GetDelegateForFunctionPointer<ImmGetCompositionStringWFn>(
                    System.Runtime.InteropServices.NativeLibrary.GetExport(h, "ImmGetCompositionStringW"));
                Available = true;
            }
            catch
            {
                Available = false;   // no ime32 on this system; the app still works without it
            }
        }

        // Intentionally never freed: the delegates above are only valid while the module is loaded.
        private static IntPtr _module;
    }

    // Create the hidden EDIT the IME composes into and subclass it. Called once the window is
    // realized, the first point a top-level HWND exists. Runs at most once per process: retrying
    // after a failure would leak a window handle on every attempt.
    private void ImeAttach()
    {
        if (_imeAttachTried) return;
        _imeAttachTried = true;
        try
        {
            IntPtr owner = GetTopLevelHwnd();
            if (owner == IntPtr.Zero) { if (_diagEnabled) LogStartup("IME: no top-level HWND yet"); return; }

            // ES_MULTILINE matters for two reasons, both observed:
            //  - A single-line EDIT refuses the Enter that an IME sends to commit, and Windows
            //    rings the bell for a refused keystroke. Multiline accepts it.
            //  - Multiline also makes the control accept text at any caret position instead of
            //    belling when a commit would not fit, which is what made the beep intermittent:
            //    it rang only for the commits that happened to overflow.
            // EM_SETLIMITTEXT to 64 keeps the clear cheap regardless.
            _imeEdit = CreateWindowExW(0, "EDIT", "", WS_CHILD | WS_VISIBLE | WS_TABSTOP | (uint)ES_MULTILINE,
                0, 0, 1, 1, owner, (IntPtr)ImeEditId, GetModuleHandleW(null), IntPtr.Zero);
            if (_imeEdit == IntPtr.Zero)
            {
                if (_diagEnabled) LogStartup("IME: CreateWindowExW(EDIT) failed err=" + Marshal.GetLastWin32Error());
                return;
            }
            SendMessageW(_imeEdit, EM_SETLIMITTEXT, new IntPtr(64), IntPtr.Zero);

            // Subclass so the IME/char messages reach us. The delegate is held in a field: the
            // window references only the thunk, and a collected delegate means the next message
            // jumps into freed memory.
            _imeEditProcKeepAlive = ImeEditProcThunk;
            _imeEditPrevProc = SetWindowLongPtrW(_imeEdit, GWLP_WNDPROC,
                Marshal.GetFunctionPointerForDelegate(_imeEditProcKeepAlive));
            if (_imeEditPrevProc == IntPtr.Zero)
            {
                // Subclassing failed. The EDIT would still swallow keystrokes with no way for us to
                // read them, so destroy it rather than leave an invisible orphan in the input path.
                if (_diagEnabled) LogStartup("IME: SetWindowLongPtrW failed err=" + Marshal.GetLastWin32Error());
                DestroyWindow(_imeEdit);
                _imeEdit = IntPtr.Zero;
                return;
            }

            _imeOwner = owner;
            if (_diagEnabled) LogStartup("IME: EDIT target attached hwnd=0x" + _imeEdit.ToString("X") +
                                          " owner=0x" + owner.ToString("X"));
            // Focus now, not only on Activated: at launch the window is activated BEFORE Loaded, so
            // the Activated handler has already run and the target would never get focus.
            ImeFocusTarget();
        }
        catch (Exception ex) { if (_diagEnabled) LogStartup("IME attach failed: " + ex.Message); }
    }

    // Top-level HWND for this Window. WinUI 3's Window exposes no Handle; the interop helper is
    // the supported way to reach it, and it only works once the window is realized.
    private IntPtr GetTopLevelHwnd()
    {
        try { return WinRT.Interop.WindowNative.GetWindowHandle(this); }
        catch { return IntPtr.Zero; }
    }

    private IntPtr ImeEditProcThunk(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        // Set when a commit was harvested from this message. The EDIT is cleared AFTER the original
        // proc has run, not before: the proc is what actually inserts the character into the
        // control, so clearing first would let it re-insert and the text would be forwarded twice.
        bool clearAfter = false;
        // Keys we forward to nvim as commands must NOT reach the EDIT's own proc. The control is
        // single-line and permanently empty (cleared after every commit), so it cannot act on
        // Backspace/Enter/Tab/Esc/Delete/arrows: Windows rings the bell for a keystroke a control
        // refuses. Consuming them here keeps the beep out while nvim still gets the key.
        bool suppressOriginal = false;
        try
        {
            switch (msg)
            {
                case WM_IME_COMPOSITION:
                {
                    // Any composition update means preedit, not committed input.
                    _imeComposing = true;
                    if ((lParam & GCS_RESULTSTR) != 0)
                    {
                        string committed = ImeCompositionResultString();
                        if (committed.Length > 0) clearAfter = CommitImeText(committed);
                    }
                    break;
                }
                case WM_IME_ENDCOMPOSITION:
                {
                    _imeComposing = false;
                    break;
                }
                case WM_IME_CHAR:
                {
                    // A char with no preceding GCS_RESULTSTR: an ASCII key typed through the IME, or
                    // an IME that commits without setting GCS_RESULTSTR. wParam is one UTF-16 unit;
                    // a surrogate pair arrives as two messages and appending in order reproduces it.
                    // Lone surrogates are skipped: a valid character never is one.
                    //
                    // This is DELIBERATELY not forwarded here. The EDIT's own proc turns a
                    // WM_IME_CHAR into a synthetic WM_CHAR carrying the same character, so handling
                    // both would send every committed character to nvim twice (observed: '日' twice).
                    // The WM_CHAR below is the single delivery point, so there is nothing to send here.
                    //
                    // It MUST reach the original proc under its OWN id. The IME state machine keys
                    // off the message id: relaying it as WM_CHAR (tried in 9eeda33) leaves the IME
                    // believing the commit was never acknowledged, so the first composition never
                    // reaches nvim and the IME then stops responding altogether.
                    //
                    // Nothing is done with the char here, and nothing needs to be: the committed
                    // text is delivered by the WM_CHAR the control generates, which is the single
                    // delivery point. This message exists only to tell the IME it was heard.
                    break;
                }
                case WM_KEYDOWN:
                {
                    // The IME target holds keyboard focus, so XAML's KeyDown never fires and every
                    // ordinary nvim key would be lost. Non-printable keys are handled here; printable
                    // ones are NOT, because Windows pairs every WM_KEYDOWN with a WM_CHAR carrying the
                    // same character -- forwarding both would send each keystroke to nvim twice.
                    // While the IME is composing, keys belong to the IME and nothing is forwarded.
                    if (!_imeComposing)
                    {
                        ForwardNvimKey(wParam, lParam, printable: false);
                        // Every non-printable key is consumed here: the EDIT is empty and single-line,
                        // so it can only refuse it, and a refused key rings the bell.
                        suppressOriginal = true;
                    }
                    break;
                }
                case WM_CHAR:
                {
                    // The single delivery point for text. Reached from a real keyboard (IME off, or a
                    // key the IME does not consume) and from the synthetic WM_CHAR the EDIT generates
                    // for a committed IME character, so plain typing and committed kanji share one path.
                    if (wParam == 0x1B)                       // <Esc> arrives as a char too
                    {
                        // Already forwarded from WM_KEYDOWN: forwarding it again here sent <Esc>
                        // to nvim twice per press. Just consume the char.
                        suppressOriginal = true;
                        break;
                    }
                    if (wParam == 0x0D || wParam == 0x0A) // <CR> arrives as a char, not a keydown
                    {
                        if (!_imeComposing) { ForwardToNvim("<CR>"); suppressOriginal = true; }
                        break;
                    }
                    if (wParam == 0x09)                     // <Tab> likewise
                    {
                        if (!_imeComposing) { ForwardToNvim("<Tab>"); suppressOriginal = true; }
                        break;
                    }
                    // Every other control char (^H/BS, ^G, ^C, DEL...) was already forwarded from
                    // WM_KEYDOWN. Forwarding here too would double it, so only consume: the EDIT is
                    // empty and single-line, so it would refuse the char and ring the bell.
                    if (wParam < 0x20 || wParam == 0x7F)
                    {
                        suppressOriginal = true;
                        break;
                    }
                    if (wParam >= 1 && wParam <= 0xFFFF && (wParam & 0xF800) != 0xD800)
                    {
                        string ch = ((char)wParam).ToString();
                        // Forward it, then CONSUME the insert — for a plain keystroke and for an IME
                        // commit alike.
                        //
                        // The plain case is the one that used to ring: the EDIT is a scratch buffer
                        // we empty after every keystroke, so it can only refuse an insert, and
                        // Windows bells on a refused keystroke. Measured stream for a plain 'a':
                        //   KEYDOWN -> IME-COMMIT 'a' -> CHAR(0x08) -> CHAR('a') -> KEYUP
                        // with CHAR(0x08) the control erasing its own text and CHAR('a') the insert.
                        //
                        // The IME case rang too, once per commit, and only that: a committed char
                        // used to be the single insert still allowed through, because the control
                        // was assumed to be how the IME keeps composition state. It is not. The
                        // preedit lives in the IME; the control's text is only ever read for the
                        // commit string, which WM_IME_COMPOSITION already harvested. Suppressing
                        // the insert breaks nothing (0ca7d04 measured this) and it was the last
                        // path to a refused keystroke, so every message is now consumed.
                        clearAfter = CommitImeText(ch);
                        suppressOriginal = true;
                    }
                    break;
                }
                case WM_NCDESTROY:
                {
                    _imeEdit = IntPtr.Zero;
                    _imeEditPrevProc = IntPtr.Zero;
                    break;
                }
            }
        }
        catch (Exception ex) { if (_diagEnabled) LogStartup("IME proc error: " + ex.Message); }

        // Chain to the original proc, or DefWindowProc if the chain was torn down (e.g. the control
        // is being destroyed). Calling a null prev-proc would jump to address 0 and hard-crash.
        //
        // Every message is consumed. The EDIT is a scratch buffer that we clear after every
        // keystroke, so its proc can never usefully act on anything we send it, and Windows rings
        // the bell for a keystroke a control refuses. The measured message stream for a plain 'a'
        // was the whole story, and every one of these was still reaching the EDIT:
        //   KEYDOWN 'a' | CHAR(0x08) synthetic backspace | CHAR('a') insert | KEYUP 'a'
        // The key-down half and the command chars were consumed by an earlier fix, which is why
        // the beep became intermittent instead of constant; the key-up pair and the text insert
        // were the remainder.
        //
        // IME commits are the last case, and the reason the ring survived every earlier fix while
        // firing only on commit: a committed char is the one insert that still had to be ALLOWED
        // through, because the EDIT is how the IME keeps composition state and suppressing it
        // broke composition in 9eeda33/0ca7d04. So the ring that is left is the IME's own insert.
        //
        // Which means the insert is no longer needed to carry composition state: the preedit lives
        // in the IME, not in the control, and the only thing the control's text is ever used for
        // here is the commit string we already harvested. So an insert is pure noise, and this
        // consumes it. WM_IME_CHAR still passes through under its own id (0ca7d04) because the IME
        // state machine keys off the message id, not because of anything the control does with it.
        IntPtr result = IntPtr.Zero;
        if (!suppressOriginal && _imeEditPrevProc != IntPtr.Zero)
            result = CallWindowProcW(_imeEditPrevProc, hWnd, msg, wParam, lParam);
        else if (!suppressOriginal)
            result = DefWindowProcW(hWnd, msg, wParam, lParam);

        if (clearAfter) ImeEditClear();
        return result;
    }

    // Clear the EDIT without forwarding the change to nvim.
    private void ImeEditClear()
    {
        try
        {
            if (_imeEdit == IntPtr.Zero) return;
            SendMessageW(_imeEdit, EM_SETSEL, IntPtr.Zero, IntPtr.Zero);
            SendMessageW(_imeEdit, WM_SETTEXT, IntPtr.Zero, IntPtr.Zero); // lParam NULL == empty
        }
        catch { }
    }

    // The commit string for the in-flight composition, or "" when there is none.
    private string ImeCompositionResultString()
    {
        try
        {
            if (_imeEdit == IntPtr.Zero) return "";
            if (Ime32.GetContext is null || Ime32.GetCompositionStringW is null || Ime32.ReleaseContext is null)
                return "";   // no ime32 on this system; the WM_CHAR path still delivers the commit
            IntPtr himc = Ime32.GetContext(_imeEdit);
            // NULL context means the control has no IME at all. Returning "" here is safe: the
            // synthetic WM_CHAR the EDIT generates still carries the commit, and WM_CHAR is the
            // single delivery point — so the text is not lost.
            if (himc == IntPtr.Zero) return "";
            try
            {
                int len = Ime32.GetCompositionStringW(himc, GCS_RESULTSTR, null, 0);
                if (len <= 0) return "";
                var sb = new StringBuilder(len);
                Ime32.GetCompositionStringW(himc, GCS_RESULTSTR, sb, len);
                return sb.ToString();
            }
            finally { Ime32.ReleaseContext(himc); }
        }
        catch { return ""; }
    }

    // Send committed text to nvim as ONE nvim_input. Returns true when the caller must clear the
    // EDIT, which it does only after the original window proc has run (see the thunk).
    // One nvim_input, not one per character: nvim's autocmds/timeout must not see a half-inserted
    // word, and nvim_input handles the UTF-8 encoding of the whole string itself.
    private bool CommitImeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (_diagEnabled) LogStartup($"IME-COMMIT '{text}'");
        _imeLastForwarded = text;
        _imeComposing = false;
        ForwardToNvim(text);
        return true;
    }

    // Keep the IME candidate window on the nvim cursor.
    //
    // The IME anchors its candidate list to the caret of its target window, so the ONLY way to make
    // the list follow the cursor is to put the target where the cursor is. The EDIT is 1x1 px and
    // hidden behind the canvas, so moving it is invisible; the candidate list then appears at the
    // cursor instead of at 0,0. Public-pixel coordinates (SetWindowPos), not DIPs.
    //
    // Called after every cursor update and after any layout change, since _cellW/_cellH and the
    // grid origin both move independently of the cursor.
    private void ImeTrackCursor()
    {
        try
        {
            if (_imeEdit == IntPtr.Zero) return;
            if (_curLocalRow < 0) return;
            if (!IsWindow(_imeEdit)) return;

            int row = _curLocalRow, col = _curLocalCol;
            if (_multigridActive) MGridResolveCursor(_curGridId, _curLocalRow, _curLocalCol, out row, out col);
            if (row < 0) return;

            int x = (int)Math.Round(col * _cellW);
            int y = (int)Math.Round(row * _cellH);
            if (x == _imeTrackedX && y == _imeTrackedY) return; // avoid pointless SetWindowPos churn
            _imeTrackedX = x; _imeTrackedY = y;
            SetWindowPos(_imeEdit, IntPtr.Zero, x, y, 1, 1, SWP_NOZORDER | SWP_NOACTIVATE);
        }
        catch (Exception ex) { if (_diagEnabled) LogStartup("IME track failed: " + ex.Message); }
    }

    // Forward a non-composition key to nvim as nvim notation.
    //
    // The IME target owns focus, so this is the ONLY path keys can take — XAML's KeyDown (and the
    // mapping in OnKeyDown) is bypassed entirely. `printable` is false here: printable keys are
    // delivered as WM_CHAR instead, so honouring one on WM_KEYDOWN would duplicate the keystroke.
    private void ForwardNvimKey(IntPtr vk, IntPtr lParam, bool printable)
    {
        try
        {
            bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0; // VK_CONTROL
            bool alt = (GetAsyncKeyState(0x12) & 0x8000) != 0;  // VK_MENU
            if (printable) return;
            // Ctrl+digit / Ctrl+letter are nvim commands, not text, and never produce a useful
            // WM_CHAR, so they are mapped here from the virtual key.
            if (ctrl)
            {
                int v = (int)vk;
                if (v >= 0x41 && v <= 0x5A) { ForwardToNvim("<C-" + (char)(v - 0x41 + 'a') + ">"); return; }
                if (v >= 0x30 && v <= 0x39) { ForwardToNvim("<C-" + (char)(v - 0x30 + '0') + ">"); return; }
            }
            string? nvim = ImeKeyToNvim((int)vk, ctrl, alt);
            if (nvim == null) { if (_diagEnabled) LogStartup($"IME-KEY skip vk=0x{vk:X}"); return; }
            if (_diagEnabled) LogStartup($"IME-KEY '{nvim}' ctrl={ctrl} alt={alt}");
            ForwardToNvim(nvim);
        }
        catch (Exception ex) { if (_diagEnabled) LogStartup("IME key fwd failed: " + ex.Message); }
    }

    // VK -> nvim key notation, for keys nvim handles as commands. Mirrors the WinUI-side mapping in
    // MainWindow.MapKey/MapModifierKey, but on raw virtual keys from the Win32 message.
    private static string? ImeKeyToNvim(int vk, bool ctrl, bool alt)
    {
        string name = vk switch
        {
            0x08 => "<BS>", 0x09 => "<Tab>", 0x0D => "<CR>", 0x1B => "<Esc>",
            0x24 => "<Home>", 0x23 => "<End>", 0x21 => "<PageUp>", 0x22 => "<PageDown>",
            >= 0x70 and <= 0x87 => "<F" + (vk - 0x6F) + ">",           // F1-F24
            0x6B => "<Left>", 0x6C => "<Right>", 0x6D => "<Down>", 0x6E => "<Up>",
            0x25 => "<Left>", 0x26 => "<Up>", 0x28 => "<Down>",        // OEM keys, on US layouts
            // Anything printable (letters, digits, punctuation) is deliberately absent: those arrive
            // as WM_CHAR and are forwarded as text there. A printable key must be handled exactly
            // once, and Windows sends BOTH messages for a single physical keypress.
            _ => null
        };
        if (name == null) return null;
        string inner = name;
        if (alt) inner = "<A-" + inner + ">";
        if (ctrl) inner = "<C-" + inner + ">";
        return inner;
    }

    // Give the IME target keyboard focus. Called on activation and on every grid click — if focus
    // lands elsewhere the IME has no target and stops composing.
    private void ImeFocusTarget()
    {
        try
        {
            if (_imeEdit == IntPtr.Zero) return;
            if (!IsWindow(_imeEdit)) return;
            // A freshly created EDIT has NO input context: Windows leaves the default IME context
            // disabled on it, so composition silently does nothing and the app looks like the IME
            // is not working at all. Re-associating the DEFAULT context (the real OS IME) is what
            // actually enables IME composition for this control.
            //
            // This is best-effort and MUST NOT be able to break input: on a system without
            // ime32.dll the DllImport throws EntryPointNotFoundException/DllNotFoundException, and
            // losing focus handling entirely because of it would be far worse than no IME. So the
            // association is attempted inside its own try, and the diagnostic says which way it
            // went — a silently skipped call is exactly the bug being fixed here.
            if (!_imeContextTried)
            {
                _imeContextTried = true;
                if (Ime32.Available && Ime32.AssociateContext is not null)
                {
                    try
                    {
                        IntPtr himc = Ime32.AssociateContext(_imeEdit, IntPtr.Zero); // NULL = default IME
                        _imeContextAssociated = himc != IntPtr.Zero;
                        if (_diagEnabled) LogStartup("IME: ImmAssociateContext(default) -> 0x" +
                                                     himc.ToString("X") + (_imeContextAssociated ? " OK" : " NULL"));
                    }
                    catch (Exception ex)
                    {
                        if (_diagEnabled) LogStartup("IME: ImmAssociateContext failed: " + ex.GetType().Name);
                    }
                }
                else if (_diagEnabled)
                {
                    LogStartup("IME: ime32.dll unavailable — composing falls back to the synthetic WM_CHAR path");
                }
            }
            SetFocus(_imeEdit);
            // Log what actually holds focus: SetFocus can silently fail (e.g. the window is not the
            // foreground window), and then the IME has no target and composing does nothing.
            if (_diagEnabled)
            {
                IntPtr focus = GetFocus();
                LogStartup("IME: focus -> edit=0x" + _imeEdit.ToString("X") + " getfocus=0x" +
                           focus.ToString("X") + (focus == _imeEdit ? " OK" : " MISMATCH"));
            }
        }
        catch (Exception ex) { if (_diagEnabled) LogStartup("IME focus failed: " + ex.Message); }
    }
    private bool _imeContextTried;
    private bool _imeContextAssociated;

    // True while composing: OnKeyDown must swallow those keystrokes so the romaji that drove the
    // IME is not inserted into nvim as plain input alongside the committed result.
    private bool ImeIsComposing() => _imeComposing;

    private void ForwardToNvim(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            var client = _client;
            if (client == null) return;
            _ = client.CallAsync("nvim_input", text);
        }
        catch (Exception ex) { if (_diagEnabled) LogStartup("IME forward failed: " + ex.Message); }
    }

    private void ImeDetach()
    {
        try
        {
            if (_imeEdit != IntPtr.Zero)
            {
                DestroyWindow(_imeEdit);
                _imeEdit = IntPtr.Zero;
            }
            _imeOwner = IntPtr.Zero;
            _imeEditPrevProc = IntPtr.Zero;
            _imeEditProcKeepAlive = null;
        }
        catch { }
    }
}
