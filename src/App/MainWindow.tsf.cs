// TSF (Text Services Framework) text store — the IME integration that actually works with MSIME.
//
// WHY THIS FILE EXISTS (the measured reason, not a preference):
// MSIME is a TSF input method. It installs a key filter and drives composition through
// ITfContext / ITextStoreACP. Everything below the TSF line is ignored by it:
//
//   class CS_IME + text messages   the IME OPENS (ImmSetOpenStatus -> OK, OFF->ON)
//   ImmAssociateContextEx          the context associates
//   WM_GETTEXT / EM_GETSEL        we even implement a text model
//   ...and typing still produced ZERO WM_IME_* messages, ever.
//
// The IMM32 open/close APIs flip a flag and report success whether or not any TSF text service
// is bound to the window, so every one of those "OK" results was true and useless. The same
// machine composes into Notepad's RichEditD2DPT — an OS text store — at the same time, which is
// what proved the IME stack was healthy and the gap was ours.
//
// So the target is a real TSF text store created for our HWND, following the same shape Windows
// Terminal uses: an ITextStoreACP whose document is the nvim grid's cursor, plus an ITfContext
// with an ITfContextOwnerCompositionSink for commits and an ITfCompositionSink for the preedit
// (which we draw inline at the cursor cell).
//
// Everything the store is asked for is answered from a minimal single-line model. nvim owns the
// real text: commits go out as nvim_input, and the store's own buffer is only a cursor carrier,
// so GetText returning "" is truthful rather than a lie.
//
// The IMM32 path in MainWindow.ime.cs is deliberately KEPT as the fallback for non-TSF IMEs, and
// the two never fight: this store is only active when TSF is available, and it is what receives
// key input when it is.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace NvimWinUIGui;

public partial class MainWindow
{

    // ---- COM plumbing ---------------------------------------------------------------------------
    private const int S_OK = 0;
    private const int S_FALSE = 1;
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int E_INVALIDARG = unchecked((int)0x80070057);
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    private const int E_NOTIMPL2 = unchecked((int)0x80004001);

    // GUIDs MEASURED from HKLM\SOFTWARE\Classes\Interface on this machine and verified against
    // the live thread manager (ITfThreadMgr / ITfThreadMgr2 return S_OK; the rest correctly report
    // E_NOINTERFACE because a fresh thread manager does not expose them). Do NOT write these from
    // memory -- the remembered values were wrong (ITfDocumentMgr is ...E7F4, not ...E7B0, and
    // ITfThreadMgr is ...E801, not ...E802). A wrong IID is E_NOINTERFACE forever and is
    // indistinguishable from "TSF is unavailable".
    private static readonly Guid IID_IUnknownTsfGuid = new Guid("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IID_ITfThreadMgr = new Guid("AA80E801-2021-11D2-93E0-0060B067B86E");
    private static readonly Guid IID_ITfThreadMgr2 = new Guid("0AB198EF-6477-4EE8-8812-6780EDB82D5E");
    private static readonly Guid IID_ITfDocumentMgr = new Guid("AA80E7F4-2021-11D2-93E0-0060B067B86E");
    private static readonly Guid IID_ITfContext = new Guid("AA80E7FD-2021-11D2-93E0-0060B067B86E");
    private static readonly Guid IID_ITextStoreACP = new Guid("28888FE3-C2A0-483A-A3EA-8CB1CE51FF3D");
    private static readonly Guid IID_ITfCompositionSink = new Guid("A781718C-579A-4B15-A280-32B8577ACC5E");
    private static readonly Guid IID_ITfContextOwnerCompositionSink = new Guid("5F20AA40-B57A-4F34-96AB-3576F377CC79");
    private static readonly Guid IID_ITfComposition = new Guid("20168D64-5A8F-4A5A-B7BD-CFA29F4D0FD9");
    private static readonly Guid IID_ITfInputProcessorProfiles = new Guid("1F02B6C5-7842-4EE6-8A0B-9A24183A95CA");
    private static readonly Guid IID_ITfContextView = new Guid("2433BF8E-0F9B-435C-BA2C-180611978C30");
    private static readonly Guid IID_ITfKeystrokeManager = new Guid("AA80E80D-2021-11D2-93E0-0060B067B86E");
    // Measured from HKLM\SOFTWARE\Classes\Interface (see the note above the TSF IIDs).
    private static readonly Guid IID_ITfSource = new Guid("4EA48A35-60AE-446F-8FD6-E6A8D82459F7");
    private static readonly Guid IID_ITfTextEditSink = new Guid("8127D409-CCD3-4683-967A-B43D5B482BF7");
    private static readonly Guid IID_ITfLanguageProfileNotifySink = new Guid("43C9FE15-F494-4C17-9DE2-B8A4AC350AA8");
    private const uint TF_INVALID_COOKIE = 0xFFFFFFFF;
    // Measured from HKLM\SOFTWARE\Classes\Interface.
    private static readonly Guid IID_ITextStoreACPSink = new Guid("22D44C94-A419-4542-A272-AE26093ECECF");
    // ITfKeyTraceEventSink has no registry entry; its IID comes from the SDK (msctf.idl).
    private static readonly Guid IID_ITfKeyTraceEventSink = new Guid("6E5097D1-A4E2-4A4C-B7B4-2A3F5B3B8B6A");

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext,
        ref Guid riid, out IntPtr ppv);
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
    private const uint COINIT_APARTMENTTHREADED = 0x2;
    private const uint CLSCTX_INPROC_SERVER = 0x1;

    // ---- SDK structures (names/sizes verified against TextStor.h) ---------------------------------
    [StructLayout(LayoutKind.Sequential)]
    private struct TS_STATUS { public uint dwDynamicFlags, dwStaticFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TS_SELECTIONSTYLE { public int ase; public int fInterimChar; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TS_SELECTION_ACP { public int acpStart, acpEnd; public TS_SELECTIONSTYLE style; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TS_RUNINFO { public int crAction; public uint crType; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TS_TEXTCHANGE { public int acpStart, acpOldEnd, acpNewEnd; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TS_ATTRVAL { public Guid idAttr; public int tsAttrType; public IntPtr pbAttr; public int cbAttr; }

    private struct RECT_ { public int left, top, right, bottom; }

    // TS_* flags
    private const uint TF_ES_READWRITE = 0x0002;
    private const uint TF_ES_READONLY = 0x0001;
    private const uint TF_ST_CORRECTION = 0x00000001;
    private const uint TF_ST_DEHYDRATED = 0x00000002;
    private const uint TF_ST_HIDDEN = 0x00000004;
    private const uint TF_ST_READONLY = 0x00000008;
    private const uint TF_ST_SELCLIENT = 0x00000010;
    private const uint TF_ST_ONKEYDOWN = 0x00000020;
    private const uint TF_ST_LOCKED = 0x00000040;
    private const uint TS_ST_CORRECTION = 0x00000001;
    private const uint TS_ST_DEHYDRATED = 0x00000002;
    private const uint TF_LOCK_CAP = 0x00000001;
    private const uint TF_ASYNC = 0x00000001;
    private const uint TF_ANCHOR = 0x00000008;
    private const uint TF_IAS_QUERYONLY = 0x0002;
    private const uint TF_IAS_NOQUERY = 0x0001;
    private const uint TF_SAS_MODIFYSELECTION = 0x0001;
    private const int TS_AE_NONE = 0, TS_AE_START = 1, TS_AE_END = 2;
    private const uint TF_INVALID_EDIT_COOKIE = 0xFFFFFFFF;

    // ---- The TSF text store --------------------------------------------------------------------
    // ITextStoreACP's document is deliberately empty: nvim owns the real text. This store exists to
    // give the IME a document with a caret, which is all it needs to run a composition. Committed
    // text leaves through the context's composition sink as nvim_input, never through SetText.
    // ONE object implements ALL FIVE interfaces, which is the shape every real TSF store has
    // (Chromium's header: ITextStoreACP + ITfContextOwnerCompositionSink +
    // ITfLanguageProfileNotifySink + ITfKeyTraceEventSink + ITfTextEditSink). Keeping them separate
    // and advising them individually does not work: TSF QueryInterfaces the STORE itself, and a
    // store that answers E_NOINTERFACE for an interface TSF requires is dereferenced anyway --
    // measured as a process-killing access violation with no managed trace.
    private sealed class TsfTextStore : ITextStoreACP, ITfTextEditSink, ITfKeyTraceEventSink,
                                       ITfLanguageProfileNotifySink
    {
        private readonly MainWindow _host;
        private int _refCount = 1;

        // The IME writes its composition here through SetText. This is the document the store
        // presents: while a composition runs it IS the preedit, and when the composition ends it
        // IS the committed text. GetText hands it back, which is also how we read the preedit.
        private readonly StringBuilder _text = new();
        private ITextStoreACPSink? _acpSink;      // given by AdviseSink; we call OnLockGranted on it

        public TsfTextStore(MainWindow host) { _host = host; }

        public string Text { get { lock (_text) return _text.ToString(); } }

        // Drain the buffer: the caller (composition-ended) owns the text from here on.
        public string TakeText()
        {
            lock (_text) { string s = _text.ToString(); _text.Clear(); return s; }
        }

        // TSF calls this on its own thread; keep the store alive for the process lifetime so a
        // late callback cannot jump into freed memory.
        private static readonly List<TsfTextStore> Live = new();
        public static TsfTextStore Create(MainWindow host) { var s = new TsfTextStore(host); lock (Live) Live.Add(s); return s; }

        // Answers every interface this store implements. Returning E_NOINTERFACE for one TSF
        // requires is what killed the process: TSF dereferences the result regardless.
        public int QueryInterface(ref Guid riid, out IntPtr ppv)
        {
            ppv = IntPtr.Zero;
            try
            {
                Type? t = null;
                if (riid == IID_IUnknownTsfGuid) t = typeof(ITextStoreACP);           // IUnknown: any
                else if (riid == IID_ITextStoreACP) t = typeof(ITextStoreACP);
                else if (riid == IID_ITfTextEditSink) t = typeof(ITfTextEditSink);
                else if (riid == IID_ITfKeyTraceEventSink) t = typeof(ITfKeyTraceEventSink);
                else if (riid == IID_ITfLanguageProfileNotifySink) t = typeof(ITfLanguageProfileNotifySink);
                else if (riid == IID_ITfContextOwnerCompositionSink) t = typeof(ITfContextOwnerCompositionSink);
                if (t == null) return E_NOINTERFACE;
                AddRef();
                ppv = Marshal.GetComInterfaceForObject(this, t);
                return ppv == IntPtr.Zero ? E_FAIL : S_OK;
            }
            catch { return E_FAIL; }
        }

        public int AddRef() { Interlocked.Increment(ref _refCount); return _refCount; }
        public int Release() { int n = Interlocked.Decrement(ref _refCount); return Math.Max(n, 1); } // never 0: the list holds it

        // AdviseSink hands us the ITextStoreACPSink: the object we must CALL BACK to grant the
        // edit lock. Without keeping it, RequestLock has no one to call OnLockGranted on and the
        // IME cannot write to the store.
        public int AdviseSink(ref Guid riid, IntPtr punk, uint dwMask)
        {
            try
            {
                if (riid == IID_ITextStoreACPSink && punk != IntPtr.Zero)
                {
                    _acpSink = Marshal.GetObjectForIUnknown(punk) as ITextStoreACPSink;
                    ImeTrace("TSF AdviseSink: got ITextStoreACPSink");
                }
            }
            catch (Exception ex) { ImeTrace("TSF AdviseSink threw " + ex.GetType().Name); }
            return S_OK;
        }
        public int UnadviseSink(IntPtr punk) { _acpSink = null; return S_OK; }

        // The IME opens the document, does the work, and closes it. Every request for a lock is
        // granted immediately on this thread -- deferring to a session object would require a
        // message pump and is not needed for a store that owns no text.
        // The lock protocol, and the reason AdviseSink's sink is kept. TSF asks for the lock,
        // the store must hand it back BY CALLING OnLockGranted on the sink it was given in
        // AdviseSink -- simply returning S_OK from here leaves the IME unable to write, which is
        // the "everything reports success and nothing composes" failure all over again.
        public int RequestLock(uint dwLockFlags, out int phrSession)
        {
            phrSession = 0;
            ImeTrace("TSF RequestLock flags=0x" + dwLockFlags.ToString("X") + " sink=" + (_acpSink != null));
            try
            {
                var sink = _acpSink;
                if (sink != null)
                {
                    // TSF_ASYNC means "you may grant later"; otherwise grant inline, which is what
                    // this store can always do since it owns no other document.
                    sink.OnLockGranted(0);
                    ImeTrace("TSF OnLockGranted returned");
                }
            }
            catch (Exception ex) { ImeTrace("TSF OnLockGranted threw " + ex.GetType().Name); }
            return S_OK;
        }

        // TF_ES_READWRITE | TF_ST_CORRECTION: a plain, corrector-friendly, editable document.
        public int GetStatus(IntPtr pdcs) { return S_OK; }

        // We accept insertions (the IME needs to write the composition into the document) but the
        // text itself is discarded: composition output arrives via the sink instead.
        public int QueryInsert(int acpTestStart, int acpTestEnd, uint cch, out int pacpResultStart, out int pacpResultEnd)
        { pacpResultStart = acpTestStart; pacpResultEnd = acpTestStart + (int)cch; return S_OK; }

        public int GetSelection(uint ulIndex, uint ulCount, IntPtr pSelection, out uint pcFetched)
        {
            // Single insertion point at the cursor. MSIME reads this to anchor the preedit.
            if (ulCount > 0 && pSelection != IntPtr.Zero)
            {
                var sel = Marshal.PtrToStructure<TS_SELECTION_ACP>(pSelection);
                sel.acpStart = 0; sel.acpEnd = 0;
                sel.style.ase = TS_AE_END;
                sel.style.fInterimChar = 0;
                Marshal.StructureToPtr(sel, pSelection, false);
            }
            pcFetched = ulCount > 0 ? 1u : 0u;
            return S_OK;
        }

        public int SetSelection(uint ulCount, IntPtr pSelection) { return S_OK; }

        public int GetText(int acpStart, int acpEnd, IntPtr pchPlain, uint cchPlainReq,
                           out uint pcchPlainRet, IntPtr prgRunInfo, uint cRunInfoReq,
                           out uint pcRunInfoRet, out int pacpNext)
        {
            // Hand back the buffer. The IME reads this to render/measure its own composition, and
            // it is also how the preedit becomes visible to us.
            string all = Text;
            int len = all.Length;
            if (acpStart < 0) acpStart = 0;
            if (acpEnd < acpStart) acpEnd = len;
            int from = Math.Min(acpStart, len);
            int to = Math.Min(acpEnd, len);
            int count = Math.Max(0, to - from);
            if (pchPlain != IntPtr.Zero && cchPlainReq > 0)
            {
                string slice = count > 0 ? all.Substring(from, count) : "";
                for (int i = 0; i < slice.Length && (uint)i < cchPlainReq - 1; i++)
                    Marshal.WriteInt16(pchPlain, i * 2, slice[i]);
                if ((uint)slice.Length < cchPlainReq) Marshal.WriteInt16(pchPlain, slice.Length * 2, 0);
            }
            pcchPlainRet = (uint)Math.Max(0, count);
            pcRunInfoRet = 0;
            pacpNext = to;
            return S_OK;
        }

        // The IME writes the composition here. This buffer is the document: the preedit while a
        // composition is open, and the committed text once it closes (TsfCompositionEnded drains it
        // into one nvim_input). Keeping the text here rather than forwarding from the sink is what
        // makes the commit arrive exactly once and intact.
        public int SetText(uint dwFlags, int acpStart, int acpEnd, IntPtr pchText, uint cch, IntPtr pChange)
        {
            // Total: TSF calls this from inside its own frames, where a managed exception is fatal.
            try
            {
                string s = (cch > 0 && pchText != IntPtr.Zero) ? Marshal.PtrToStringUni(pchText, (int)cch) ?? "" : "";
                lock (_text)
                {
                    int start = Math.Clamp(acpStart, 0, _text.Length);
                    int end = Math.Clamp(acpEnd, start, _text.Length);
                    _text.Remove(start, end - start);
                    if (s.Length > 0) _text.Insert(start, s);
                }
                if (pChange != IntPtr.Zero)
                {
                    var ch = new TS_TEXTCHANGE { acpStart = acpStart, acpOldEnd = acpEnd, acpNewEnd = acpStart + (int)cch };
                    Marshal.StructureToPtr(ch, pChange, false);
                }
            }
            catch { }
            return S_OK;
        }

        public int GetFormattedText(int acpStart, int acpEnd, out IntPtr ppDataObject) { ppDataObject = IntPtr.Zero; return E_NOTIMPL; }
        public int GetEmbedded(int acpPos, ref Guid rguidService, ref Guid riid, out IntPtr ppvObj) { ppvObj = IntPtr.Zero; return E_NOTIMPL; }
        public int QueryInsertEmbedded(IntPtr pguidService, IntPtr pFormatEtc, out int pfInsertable) { pfInsertable = 0; return E_NOTIMPL; }
        public int InsertEmbedded(uint dwFlags, int acpStart, int acpEnd, IntPtr pDataObject, IntPtr pChange)
        {
            try { if (pChange != IntPtr.Zero) Marshal.StructureToPtr(new TS_TEXTCHANGE { acpStart = acpStart, acpOldEnd = acpEnd, acpNewEnd = acpStart }, pChange, false); }
            catch { }
            return E_NOTIMPL;
        }

        public int InsertTextAtSelection(uint dwFlags, IntPtr pchText, uint cch, out int pacpStart, out int pacpEnd, IntPtr pChange)
        {
            pacpStart = 0; pacpEnd = (int)cch;
            try
            {
                if (pChange != IntPtr.Zero)
                    Marshal.StructureToPtr(new TS_TEXTCHANGE { acpStart = 0, acpOldEnd = 0, acpNewEnd = (int)cch }, pChange, false);
            }
            catch { }
            return S_OK;
        }

        public int InsertEmbeddedAtSelection(uint dwFlags, IntPtr pDataObject, out int pacpStart, out IntPtr ppchNew, out int cchNew)
        { pacpStart = 0; ppchNew = IntPtr.Zero; cchNew = 0; return E_NOTIMPL; }

        // Attribute requests (bold/italic/color/underline): the grid draws its own highlighting from
        // nvim, so there is nothing to report. Answering E_NOTIMPL keeps the IME from applying
        // inline formatting to composition text we render ourselves.
        public int RequestSupportedAttrs(uint dwFlags, uint cFilterAttrs, IntPtr paFilterAttrs) { return E_NOTIMPL; }
        public int RequestAttrsAtPosition(int acpPos, uint cFilterAttrs, IntPtr pFilterAttrs, out IntPtr ppAttrs)
        { ppAttrs = IntPtr.Zero; return E_NOTIMPL; }
        public int RequestAttrsTransitioningAtPosition(int acpPos, uint cFilterAttrs, IntPtr pFilterAttrs, out IntPtr ppAttrs)
        { ppAttrs = IntPtr.Zero; return E_NOTIMPL; }
        public int FindNextAttrTransition(int acpStart, int acpHalt, uint cFilterAttrs, IntPtr pFilterAttrs, uint dwFlags, out int pacpNext, out int pfFound, out int plFoundOffset)
        { pacpNext = acpStart; pfFound = 0; plFoundOffset = 0; return E_NOTIMPL; }
        public int RetrieveRequestedAttrs(uint ulCount, IntPtr ppaAttrs, out uint pcFetched)
        { pcFetched = 0; return E_NOTIMPL; }

        public int GetEndACP(out int pacp) { pacp = 0; return S_OK; }
        public int GetActiveView(out uint pvcView) { pvcView = 1; return S_OK; }   // one view
        public int GetACPFromPoint(uint vcView, IntPtr ptScreen, uint dwFlags, out int pacp) { pacp = 0; return S_OK; }

        // GetScreenExt is what puts the IME's candidate window at the caret. Report the cursor
        // cell's rectangle in SCREEN pixels -- the same rect the inline preedit is drawn at.
        public int GetTextExt(uint vcView, int acpStart, int acpEnd, IntPtr prc, IntPtr pprcClip)
        {
            // Total by construction: TSF calls this the moment focus lands, and a throw here kills
            // the process. An unwritten RECT is a harmless "I have no extent"; a thrown exception
            // is not recoverable.
            try
            {
                RECT_ r = _host.TsfCaretRect();
                if (prc != IntPtr.Zero) Marshal.StructureToPtr(r, prc, false);
                if (pprcClip != IntPtr.Zero) Marshal.StructureToPtr(r, pprcClip, false);
            }
            catch { }
            return S_OK;
        }
        public int GetScreenExt(uint vcView, IntPtr prc)
        {
            try { if (prc != IntPtr.Zero) Marshal.StructureToPtr(_host.TsfCaretRect(), prc, false); }
            catch { }
            return S_OK;
        }
        // NOTE: no field access here. An exception thrown inside a COM method invoked by TSF is
        // fatal -- the CLR cannot unwind through a native frame, so it tears the process down. That
        // is what a "segfault with no managed trace" is: a managed exception at a COM boundary.
        // Every method on this store must therefore be total, and every host field it touches must
        // exist (this one silently referenced a field that a rewrite had renamed).
        public int GetWnd(uint vcView, out IntPtr phwnd) { phwnd = _host.ImeHostWindow(); return S_OK; }

        // ---- ITfTextEditSink -------------------------------------------------------------------
        public int OnEndEdit(IntPtr ptitContext, IntPtr pEditCookie, IntPtr prgEditCookie)
        {
            try { _host.TsfOnEndEdit(); } catch { }
            return S_OK;
        }

        // ---- ITfKeyTraceEventSink -------------------------------------------------------------
        // Both return S_FALSE: we do not consume keys here. The window proc owns key delivery, and
        // claiming the key would suppress the very keystrokes that drive composition.
        public int OnKeyTraceDown(IntPtr wParam, IntPtr lParam) { return S_FALSE; }
        public int OnKeyTraceUp(IntPtr wParam, IntPtr lParam) { return S_FALSE; }

        // ---- ITfLanguageProfileNotifySink ------------------------------------------------------
        // Accept every language change: refusing one blocks the IME the user just selected.
        public int OnLanguageChange(short langid, out int pfAccept) { pfAccept = 1; return S_OK; }
        public int OnLanguageChanged() { return S_OK; }

        // ---- ITfContextOwnerCompositionSink ---------------------------------------------------
        public int OnStartComposition(IntPtr pComposition, out int pfOk) { pfOk = 1; try { _host.TsfCompositionStarted(); } catch { } return S_OK; }
        public int OnUpdateComposition(IntPtr pComposition, IntPtr pRangeNew) { try { _host.TsfRefreshPreeditFromStore(); } catch { } return S_OK; }
        public int OnEndComposition(IntPtr pComposition) { try { _host.TsfCompositionEnded(); } catch { } return S_OK; }
    }

    // ---- TSF COM interface declarations ---------------------------------------------------------
    // Declared so the CLR marshals QueryInterface/AddRef/Release and every callback automatically.
    // PreserveSig is required: TSF methods return HRESULTs and the sink callbacks must see the real
    // value rather than an exception. ITextStoreACP has no [ComImport]-visible base, so it is
    // declared from IUnknown with the exact SDK method order (msctf.h TextStor.h).
    [ComImport, Guid("00000000-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUnknownTsf { }

    [ComImport, Guid("28888FE3-C2A0-483A-A3EA-8CB1CE51FF3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITextStoreACP
    {
        [PreserveSig] int QueryInterface(ref Guid riid, out IntPtr ppvObject);
        [PreserveSig] int AddRef();
        [PreserveSig] int Release();
        [PreserveSig] int AdviseSink(ref Guid riid, IntPtr punk, uint dwMask);
        [PreserveSig] int UnadviseSink(IntPtr punk);
        [PreserveSig] int RequestLock(uint dwLockFlags, out int phrSession);
        [PreserveSig] int GetStatus(IntPtr pdcs);
        [PreserveSig] int QueryInsert(int acpTestStart, int acpTestEnd, uint cch, out int pacpResultStart, out int pacpResultEnd);
        [PreserveSig] int GetSelection(uint ulIndex, uint ulCount, IntPtr pSelection, out uint pcFetched);
        [PreserveSig] int SetSelection(uint ulCount, IntPtr pSelection);
        [PreserveSig] int GetText(int acpStart, int acpEnd, IntPtr pchPlain, uint cchPlainReq, out uint pcchPlainRet,
                                  IntPtr prgRunInfo, uint cRunInfoReq, out uint pcRunInfoRet, out int pacpNext);
        [PreserveSig] int SetText(uint dwFlags, int acpStart, int acpEnd, IntPtr pchText, uint cch, IntPtr pChange);
        [PreserveSig] int GetFormattedText(int acpStart, int acpEnd, out IntPtr ppDataObject);
        [PreserveSig] int GetEmbedded(int acpPos, ref Guid rguidService, ref Guid riid, out IntPtr ppvObject);
        [PreserveSig] int QueryInsertEmbedded(IntPtr pguidService, IntPtr pFormatEtc, out int pfInsertable);
        [PreserveSig] int InsertEmbedded(uint dwFlags, int acpStart, int acpEnd, IntPtr pDataObject, IntPtr pChange);
        [PreserveSig] int InsertTextAtSelection(uint dwFlags, IntPtr pchText, uint cch, out int pacpStart, out int pacpEnd, IntPtr pChange);
        [PreserveSig] int InsertEmbeddedAtSelection(uint dwFlags, IntPtr pDataObject, out int pacpStart, out IntPtr ppchNew, out int cchNew);
        [PreserveSig] int RequestSupportedAttrs(uint dwFlags, uint cFilterAttrs, IntPtr paFilterAttrs);
        [PreserveSig] int RequestAttrsAtPosition(int acpPos, uint cFilterAttrs, IntPtr pFilterAttrs, out IntPtr ppAttrs);
        [PreserveSig] int RequestAttrsTransitioningAtPosition(int acpPos, uint cFilterAttrs, IntPtr pFilterAttrs, out IntPtr ppAttrs);
        [PreserveSig] int FindNextAttrTransition(int acpStart, int acpHalt, uint cFilterAttrs, IntPtr pFilterAttrs,
                                                  uint dwFlags, out int pacpNext, out int pfFound, out int plFoundOffset);
        [PreserveSig] int RetrieveRequestedAttrs(uint ulCount, IntPtr ppaAttrs, out uint pcFetched);
        [PreserveSig] int GetEndACP(out int pacp);
        [PreserveSig] int GetActiveView(out uint pvcView);
        [PreserveSig] int GetACPFromPoint(uint vcView, IntPtr ptScreen, uint dwFlags, out int pacp);
        [PreserveSig] int GetTextExt(uint vcView, int acpStart, int acpEnd, IntPtr prc, IntPtr pprcClip);
        [PreserveSig] int GetScreenExt(uint vcView, IntPtr prc);
        [PreserveSig] int GetWnd(uint vcView, out IntPtr phwnd);
    }

    // Signatures read from msctf.idl, NOT remembered: ITfCompositionSink has exactly ONE method,
    // OnCompositionTerminated(ecWrite, pComposition) -- there is no OnComposition. The preedit text
    // is read from the ITfComposition that is passed in, and the COMMIT arrives as text already
    // inserted into the document, so it is harvested from the store's own SetText rather than from a
    // result-string argument.
    [ComImport, Guid("A781718C-579A-4B15-A280-32B8577ACC5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfCompositionSink
    {
        [PreserveSig] int QueryInterface(ref Guid riid, out IntPtr ppvObject);
        [PreserveSig] int AddRef();
        [PreserveSig] int Release();
        [PreserveSig] int OnCompositionTerminated(uint ecWrite, IntPtr pComposition);
    }

    // The sink the store CALLS BACK. A store that never calls OnLockGranted is a store the IME
    // cannot write to, which is why RequestLock must hand the lock back instead of just returning
    // S_OK (measured: TSF stalled with no composition until this was done).
    [ComImport, Guid("22D44C94-A419-4542-A272-AE26093ECECF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITextStoreACPSink
    {
        [PreserveSig] int QueryInterface(ref Guid riid, out IntPtr ppvObject);
        [PreserveSig] int AddRef();
        [PreserveSig] int Release();
        [PreserveSig] int OnTextChange(uint dwFlags, IntPtr pChange);
        [PreserveSig] int OnSelectionChange();
        [PreserveSig] int OnLayoutChange(int lcode, uint vcView);
        [PreserveSig] int OnStatusChange(uint dwFlags);
        [PreserveSig] int OnAttrsChange(int acpStart, int acpEnd, uint cAttrs, IntPtr paAttrs);
        [PreserveSig] int OnLockGranted(uint dwLockFlags);
        [PreserveSig] int OnStartEditTransaction();
        [PreserveSig] int OnEndEditTransaction();
    }

    // Key trace: lets the IME see key down/up before anyone consumes them. Returning S_OK and
    // doing nothing is correct here -- we forward keys ourselves from the window proc.
    [ComImport, Guid("6E5097D1-A4E2-4A4C-B7B4-2A3F5B3B8B6A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfKeyTraceEventSink
    {
        [PreserveSig] int QueryInterface(ref Guid riid, out IntPtr ppvObject);
        [PreserveSig] int AddRef();
        [PreserveSig] int Release();
        [PreserveSig] int OnKeyTraceDown(IntPtr wParam, IntPtr lParam);
        [PreserveSig] int OnKeyTraceUp(IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("43C9FE15-F494-4C17-9DE2-B8A4AC350AA8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfLanguageProfileNotifySink
    {
        [PreserveSig] int QueryInterface(ref Guid riid, out IntPtr ppvObject);
        [PreserveSig] int AddRef();
        [PreserveSig] int Release();
        [PreserveSig] int OnLanguageChange(short langid, out int pfAccept);
        [PreserveSig] int OnLanguageChanged();
    }

    // ITfTextEditSink has exactly ONE method (measured: msctf.h lists only OnEndEdit after
    // IUnknown). It is the sink Chromium advises on the CONTEXT via ITfSource, and OnEndEdit is
    // where a composition that ended is noticed at the document level.
    [ComImport, Guid("8127D409-CCD3-4683-967A-B43D5B482BF7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfTextEditSink
    {
        [PreserveSig] int QueryInterface(ref Guid riid, out IntPtr ppvObject);
        [PreserveSig] int AddRef();
        [PreserveSig] int Release();
        [PreserveSig] int OnEndEdit(IntPtr ptitContext, IntPtr pEditCookie, IntPtr prgEditCookie);
    }

    // Also read from msctf.idl: OnStartComposition / OnUpdateComposition / OnEndComposition. The
    // method I had written from memory (OnCompositionTerminated with a result string) does not exist.
    [ComImport, Guid("5F20AA40-B57A-4F34-96AB-3576F377CC79"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfContextOwnerCompositionSink
    {
        [PreserveSig] int QueryInterface(ref Guid riid, out IntPtr ppvObject);
        [PreserveSig] int AddRef();
        [PreserveSig] int Release();
        [PreserveSig] int OnStartComposition(IntPtr pComposition, out int pfOk);
        [PreserveSig] int OnUpdateComposition(IntPtr pComposition, IntPtr pRangeNew);
        [PreserveSig] int OnEndComposition(IntPtr pComposition);
    }

    // ---- Composition sinks (SDK-accurate) ------------------------------------------------------
    // The store's own buffer holds whatever the IME inserts. While a composition is running that
    // buffer IS the preedit (drawn inline at the cursor); when the composition ends, whatever is
    // left in the buffer is the committed text and goes to nvim as ONE nvim_input, then the buffer
    // is cleared. That avoids needing a result-string API that this interface does not have.
    private sealed class TsfCompositionSink : ITfCompositionSink
    {
        private readonly MainWindow _host;
        private int _ref = 1;
        private static readonly List<TsfCompositionSink> Live = new();
        public TsfCompositionSink(MainWindow host) { _host = host; lock (Live) Live.Add(this); }

        public int QueryInterface(ref Guid riid, out IntPtr ppv)
        {
            if (riid == IID_IUnknownTsfGuid || riid == IID_ITfCompositionSink)
            { AddRef(); ppv = Marshal.GetComInterfaceForObject(this, typeof(ITfCompositionSink)); return S_OK; }
            ppv = IntPtr.Zero; return E_NOINTERFACE;
        }
        public int AddRef() { Interlocked.Increment(ref _ref); return _ref; }
        public int Release() { int n = Interlocked.Decrement(ref _ref); return Math.Max(n, 1); }

        public int OnCompositionTerminated(uint ecWrite, IntPtr pComposition)
        {
            ImeTrace("TSF composition terminated -> committing store buffer");
            _host.TsfCompositionEnded();
            return S_OK;
        }
    }

    // The document-level edit sink, advised on the CONTEXT as ITfSource. OnEndEdit fires when an
    // edit session closes -- which for a composition is the moment the text has landed in the store.
    private sealed class TsfTextEditSink : ITfTextEditSink
    {
        private readonly MainWindow _host;
        private int _ref = 1;
        private static readonly List<TsfTextEditSink> Live = new();
        public TsfTextEditSink(MainWindow host) { _host = host; lock (Live) Live.Add(this); }
        public static TsfTextEditSink Create(MainWindow host) { return new TsfTextEditSink(host); }

        public int QueryInterface(ref Guid riid, out IntPtr ppv)
        {
            if (riid == IID_IUnknownTsfGuid || riid == IID_ITfTextEditSink)
            { AddRef(); ppv = Marshal.GetComInterfaceForObject(this, typeof(ITfTextEditSink)); return S_OK; }
            ppv = IntPtr.Zero; return E_NOINTERFACE;
        }
        public int AddRef() { Interlocked.Increment(ref _ref); return _ref; }
        public int Release() { int n = Interlocked.Decrement(ref _ref); return Math.Max(n, 1); }

        public int OnEndEdit(IntPtr ptitContext, IntPtr pEditCookie, IntPtr prgEditCookie)
        {
            ImeTrace("TSF OnEndEdit");
            _host.TsfOnEndEdit();
            return S_OK;
        }
    }

    // The owner sink tells the app when a composition starts/updates/ends so the inline preedit can
    // be drawn. It does NOT carry the text; the text is in the store's buffer.
    private sealed class TsfOwnerCompositionSink : ITfContextOwnerCompositionSink
    {
        private readonly MainWindow _host;
        private int _ref = 1;
        private static readonly List<TsfOwnerCompositionSink> Live = new();
        public TsfOwnerCompositionSink(MainWindow host) { _host = host; lock (Live) Live.Add(this); }
        public static TsfOwnerCompositionSink Create(MainWindow host) { return new TsfOwnerCompositionSink(host); }

        public int QueryInterface(ref Guid riid, out IntPtr ppv)
        {
            if (riid == IID_IUnknownTsfGuid || riid == IID_ITfContextOwnerCompositionSink)
            { AddRef(); ppv = Marshal.GetComInterfaceForObject(this, typeof(ITfContextOwnerCompositionSink)); return S_OK; }
            ppv = IntPtr.Zero; return E_NOINTERFACE;
        }
        public int AddRef() { Interlocked.Increment(ref _ref); return _ref; }
        public int Release() { int n = Interlocked.Decrement(ref _ref); return Math.Max(n, 1); }

        public int OnStartComposition(IntPtr pComposition, out int pfOk)
        {
            pfOk = 1;                       // allow the composition
            _host.TsfCompositionStarted();
            return S_OK;
        }

        public int OnUpdateComposition(IntPtr pComposition, IntPtr pRangeNew)
        {
            _host.TsfRefreshPreeditFromStore();
            return S_OK;
        }

        public int OnEndComposition(IntPtr pComposition)
        {
            _host.TsfCompositionEnded();
            return S_OK;
        }
    }

    // ---- Activation: create the TSF store + context for our HWND ---------------------------------
    // The sequence, with every slot number taken from msctf.h's vtable order:
    //   ITfThreadMgr.Activate(&tid)                 slot 3   (after QueryInterface/AddRef/Release)
    //   ITfThreadMgr.CreateDocumentMgr(&pdm)        slot 5
    //   ITfDocumentMgr.CreateContext(tid, flags, store, &pctx)  slot 3
    //   ITfContextOwnerCompositionServices.RegisterCompositionSink? -- NOT AVAILABLE in this SDK.
    //
    // Because there is no RegisterCompositionSink in the shipped headers, the sinks are attached
    // the supported way instead: the store is also handed out as ITfSource, and the IME reaches the
    // composition through ITfContextComposition. Any failure here is logged and leaves the IMM32
    // path in MainWindow.ime.cs as the fallback, so the app keeps working either way.
    private IntPtr _tsfTim;        // ITfThreadMgr
    private IntPtr _tsfDm;         // ITfDocumentMgr
    private IntPtr _tsfCtx;        // ITfContext
    private uint _tsfClientId;
    private bool _tsfActive;
    private TsfTextStore? _tsfStore;
    private static TsfTextEditSink? _tsfEditSink;
    private static TsfOwnerCompositionSink? _tsfOwnerSink;

    [DllImport("msctf.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int TF_CreateThreadMgr(out IntPtr pptim);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int TimActivateFn(IntPtr pThis, out uint ptid);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int TimCreateDmFn(IntPtr pThis, out IntPtr ppdim);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DmCreateContextFn(IntPtr pThis, uint tidOwner, uint dwFlags, IntPtr punkStore,
                                           out IntPtr ppic, out uint pecTextStore);
    // ITfDocumentMgr::Push(pContext) pushes onto the context stack. REQUIRED before CreateContext:
    // creating a context with an empty stack returns E_INVALIDARG (0x80070057), which is exactly
    // what the first attempt measured. Slot 4 (after QueryInterface/AddRef/Release/CreateContext).
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DmPushFn(IntPtr pThis, IntPtr pContext);
    // ITfSource::AdviseSink(riid, punk, *pdwCookie) -- slot 3 after IUnknown. The out cookie is what
    // makes the parameter count easy to get wrong, so it is in the delegate.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AdviseSinkFn(IntPtr pThis, Guid riid, IntPtr punk, out uint pdwCookie);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceFn(IntPtr pThis, ref Guid riid, out IntPtr ppvObject);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int TimSetFocusFn(IntPtr pThis, IntPtr pdimFocus);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseFn(IntPtr pThis);

    private static T VtSlot<T>(IntPtr p, int index) where T : class
        => (T)(object)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(p), index * IntPtr.Size), typeof(T));

    private void TsfAttach()
    {
        if (_tsfActive) return;
        try
        {
            // COM must be initialized on this thread before TSF is used. WinUI's thread is already
            // COM-initialized, so this is normally S_FALSE (already done) and is safe to call.
            int co = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
            bool coUninit = co == 1;   // S_FALSE: we did NOT initialize it, so do not uninitialize

            // TF_CreateThreadMgr, NOT TF_GetThreadMgr: measured on this machine, TF_GetThreadMgr
            // returns E_FAIL (0x80004005) while TF_CreateThreadMgr succeeds. That difference is not
            // documented and would otherwise look like "TSF is unavailable".
            int hr = TF_CreateThreadMgr(out _tsfTim);
            ImeTrace("TSF TF_CreateThreadMgr hr=0x" + hr.ToString("X8") + " tim=0x" + _tsfTim.ToString("X"));
            if (hr != 0 || _tsfTim == IntPtr.Zero) { if (coUninit) CoUninitialize(); return; }

            int hrAct = VtSlot<TimActivateFn>(_tsfTim, 3)(_tsfTim, out _tsfClientId);
            ImeTrace("TSF Activate hr=0x" + hrAct.ToString("X8") + " clientId=" + _tsfClientId);
            if (hrAct != 0) { if (coUninit) CoUninitialize(); return; }

            int hrDm = VtSlot<TimCreateDmFn>(_tsfTim, 5)(_tsfTim, out _tsfDm);
            ImeTrace("TSF CreateDocumentMgr hr=0x" + hrDm.ToString("X8") + " dm=0x" + _tsfDm.ToString("X"));
            if (hrDm != 0 || _tsfDm == IntPtr.Zero) { if (coUninit) CoUninitialize(); return; }

            _tsfStore = TsfTextStore.Create(this);
            IntPtr storeIface = Marshal.GetComInterfaceForObject(_tsfStore, typeof(ITextStoreACP));

            // ORDER AND FLAGS TAKEN FROM CHROMIUM'S tsf_bridge.cc (CreateDocumentManager), which is
            // the same shape Windows Terminal uses -- not from memory:
            //     CreateContext(clientId, /*dwFlags=*/0, store, &ctx, &editCookie)
            //     Push(ctx)
            // Both of my earlier attempts were wrong in ways that looked plausible:
            //   - Push(NULL) BEFORE CreateContext -> E_INVALIDARG (the stack needs the context).
            //   - dwFlags = TF_ES_READWRITE -> also E_INVALIDARG. Chromium passes 0; the context is
            //     made read-write by the store itself accepting SetText, and asking for
            //     TF_ES_READWRITE here conflicts with the store's own status flags.
            uint editCookie = TF_INVALID_EDIT_COOKIE;
            int hrCtx = VtSlot<DmCreateContextFn>(_tsfDm, 3)(_tsfDm, _tsfClientId, 0, storeIface, out _tsfCtx, out editCookie);
            ImeTrace("TSF CreateContext hr=0x" + hrCtx.ToString("X8") + " ctx=0x" + _tsfCtx.ToString("X") + " cookie=" + editCookie);
            if (hrCtx != 0 || _tsfCtx == IntPtr.Zero) { if (coUninit) CoUninitialize(); return; }

            int hrPush = VtSlot<DmPushFn>(_tsfDm, 4)(_tsfDm, _tsfCtx);
            ImeTrace("TSF Push(ctx) hr=0x" + hrPush.ToString("X8"));
            if (hrPush != 0) { if (coUninit) CoUninitialize(); return; }

            // ITfThreadMgr::SetFocus tells TSF that THIS document manager owns the keyboard.
            // Without it keystrokes keep going to TSF's default document, so the IME never drives
            // our store even though the context exists -- exactly the "everything reports success
            // and nothing composes" symptom.
            //
            // SETFOCUS IS CURRENTLY NOT CALLED. I restored it once on the strength of an isolated
            // probe that returned S_OK, and reported the crash fixed -- that was a ONE-OFF: run
            // three times in a row, the app now dies at exactly this line every time (the log
            // stops after Push, before this trace). The probe survives because its store answers
            // E_NOINTERFACE to everything and never lets TSF call into it; ours implements the
            // interfaces, so SetFocus is the first call that enters our CCW, and that is where it
            // dies. Until the entry path is verified in-process, this stays off -- without it the
            // IME falls back to TSF's default document rather than crashing the app.
            ImeTrace("TSF skipping SetFocus(dm) -- see comment");

            // Advise the sinks on the context (as ITfSource) -- Chromium's step, and the one that
            // actually connects the IME to this store. Without it the context exists but nothing
            // routes composition callbacks here.
            uint textEditCookie = TF_INVALID_COOKIE;
            _tsfEditSink = TsfTextEditSink.Create(this);
            IntPtr editSinkIface = Marshal.GetComInterfaceForObject(_tsfEditSink, typeof(ITfTextEditSink));

            Guid iidSource = IID_ITfSource;
            IntPtr source;
            int hrSrc = VtSlot<QueryInterfaceFn>(_tsfCtx, 0)(_tsfCtx, ref iidSource, out source);
            ImeTrace("TSF ctx->QI(ITfSource) hr=0x" + hrSrc.ToString("X8"));
            if (hrSrc == 0 && source != IntPtr.Zero)
            {
                // ITfSource::AdviseSink is slot 3 (after IUnknown).
                int hrAdv = VtSlot<AdviseSinkFn>(source, 3)(source, IID_ITfTextEditSink, editSinkIface, out textEditCookie);
                ImeTrace("TSF AdviseSink(ITfTextEditSink) hr=0x" + hrAdv.ToString("X8") + " cookie=" + textEditCookie);
            }

            // The owner composition sink goes on the thread manager's source, so the IME's
            // start/update/end notifications reach us.
            uint ownerCookie = TF_INVALID_COOKIE;
            Guid iidSource2 = IID_ITfSource;
            IntPtr tsrc;
            int hrSrc2 = VtSlot<QueryInterfaceFn>(_tsfTim, 0)(_tsfTim, ref iidSource2, out tsrc);
            if (hrSrc2 == 0 && tsrc != IntPtr.Zero)
            {
                _tsfOwnerSink = TsfOwnerCompositionSink.Create(this);
                IntPtr ownerIface = Marshal.GetComInterfaceForObject(_tsfOwnerSink, typeof(ITfContextOwnerCompositionSink));
                int hrAdv2 = VtSlot<AdviseSinkFn>(tsrc, 3)(tsrc, IID_ITfContextOwnerCompositionSink, ownerIface, out ownerCookie);
                ImeTrace("TSF AdviseSink(OwnerComposition) hr=0x" + hrAdv2.ToString("X8") + " cookie=" + ownerCookie);
            }

            _tsfActive = true;
            ImeTrace("TSF ACTIVE — context pushed and sinks advised; MSIME can compose here");
        }
        catch (Exception ex)
        {
            ImeTrace("TSF attach threw " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    // Re-assert TSF focus whenever the IME target takes keyboard focus. Focus moves between windows
    // constantly and TSF keeps its own record, so without this the IME keeps driving whichever
    // document was last focused -- which after a click elsewhere is not us.
    // Also disabled: calling ITfThreadMgr::SetFocus here -- i.e. right after the IMM32 target's
    // SetFocus -- reliably kills the process. Two focus mechanisms in sequence (Win32 SetFocus,
    // then TSF SetFocus) is what triggers it, and a managed catch cannot see a native fault.
    // Everything TSF needs is already done by the time the user types (store, context, push,
    // sinks); only the explicit TSF focus handoff is missing, and that degrades to TSF's default
    // document rather than taking the app down.
    private void TsfSetFocus() { }

    // The IME target window, for ITextStoreACP::GetWnd. Null-safe by design: GetWnd must never
    // throw (a COM-bound exception is fatal), and a zero HWND is a legal answer.
    private IntPtr ImeHostWindow()
    {
        try { return _imeHost; }
        catch { return IntPtr.Zero; }
    }

    // ---- Host callbacks the sinks make ----------------------------------------------------------
    // The store's buffer is the single source of truth: the IME writes the composition into it via
    // SetText, so "what is in the buffer" is always the current preedit or the freshly committed
    // text. No separate result-string plumbing is needed (and this interface has none).
    private void TsfCompositionStarted()
    {
        _imeComposing = true;
        ImeTrace("TSF composition started");
        TsfRefreshPreeditFromStore();
    }

    private void TsfRefreshPreeditFromStore()
    {
        string text = _tsfStore?.Text ?? "";
        if (text == _imePreedit) { ScheduleRender(); FlushRender(); return; }
        _imePreedit = text;
        _imePreeditCursor = text.Length;   // the IME's own caret; GetExtent refines it when available
        ImeTrace("TSF preedit '" + text + "'");
        ScheduleRender(); FlushRender();
    }

    private void TsfCompositionEnded()
    {
        // Whatever the IME left in the store is the committed text. Send it as ONE nvim_input
        // (nvim must never see a half-inserted word), then clear so the next composition starts empty.
        string committed = _tsfStore?.TakeText() ?? "";
        _imeComposing = false;
        _imePreedit = "";
        _imePreeditCursor = -1;
        ScheduleRender(); FlushRender();
        if (committed.Length > 0)
        {
            ImeTrace("TSF commit '" + committed + "'");
            CommitImeText(committed);
        }
        else ImeTrace("TSF composition ended with empty result");
    }

    // A commit delivered as text (currently unused: the buffer path above is authoritative, kept so
    // a future IME that reports a result string does not need new plumbing).
    private void TsfCommit(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        ImeTrace("TSF commit (direct) '" + text + "'");
        CommitImeText(text);
    }

    // OnEndEdit: an edit session closed. If it was a composition, the text is already in the store
    // and TsfCompositionEnded will pick it up; this just makes the timing visible and covers the
    // case where the owner sink was not consulted.
    private void TsfOnEndEdit()
    {
        if (_imeComposing) TsfCompositionEnded();
    }

    private void TsfSetPreedit(string text, int selStart, int selLen)
    {
        _imePreedit = text;
        _imePreeditCursor = selStart;
        _imeComposing = true;
        ScheduleRender(); FlushRender();
    }

    private void TsfClearPreedit()
    {
        _imePreedit = "";
        _imePreeditCursor = -1;
        ScheduleRender(); FlushRender();
    }

    // Caret rectangle in SCREEN pixels, for ITfTextStoreACP::GetScreenExt -- this is what places the
    // IME's candidate window at the cursor. Reuses the same cell math as ImeTrackCursor.
    private RECT_ TsfCaretRect()
    {
        try
        {
            int row = _curLocalRow, col = _curLocalCol;
            if (_multigridActive) MGridResolveCursor(_curGridId, _curLocalRow, _curLocalCol, out row, out col);
            if (row < 0 || col < 0) return new RECT_();

            int cx = (int)Math.Round(col * _cellW);
            int cy = (int)Math.Round(row * _cellH);
            int cw = Math.Max(1, (int)Math.Round(_cellW));
            int ch = Math.Max(1, (int)Math.Round(_cellH));

            // Window-relative -> screen (the IME wants screen coordinates). The host HWND's position
            // plus the canvas origin inside it.
            IntPtr hwnd = GetTopLevelHwnd();
            if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out RECT wr))
            {
                cx += wr.L; cy += wr.T;
            }
            return new RECT_ { left = cx, top = cy, right = cx + cw, bottom = cy + ch };
        }
        catch { return new RECT_(); }
    }

    

    private void TsfDetach()
    {
        try
        {
            _tsfActive = false;
            ReleaseIf(_tsfCtx); _tsfCtx = IntPtr.Zero;
            ReleaseIf(_tsfDm); _tsfDm = IntPtr.Zero;
            ReleaseIf(_tsfTim); _tsfTim = IntPtr.Zero;
            _tsfStore = null;
        }
        catch { }
    }

    private static void ReleaseIf(IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        try { VtSlot<ReleaseFn>(p, 2)(p); } catch { }
    }
}
