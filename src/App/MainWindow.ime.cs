// IME (input method editor) support — custom window-class target.
//
// The grid is painted on a Win2D CanvasControl — a DirectComposition surface with no notion of
// text input, so the OS IME has nothing to compose into. The composition target is a bare
// custom-class child window ("NvimImeHost") of the top-level HWND, registered and owned by
// this file: its proc is 100% ours and its only fallback is DefWindowProc.
//
// HISTORY: this used to be a hidden 1x1 Win32 EDIT control. It worked, but every design
// decision there mitigated EDIT's own default behavior: the bell rang for every keystroke the
// always-empty control refused (WM_KEYDOWN, WM_KEYUP, the char message AND the insert each
// had to be consumed individually), the single-line control refused the IME's commit Enter
// (ES_MULTILINE), and the subclass proc had to chain WM_IME_CHAR to the ORIGINAL proc under
// its own id because that proc synthesized the WM_CHAR carrying committed text. A custom
// class has no default behavior to fight, so the whole suppressOriginal/clearAfter machinery
// is gone. What remains are IME state-machine facts, not control facts, and they carry over
// unchanged:
//
//   - ONE delivery point. Windows sends both WM_KEYDOWN and WM_CHAR for one physical press.
//     Text AND the character-shaped commands Enter/Tab (0x0D, 0x09) are forwarded from WM_CHAR
//     only; other command keys (<BS>, <Esc>, arrows, F-keys, Ctrl-combos) are decided once on
//     WM_KEYDOWN and their char message is ignored.
//   - While composing, keys belong to the IME — EXCEPT Esc, which is how a composition is
//     abandoned. Esc is forwarded to nvim AND the IME's composition is torn down (release the
//     input context, then re-take it immediately — the user's next keystroke arrives before
//     any window activation would restore it).
//   - Committed text is delivered from WM_IME_COMPOSITION's GCS_RESULTSTR, NOT from the WM_CHAR
//     that DefWindowProc derives from WM_IME_CHAR. Measured on the live window: that derived char
//     carries only the HIGH BYTE of the committed code point: measured on the live window, posted
//     U+3042 / U+65E5 / U+3044 arrived as 0x30 / 0x65 / 0x30 (i.e. code point >> 8), never the
//     character. So it is not a usable text source. WM_IME_CHAR itself is only an
//     acknowledgment and must chain to DefWindowProc UNDER ITS OWN ID — the IME state machine keys
//     off the message id, and a commit the IME believes was never acknowledged stalls it for the
//     rest of the session. The derived echo char is then consumed (_imeCommitEcho) so a commit
//     lands exactly once.
//   - A window created via CreateWindowExW needs its default input context associated before
//     the IME will compose into it (ImmAssociateContext(hwnd, NULL) = the default system IME). The
//     association must be re-doable (a cancel releases it) and must never be gated behind a
//     one-shot startup flag.
//
// Preedit is harvested from WM_IME_COMPOSITION (GCS_COMPSTR + GCS_CURSORPOS) and drawn INLINE
// in the grid at the cursor (DrawImePreedit, called from RenderCore) — the point of moving off
// the EDIT: composition is rendered by us, in our font, at the exact cursor cell.
//
// The host window is 1x1 px and parked at the nvim cursor cell (ImeTrackCursor), so the IME
// anchors its candidate list at the cursor and the list follows the cursor for free. It is a
// child of the *top-level HWND*, not of the XAML island: a child of a real Win32 window takes
// real keyboard focus, whereas anything inside the XAML island is driven by WinUI's own input
// stack and never becomes the IME's target.
//
// Because the host owns keyboard focus, the XAML KeyDown path never fires while it is
// attached, so the proc also forwards ordinary nvim keys (ForwardNvimKey).

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace NvimWinUIGui;

public partial class MainWindow
{
    private const string ImeHostClass = "NvimImeHost";
    private static bool _imeClassRegistered;    // process-wide: re-registering fails harmlessly anyway

    private IntPtr _imeHost;                    // HWND of the custom-class IME target
    private ImeHostProc? _imeHostProcKeepAlive; // class proc delegate; the class holds the thunk
    private bool _imeAttachTried;               // never retry: a failed attach would leak HWNDs
    private bool _imeComposing;                 // IME is mid-composition
    private string _imePreedit = "";            // current composition string (GCS_COMPSTR)
    private int _imePreeditCursor = -1;         // cursor inside the preedit (GCS_CURSORPOS), -1 unknown
    // Set while an IME commit is in flight. DefWindowProc synthesizes a WM_CHAR echoing the
    // commit, so the NEXT WM_CHAR after any WM_IME_CHAR is that echo and must be consumed
    // rather than forwarded — otherwise the commit is sent twice.
    private bool _imeCommitEcho;
    private int _imeTrackedX = -1, _imeTrackedY = -1; // last SetWindowPos, to skip redundant moves

    // Win32 messages / notifications / styles.
    private const int WM_CHAR = 0x0102;
    private const int VK_ESCAPE = 0x1B;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_NCDESTROY = 0x0082;
    private const int WM_IME_STARTCOMPOSITION = 0x0283;
    private const int WM_IME_COMPOSITION = 0x0284;
    private const int WM_IME_CHAR = 0x0286;
    private const int WM_IME_ENDCOMPOSITION = 0x028E;
    private const int GCS_RESULTSTR = 0x0001;
    private const int GCS_COMPSTR = 0x0008;
    private const int GCS_CURSORPOS = 0x0080;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_TABSTOP = 0x00010000;
    private const uint SWP_NOZORDER = 0x0004;   // keep the host behind the canvas, not reordered
    private const int GWLP_WNDPROC = -4;
    private const uint SWP_NOACTIVATE = 0x0010; // moving it must not steal/steal-back focus

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
        public IntPtr hIconSm;
    }

    private delegate IntPtr ImeHostProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);
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
    // Used by the activation trace: when the app is being deactivated, the new foreground window's
    // process tells us whether the cause was something in OUR process (a floating window taking
    // activation) or a genuinely different application. Only the former may be fought back.
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    // --- IME input context (OPTIONAL, loaded dynamically) ---------------------------------
    // imm32.dll carries the composition-string readers we need for the inline preedit. It is
    // present on every Windows install, but the old code loaded "ime32.dll" (a different,
    // optional DLL that is ABSENT on some installs — verified on this box) and a static
    // [DllImport] of an absent DLL throws DllNotFoundException on FIRST USE, silently skipping
    // every statement after it in the same try. So both DLLs are loaded LAZILY and probed once.
    // When imm32 is somehow missing, the app says so and keeps working: committed text still
    // arrives via the WM_CHAR that DefWindowProc generates for WM_IME_CHAR — only the preedit
    // read (GCS_COMPSTR/GCS_RESULTSTR) degrades.
    private static class Ime32
    {
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
            // imm32.dll first: it is the one that always exists, and the composition reads below
            // live in it. Each DLL load in its own try — a failure must cost that one library,
            // not the rest of the probe.
            try
            {
                IntPtr h = System.Runtime.InteropServices.NativeLibrary.Load("imm32.dll");
                _module = h;   // held for the process lifetime: the delegates point INTO it
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
                Available = false;   // no imm32; the WM_CHAR commit path still works
            }
        }

        // Intentionally never freed: the delegates above are only valid while the module is loaded.
        private static IntPtr _module;
    }

    // Create the custom-class IME host window. Called once the window is realized, the first
    // point a top-level HWND exists. Runs at most once per process: retrying after a failure
    // would leak a window handle on every attempt.
    private void ImeAttach()
    {
        if (_imeAttachTried) return;
        _imeAttachTried = true;
        try
        {
            IntPtr owner = GetTopLevelHwnd();
            if (owner == IntPtr.Zero) { if (_diagEnabled) LogStartup("IME: no top-level HWND yet"); return; }

            // Register the class once per process. A bare class: no background brush (nothing
            // is ever painted into it), no cursor, no menu.
            if (!_imeClassRegistered)
            {
                _imeHostProcKeepAlive = ImeHostProcThunk;   // the class holds the thunk; the delegate must outlive it
                var wc = new WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_imeHostProcKeepAlive),
                    hInstance = GetModuleHandleW(null),
                    lpszClassName = ImeHostClass,
                };
                ushort atom = RegisterClassExW(ref wc);
                if (atom == 0)
                {
                    // ERROR_CLASS_ALREADY_EXISTS (1410) is fine — a previous attempt registered it.
                    if (Marshal.GetLastWin32Error() != 1410)
                    {
                        if (_diagEnabled) LogStartup("IME: RegisterClassExW failed err=" + Marshal.GetLastWin32Error());
                        return;
                    }
                }
                _imeClassRegistered = true;
            }

            // 1x1 px at the origin, invisible behind the canvas. ImeTrackCursor parks it at the
            // nvim cursor cell so the candidate list anchors there.
            _imeHost = CreateWindowExW(0, ImeHostClass, "", WS_CHILD | WS_VISIBLE | WS_TABSTOP,
                0, 0, 1, 1, owner, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
            if (_imeHost == IntPtr.Zero)
            {
                if (_diagEnabled) LogStartup("IME: CreateWindowExW(host) failed err=" + Marshal.GetLastWin32Error());
                return;
            }

            if (_diagEnabled) LogStartup("IME: custom-class target attached hwnd=0x" + _imeHost.ToString("X") +
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

        // ---- Window activation trace (diagnostic) ------------------------------------------------
        // A plain Win32 subclass of the top-level HWND. WPF's HwndSource is not available in a
        // WinUI 3 project, and this app has no XAML markup, so subclassing the HWND directly is
        // the only way to observe activation. NOTE: this subclasses the TOP-LEVEL window only
        // for WM_ACTIVATEAPP observation — WinUI crashes on IME/key messages routed through a
        // subclassed top-level proc (STATUS_STOWED_EXCEPTION), which is why the IME target is a
        // separate custom-class child instead of top-level interception. The activation trace
        // chains everything straight through and touches no input messages.
        //
        // Why it exists: only the Activated event was logged before, so a floating window that took the
        // app's activation left NO trace — the only record was the re-activation after a user click.
        // That made "input stopped working until I clicked the window" impossible to diagnose from the
        // log. WM_ACTIVATEAPP is the signal that covers it: it fires for the whole process, and its
        // wParam is the HWND gaining activation, 0 when this app is losing it.
        private delegate IntPtr ActivationProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        private ActivationProc? _activationProcKeepAlive;   // the HWND only holds the thunk pointer
        private IntPtr _activationPrevProc;
        private bool _activationTraced;

        private void ImeAttachActivationTrace()
        {
            if (_activationTraced) return;
            try
            {
                IntPtr h = GetTopLevelHwnd();
                if (h == IntPtr.Zero) { if (_diagEnabled) LogStartup("ACT: no top-level HWND yet"); return; }
                _activationProcKeepAlive = ActivationThunk;
                _activationPrevProc = SetWindowLongPtrW(h, GWLP_WNDPROC,
                    Marshal.GetFunctionPointerForDelegate(_activationProcKeepAlive));
                _activationTraced = _activationPrevProc != IntPtr.Zero;
                if (_diagEnabled)
                    LogStartup("ACT: activation trace attached" + (_activationTraced ? "" : " FAILED"));
            }
            catch (Exception ex) { if (_diagEnabled) LogStartup("ACT: attach failed: " + ex.Message); }
        }

        private IntPtr ActivationThunk(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == 0x001C /* WM_ACTIVATEAPP */)
            {
                // wParam == 0 means THIS app stopped being the foreground app.
                string what = wParam == IntPtr.Zero
                    ? "WINDOW DEACTIVATED (app lost foreground)"
                    : "WINDOW activated (foreground -> 0x" + wParam.ToString("X") + ")";
                if (_diagEnabled) LogStartup("ACT: " + what);
                // If the app is being deactivated by something in our own process (a floating window
                // taking activation), the IME target loses the foreground input queue with it. Take the
                // focus back so typing continues without a click. Doing it on the DEACTIVATION path is
                // deliberate: by the time Activated fires, the app is already not foreground and
                // SetFocus on the target would be a no-op.
                if (wParam == IntPtr.Zero && _imeHost != IntPtr.Zero)
                {
                    try
                    {
                        IntPtr fg = GetForegroundWindow();
                        // Only reclaim if the new foreground belongs to THIS process (a float in our own
                        // window); never fight another application for the foreground.
                        uint fgPid;
                        GetWindowThreadProcessId(fg, out fgPid);
                        if (fgPid == GetCurrentProcessId()) ImeFocusTarget();
                    }
                    catch { }
                }
            }
            return _activationPrevProc != IntPtr.Zero
                ? CallWindowProcW(_activationPrevProc, hWnd, msg, wParam, lParam)
                : DefWindowProcW(hWnd, msg, wParam, lParam);
        }

    // ---- The IME host window proc --------------------------------------------------------------
    // This IS the window proc of our own class — there is no original control proc, and
    // DefWindowProc provides the system's default IME handling (which is what turns a committed
    // WM_IME_CHAR into the WM_CHAR that delivers text). Everything else is ours to decide.
    //
    // Delivery map (one physical press can generate several messages; each nvim key must be
    // forwarded EXACTLY once — see the header):
    //
    //   WM_KEYDOWN  Esc            -> forward <Esc> + tear down any composition; handled (0)
    //               VK_RETURN/TAB  -> NOT mapped here: WM_CHAR carries the same command, and
    //                                 forwarding from both branches was the doubled-Enter bug
    //               other commands -> ForwardNvimKey once; handled (0)
    //               (composing)    -> to DefWindowProc: those keys belong to the IME
    //   WM_CHAR     commit echo    -> consumed; the commit already went out from GCS_RESULTSTR
    //               CR / Tab       -> forward when not composing; handled (0)
    //               Esc char       -> already forwarded from WM_KEYDOWN; handled (0)
    //               other <0x20    -> already forwarded from WM_KEYDOWN; handled (0)
    //               printable      -> THE single delivery point for ORDINARY typing; handled (0)
    //   WM_IME_CHAR                -> chain to DefWindowProc under its own id; forward nothing
    //   WM_IME_STARTCOMPOSITION    -> composing = true; chain (default IME setup)
    //   WM_IME_COMPOSITION         -> preedit -> inline render; GCS_RESULTSTR -> DELIVERED here
    //   WM_IME_ENDCOMPOSITION      -> composing = false, preedit cleared; chain
    //
    // WHY GCS_RESULTSTR rather than the WM_CHAR that DefWindowProc derives from WM_IME_CHAR:
    // measured against the live window, that synthesized char carries only the HIGH BYTE of the
    // committed code point (posted U+3042 / U+65E5 / U+3044 arrived as 0x30 / 0x65 / 0x30 =
    // cp >> 8, never the character). The old EDIT design made that synthesized WM_CHAR the single
    // delivery point, so this exact defect is why a commit could never be trusted to arrive
    // intact. GCS_RESULTSTR is the documented source and returns the whole string at once;
    // _imeCommitEcho then consumes the derived WM_CHAR so the commit still lands exactly once.
    //
    // No message is ever "consumed to avoid a bell" — that was an EDIT-class artifact. Our class
    // has no defaults to refuse anything; returning 0 simply means "handled".
    private IntPtr ImeHostProcThunk(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_IME_STARTCOMPOSITION:
                {
                    _imeComposing = true;
                    if (_diagEnabled) LogStartup("IME: composition started");
                    break; // chain below
                }
                case WM_IME_COMPOSITION:
                {
                    _imeComposing = true;
                    // Harvest the preedit for the INLINE render (DrawImePreedit). This is the
                    // feature the EDIT target could not do: composition shown in our own font at
                    // the exact cursor cell.
                    if ((lParam & (GCS_COMPSTR | GCS_CURSORPOS)) != 0)
                    {
                        _imePreedit = ImeCompositionString(GCS_COMPSTR);
                        int cp = ImeCompositionCursorPos();
                        _imePreeditCursor = cp;
                        if (_diagEnabled) LogStartup($"IME-PREEDIT '{_imePreedit}' cursor={cp} lParam=0x{lParam:X}");
                        ScheduleRender(); FlushRender();
                    }
                    // The committed string, straight from the IME. This is the delivery point for
                    // commits, NOT the WM_CHAR that DefWindowProc synthesizes for WM_IME_CHAR:
                    // measured against the live window, that synthesized char carries only the
                    // HIGH BYTE of the committed code point (posted U+3042 / U+65E5 / U+3044
                    // arrived as '0' / 'e' / '0'), so it is not a usable text source.
                    // GCS_RESULTSTR is the documented one and returns the WHOLE string at once.
                    // It only works now because this path loads imm32.dll — the old EDIT code
                    // loaded the optional ime32.dll, absent on this box, which is exactly why the
                    // harvest was dead then and every commit went through the broken char path.
                    if ((lParam & GCS_RESULTSTR) != 0)
                    {
                        string committed = ImeCompositionString(GCS_RESULTSTR);
                        if (committed.Length > 0)
                        {
                            CommitImeText(committed);
                            _imeCommitEcho = true;   // the following WM_CHAR is this commit's echo
                        }
                    }
                    break; // chain below: DefWindowProc's default result handling must still run
                }
                case WM_IME_ENDCOMPOSITION:
                {
                    _imeComposing = false;
                    _imePreedit = "";
                    _imePreeditCursor = -1;
                    ScheduleRender(); FlushRender();
                    if (_diagEnabled) LogStartup("IME: composition ended");
                    break; // chain below
                }
                case WM_IME_CHAR:
                {
                    // Acknowledgment only. The committed TEXT comes from GCS_RESULTSTR above; the
                    // char DefWindowProc derives from this message carries only the code point's
                    // high byte (measured) and is therefore never forwarded. Chaining
                    // unchanged under its own id is required: the IME state machine keys off the
                    // message id, and re-labeling or dropping it stalls composition permanently.
                    // The flag makes the WM_CHAR DefWindowProc derives from this message a
                    // recognized echo to consume, so a commit is never delivered twice.
                    _imeCommitEcho = true;
                    break;
                }
                case WM_KEYDOWN:
                {
                    int vki = (int)wParam;
                    // Esc is carved out of the "composing owns every key" rule: it is how a
                    // composition is ABANDONED. Swallowing it wedges the app in composing forever
                    // (measured with the old target). Forward to nvim, then tear the IME's own
                    // composition down — telling nvim is only half of leaving a composition.
                    if (vki == VK_ESCAPE)
                    {
                        bool wasComposing = _imeComposing;
                        _imeComposing = false;
                        ForwardToNvim("<Esc>");
                        if (wasComposing) ImeCancelComposition();
                        return IntPtr.Zero; // handled
                    }
                    if (_imeComposing)
                    {
                        // Keys are shaping the preedit. Chain so the system's default IME key
                        // handling still sees them, but forward nothing to nvim.
                        break;
                    }
                    // Enter/Tab: WM_CHAR is their single delivery point (see map). Handled (0):
                    // nothing to do here, and our class has no default that needs the key.
                    if (vki == 0x0D || vki == 0x09) return IntPtr.Zero;
                    ForwardNvimKey(wParam, lParam);
                    return IntPtr.Zero; // handled
                }
                case WM_CHAR:
                {
                    // Echo of an IME commit we already delivered from GCS_RESULTSTR. Consuming it
                    // is what keeps "one delivery point" true now that the commit comes from the
                    // composition result rather than from this message. A WM_CHAR with no commit
                    // pending is ordinary typing and is forwarded below.
                    if (_imeCommitEcho)
                    {
                        _imeCommitEcho = false;
                        if (_diagEnabled) LogStartup("IME: consumed commit echo char 0x" + wParam.ToString("X"));
                        return IntPtr.Zero;
                    }
                    if (wParam == 0x1B) return IntPtr.Zero;   // Esc char: forwarded from WM_KEYDOWN
                    if (wParam == 0x0D || wParam == 0x0A)     // <CR>
                    {
                        if (!_imeComposing) ForwardToNvim("<CR>");
                        return IntPtr.Zero;
                    }
                    if (wParam == 0x09)                       // <Tab>
                    {
                        if (!_imeComposing) ForwardToNvim("<Tab>");
                        return IntPtr.Zero;
                    }
                    // Other control chars (^H/BS, ^G, ^C, DEL...) were forwarded from WM_KEYDOWN.
                    if (wParam < 0x20 || wParam == 0x7F) return IntPtr.Zero;
                    if (wParam >= 1 && wParam <= 0xFFFF && ((int)wParam & 0xF800) != 0xD800)
                    {
                        // THE single text delivery point: plain keystrokes and IME commits share
                        // it. A surrogate pair arrives as two WM_CHARs in order; appending
                        // reproduces it (CommitImeText forwards per message, nvim reassembles).
                        CommitImeText(((char)wParam).ToString());
                    }
                    return IntPtr.Zero;
                }
                case WM_NCDESTROY:
                {
                    _imeHost = IntPtr.Zero;
                    break; // chain below (defwindowproc finishes teardown)
                }
            }
        }
        catch (Exception ex) { if (_diagEnabled) LogStartup("IME proc error: " + ex.Message); }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // The composition string for the requested field (GCS_COMPSTR / GCS_RESULTSTR), or "".
    private string ImeCompositionString(int gcs)
    {
        try
        {
            if (_imeHost == IntPtr.Zero) return "";
            if (Ime32.GetContext is null || Ime32.GetCompositionStringW is null || Ime32.ReleaseContext is null)
                return "";   // no imm32; the WM_CHAR path still delivers commits
            IntPtr himc = Ime32.GetContext(_imeHost);
            if (himc == IntPtr.Zero) return "";
            try
            {
                int len = Ime32.GetCompositionStringW(himc, gcs, null, 0);
                if (len <= 0) return "";
                var sb = new StringBuilder(len / 2 + 1);
                // len is in BYTES for the W variant.
                int got = Ime32.GetCompositionStringW(himc, gcs, sb, len);
                return got > 0 ? sb.ToString(0, Math.Min(got / 2, sb.Length)) : "";
            }
            finally { Ime32.ReleaseContext(himc); }
        }
        catch { return ""; }
    }

    // Cursor position inside the composition (character index), or -1 when unavailable.
    // GCS_CURSORPOS returns the position in LOWORD (0x8000 bit = no display attribute info).
    private int ImeCompositionCursorPos()
    {
        try
        {
            if (_imeHost == IntPtr.Zero) return -1;
            if (Ime32.GetContext is null || Ime32.GetCompositionStringW is null || Ime32.ReleaseContext is null)
                return -1;
            IntPtr himc = Ime32.GetContext(_imeHost);
            if (himc == IntPtr.Zero) return -1;
            try
            {
                int v = Ime32.GetCompositionStringW(himc, GCS_CURSORPOS, null, 0);
                if (v < 0) return -1;
                return v & 0x7FFF;
            }
            finally { Ime32.ReleaseContext(himc); }
        }
        catch { return -1; }
    }

    // Send committed text to nvim as ONE nvim_input per message. One call per WM_CHAR (which
    // is one UTF-16 unit): nvim reassembles surrogate pairs arriving in order, and batching
    // would require a timer this path does not have.
    private void CommitImeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (_diagEnabled) LogStartup($"IME-COMMIT '{text}'");
        _imeComposing = false;
        ForwardToNvim(text);
    }

    // Abandon an in-flight composition. Forwarding <Esc> to nvim is only half of leaving a
    // composition: the IME also holds an open preedit, and if that stays open it swallows the
    // next keystrokes and never presents a candidate window again — the reported "Esc severs
    // the IME". The only IME-owned state is the input context, and releasing + immediately
    // re-taking it is what makes the next composition start fresh. Re-taking must happen NOW,
    // not on the next window activation: the user's next keystroke arrives first, and a
    // released context is not restored by the IME on its own.
    //
    // NOTE: do NOT use ImmNotifyIME from inside the window proc. It posts the composition
    // string INTO the window, synchronously re-entering this very proc — measured with the old
    // EDIT target: after one such cancel the control stopped accepting compositions and the
    // IME could not be switched on at all. Nothing inside a window proc may drive IME state
    // into its own window.
    private void ImeCancelComposition()
    {
        try
        {
            if (_imeHost == IntPtr.Zero || !IsWindow(_imeHost)) return;
            _imePreedit = "";
            _imePreeditCursor = -1;
            ScheduleRender(); FlushRender();
            if (Ime32.GetContext is not null && Ime32.ReleaseContext is not null)
            {
                try
                {
                    IntPtr himc = Ime32.GetContext(_imeHost);
                    if (himc != IntPtr.Zero)
                    {
                        Ime32.ReleaseContext(himc);
                        // ImmReleaseContext only drops the app's reference. The window's own
                        // association is re-taken below; track state so ImeFocusTarget can
                        // re-take it as a second line of defence.
                        _imeContextAssociated = false;
                        _imeContextWanted = true;
                    }
                }
                catch { }
            }
            if (_diagEnabled) LogStartup("IME: composition cancelled");
            ImeAssociateContext();
        }
        catch (Exception ex) { if (_diagEnabled) LogStartup("IME cancel failed: " + ex.Message); }
    }

    // Associate the DEFAULT system input context with the host window. Must be re-doable (a
    // cancel releases it) and never gated behind a one-shot flag. Best-effort: a box without a
    // working imm32 still gets committed text through the WM_CHAR path.
    private void ImeAssociateContext()
    {
        if (!Ime32.Available || Ime32.AssociateContext is null)
        {
            if (_diagEnabled && !Ime32.Available)
                LogStartup("IME: imm32.dll unavailable — no inline preedit; commits still arrive via WM_CHAR");
            return;
        }
        try
        {
            // NULL second arg = the real system IME context (same call the EDIT target used,
            // verified against the live control: a freshly created window has NO input context
            // and the IME does nothing until one is associated). Non-null return = associated.
            IntPtr himc = Ime32.AssociateContext(_imeHost, IntPtr.Zero);
            bool ok = himc != IntPtr.Zero;
            _imeContextAssociated = ok;
            _imeContextTried = true;
            _imeContextWanted = !ok;
            if (_diagEnabled) LogStartup("IME: ImmAssociateContext(default) -> 0x" + himc.ToString("X") +
                                          (ok ? " OK" : " NULL/FAILED"));
        }
        catch (Exception ex)
        {
            _imeContextAssociated = false;
            if (_diagEnabled) LogStartup("IME: ImmAssociateContext failed: " + ex.GetType().Name);
        }
    }
    private bool _imeContextTried;       // an association has been attempted at least once
    private bool _imeContextAssociated;  // the window currently HOLDS an input context
    // True when a context is wanted but not currently held, so ImeFocusTarget re-takes it.
    private bool _imeContextWanted = true;

    // Keep the IME candidate window on the nvim cursor.
    //
    // The IME anchors its candidate list to its target window, so the ONLY way to make the list
    // follow the cursor is to put the target where the cursor is. The host is 1x1 px and hidden
    // behind the canvas, so moving it is invisible. Public-pixel coordinates (SetWindowPos),
    // not DIPs.
    //
    // Called after every cursor update and after any layout change, since _cellW/_cellH and the
    // grid origin both move independently of the cursor.
    private void ImeTrackCursor()
    {
        try
        {
            if (_imeHost == IntPtr.Zero) return;
            if (_curLocalRow < 0) return;
            if (!IsWindow(_imeHost)) return;

            int row = _curLocalRow, col = _curLocalCol;
            if (_multigridActive) MGridResolveCursor(_curGridId, _curLocalRow, _curLocalCol, out row, out col);
            if (row < 0) return;

            int x = (int)Math.Round(col * _cellW);
            int y = (int)Math.Round(row * _cellH);
            if (x == _imeTrackedX && y == _imeTrackedY) return; // avoid pointless SetWindowPos churn
            _imeTrackedX = x; _imeTrackedY = y;
            SetWindowPos(_imeHost, IntPtr.Zero, x, y, 1, 1, SWP_NOZORDER | SWP_NOACTIVATE);
        }
        catch (Exception ex) { if (_diagEnabled) LogStartup("IME track failed: " + ex.Message); }
    }

    // Forward a non-composition key to nvim as nvim notation.
    //
    // The IME host owns focus, so this is the ONLY path keys can take — XAML's KeyDown (and the
    // mapping in OnKeyDown) is bypassed entirely. Only called from WM_KEYDOWN with printable=false
    // semantics: printable keys are delivered as WM_CHAR instead, so honouring one on WM_KEYDOWN
    // would duplicate the keystroke.
    private void ForwardNvimKey(IntPtr vk, IntPtr lParam)
    {
        try
        {
            bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0; // VK_CONTROL
            bool alt = (GetAsyncKeyState(0x12) & 0x8000) != 0;  // VK_MENU
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
            // No log here: ForwardToNvim traces every nvim_input from the single exit point, and
            // logging at both would make a doubled key look like two separate events.
            ForwardToNvim(nvim);
        }
        catch (Exception ex) { if (_diagEnabled) LogStartup("IME key fwd failed: " + ex.Message); }
    }

    // VK -> nvim key notation, for keys nvim handles as commands. Mirrors the WinUI-side mapping in
    // MainWindow.MapKey/MapModifierKey, but on raw virtual keys from the Win32 message.
    // VK_RETURN/VK_TAB are deliberately ABSENT: WM_CHAR is their single delivery point (see proc).
    private static string? ImeKeyToNvim(int vk, bool ctrl, bool alt)
    {
        string? name = vk switch
        {
            0x08 => "<BS>", 0x1B => "<Esc>",
            0x24 => "<Home>", 0x23 => "<End>", 0x21 => "<PageUp>", 0x22 => "<PageDown>",
            0x2D => "<Insert>", 0x2E => "<Del>",
            >= 0x70 and <= 0x87 => "<F" + (vk - 0x6F) + ">",           // F1-F24
            0x25 => "<Left>", 0x27 => "<Right>", 0x28 => "<Down>", 0x26 => "<Up>",
            _ => null
        };
        if (name == null) return null;
        string inner = name;
        if (alt) inner = "<A-" + inner + ">";
        if (ctrl) inner = "<C-" + inner + ">";
        return inner;
    }

    // Give the IME host keyboard focus. Called on activation and on every grid click — if focus
    // lands elsewhere the IME has no target and stops composing.
    private void ImeFocusTarget()
    {
        try
        {
            if (_imeHost == IntPtr.Zero) return;
            if (!IsWindow(_imeHost)) return;
            // A freshly created window has NO input context: Windows leaves the default IME
            // disabled on it, so composition silently does nothing and the app looks like the
            // IME is not working at all. Associate the default context before focusing.
            // Re-doable, never one-shot: a cancel releases the context, and a released context
            // is not restored by the IME on its own.
            bool needContext = !_imeContextTried;
            if (!needContext && _imeContextWanted && !_imeContextAssociated)
            {
                // A cancel released it; take it back before the next keystroke arrives.
                needContext = true;
                if (_diagEnabled) LogStartup("IME: re-associating input context after a cancel");
            }
            if (needContext) ImeAssociateContext();
            SetFocus(_imeHost);
            // Log what actually holds focus: SetFocus can silently fail (e.g. the window is not the
            // foreground window), and then the IME has no target and composing does nothing.
            if (_diagEnabled)
            {
                IntPtr focus = GetFocus();
                LogStartup("IME: focus -> host=0x" + _imeHost.ToString("X") + " getfocus=0x" +
                           focus.ToString("X") + (focus == _imeHost ? " OK" : " MISMATCH"));
            }
        }
        catch (Exception ex) { if (_diagEnabled) LogStartup("IME focus failed: " + ex.Message); }
    }

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
            // Every nvim_input leaves through here, so this is the only place that can prove a key
            // reached nvim exactly once. It has to be here and not at the call sites: WM_CHAR's own
            // <CR>/<Tab> branches call ForwardToNvim directly, so call-site logging missed them
            // entirely and a key forwarded twice from those two branches was invisible — which is
            // exactly the bug the Enter double-newline was. Gated on NVIM_WINUI_DIAG like the rest.
            if (_diagEnabled) LogStartup("INPUT " + text);
            _ = client.CallAsync("nvim_input", text);
        }
        catch (Exception ex) { if (_diagEnabled) LogStartup("IME forward failed: " + ex.Message); }
    }

    private void ImeDetach()
    {
        try
        {
            if (_imeHost != IntPtr.Zero)
            {
                DestroyWindow(_imeHost);
                _imeHost = IntPtr.Zero;
            }
            _imeHostProcKeepAlive = null;
        }
        catch { }
    }
}
