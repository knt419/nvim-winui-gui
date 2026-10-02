// WT-shaped TSF composition host (port of microsoft/terminal src/tsf/Implementation.cpp).
//
// KEY DIFFERENCE FROM THE OLD ATTEMPT: Windows Terminal does NOT implement ITextStoreACP.
// It creates a *transitory* TSF context (GetStatus -> TS_SS_TRANSITORY | TS_SS_NOHIDDENTEXT,
// which turns on CUAS = the IMM32 emulation layer), implements ITfContextOwner /
// ITfContextOwnerCompositionSink / ITfTextEditSink, reads the composition text out of the
// context itself, draws it, and sends only the finalized text onward. No text store, no
// ITextStoreACPSink, no lock protocol -- none of the machinery that killed the old path.
//
// Vtable slot numbers below come from the SDK header (msctf.h, 10.0.26100.0), extracted
// mechanically. IUnknown occupies slots 0..2, so the first listed method of each interface
// is slot 3.
//
//   ITfThreadMgrEx  {3E90ADE3-...}  ActivateEx=14  Deactivate=4  CreateDocumentMgr=5
//                                   SetFocus=8  AssociateFocus=9
//   ITfDocumentMgr  {AA80E7F4-...}  CreateContext=3  Push=4  Pop=5
//   ITfContext      {AA80E7FD-...}  RequestEditSession=3  GetStart=7  GetEnd=8  TrackProperties=14
//   ITfRange        {AA80E7FF-...}  GetText=3  SetText=4  ShiftEnd=9
//   ITfReadOnlyProperty {17D49A3D-...}  EnumRanges=4
//   IEnumTfRanges   {F99D3F40-...}  Next=4
//   ITfSource       {4EA48A35-...}  AdviseSink=3
//
// Interfaces we IMPLEMENT are declared WITHOUT the IUnknown methods: the CLR builds the
// IUnknown slots of a CCW itself, and redeclaring them shifts every method by three, so
// TSF's calls land on the wrong slots and the process dies on the first inbound call.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace NvimWinUIGui;

[StructLayout(LayoutKind.Sequential)]
public struct TsfRect { public int Left, Top, Right, Bottom; }

public static class TsfConst
{
    // ActivateEx flags (msctf.h)
    public const uint TF_TMAE_NOACTIVATETIP = 0x00000001;
    public const uint TF_TMAE_NOACTIVATEKEYBOARDLAYOUT = 0x00000020;
    // Status flags (TextStor.h)
    public const uint TS_SS_TRANSITORY = 0x4;
    public const uint TS_SS_NOHIDDENTEXT = 0x8;
    // Edit-session flags (msctf.h)
    public const uint TF_ES_READWRITE = 0x6;
    public const uint TF_ES_ASYNC = 0x8;
    public const uint TF_INVALID_COOKIE = 0xFFFFFFFF;
    // TF_DA_* / misc
    public const int S_OK = 0, S_FALSE = 1;
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_POINTER = unchecked((int)0x80004003);
    public const int LONG_MAX = 0x7FFFFFFF;
}

// ---------------------------------------------------------------------------------------------
// Interfaces we implement (CCW side). No IUnknown methods -- see the note at the top.
// ---------------------------------------------------------------------------------------------
[ComImport, Guid("AA80E80C-2021-11D2-93E0-0060B067B86E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ITfContextOwner
{
    [PreserveSig] int GetACPFromPoint(IntPtr ptScreen, uint dwFlags, out int pacp);
    [PreserveSig] int GetTextExt(int acpStart, int acpEnd, IntPtr prc, IntPtr pfClipped);
    [PreserveSig] int GetScreenExt(IntPtr prc);
    [PreserveSig] int GetStatus(IntPtr pdcs);
    [PreserveSig] int GetWnd(out IntPtr phwnd);
    [PreserveSig] int GetAttribute(ref Guid rguidAttribute, IntPtr pvarValue);
}

[ComImport, Guid("5F20AA40-B57A-4F34-96AB-3576F377CC79"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ITfContextOwnerCompositionSink
{
    [PreserveSig] int OnStartComposition(IntPtr pComposition, out int pfOk);
    [PreserveSig] int OnUpdateComposition(IntPtr pComposition, IntPtr pRangeNew);
    [PreserveSig] int OnEndComposition(IntPtr pComposition);
}

[ComImport, Guid("8127D409-CCD3-4683-967A-B43D5B482BF7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ITfTextEditSink
{
    [PreserveSig] int OnEndEdit(IntPtr pic, uint ecReadOnly, IntPtr pEditRecord);
}

[ComImport, Guid("AA80E803-2021-11D2-93E0-0060B067B86E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ITfEditSession
{
    [PreserveSig] int DoEditSession(uint ec);
}

// TSFSTATUS structure (TextStor.h TS_STATUS).
[StructLayout(LayoutKind.Sequential)]
public struct TsfStatus { public uint dwDynamicFlags, dwStaticFlags; }

// ---------------------------------------------------------------------------------------------
// Native side: vtable calls. Slot numbers from msctf.h (see the header note).
// ---------------------------------------------------------------------------------------------
public static class TsfNative
{
    [DllImport("ole32.dll")] public static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);
    public const uint CLSCTX_INPROC_SERVER = 1;

    public static readonly Guid CLSID_TF_ThreadMgr = new Guid("529A9E6B-6587-4F23-AB9E-9C7D683E3C50");
    public static readonly Guid IID_ITfThreadMgrEx = new Guid("3E90ADE3-7594-4CB0-BB58-69628F5F458C");
    public static readonly Guid IID_ITfSource = new Guid("4EA48A35-60AE-446F-8FD6-E6A8D82459F7");
    public static readonly Guid IID_ITfContextOwner = new Guid("AA80E80C-2021-11D2-93E0-0060B067B86E");
    public static readonly Guid IID_ITfTextEditSink = new Guid("8127D409-CCD3-4683-967A-B43D5B482BF7");
    // ITfContextOwnerCompositionServices derives from ITfContextComposition (4 methods), so its
    // single TerminateComposition lands in slot 7, not 3.
    public static readonly Guid IID_ITfContextOwnerCompositionServices = new Guid("86462810-593B-4916-9764-19C08E9CE110");

    public static IntPtr Slot(IntPtr p, int i) => Marshal.ReadIntPtr(Marshal.ReadIntPtr(p), i * IntPtr.Size);
    public static T Fn<T>(IntPtr p, int i) where T : class
        => (T)(object)Marshal.GetDelegateForFunctionPointer(Slot(p, i), typeof(T));
}

[UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int ActivateExFn(IntPtr p, out uint clientId, uint flags);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DeactivateFn(IntPtr p);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int CreateDocumentMgrFn(IntPtr p, out IntPtr pdm);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int SetFocusFn(IntPtr p, IntPtr pdm);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int AssociateFocusFn(IntPtr p, IntPtr hwnd, IntPtr pdm, out IntPtr prev);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int CreateContextFn(IntPtr p, uint tid, uint flags, IntPtr punk, out IntPtr ctx, out uint ec);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int PushFn(IntPtr p, IntPtr ctx);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int PopFn(IntPtr p, uint flags);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int RequestEditSessionFn(IntPtr p, uint tid, IntPtr session, uint flags, out int phr);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GetStartFn(IntPtr p, uint ec, out IntPtr range);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int RangeGetTextFn(IntPtr p, uint ec, uint flags, IntPtr buf, uint cap, out uint got);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int RangeSetTextFn(IntPtr p, uint ec, uint flags, IntPtr text, uint len);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int RangeShiftEndFn(IntPtr p, uint ec, int count, out int shifted, IntPtr halt);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int QueryInterfaceFn(IntPtr p, ref Guid riid, out IntPtr ppv);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int AdviseSinkFn(IntPtr p, ref Guid riid, IntPtr punk, out uint cookie);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int ReleaseFn(IntPtr p);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int TerminateCompositionFn(IntPtr p, IntPtr comp);

// Convenience wrappers over the raw slots.
public static class TsfCall
{
    public static int ActivateEx(IntPtr tim, out uint clientId, uint flags) => TsfNative.Fn<ActivateExFn>(tim, 14)(tim, out clientId, flags);
    public static int Deactivate(IntPtr tim) => TsfNative.Fn<DeactivateFn>(tim, 4)(tim);
    public static int CreateDocumentMgr(IntPtr tim, out IntPtr dm) => TsfNative.Fn<CreateDocumentMgrFn>(tim, 5)(tim, out dm);
    public static int SetFocus(IntPtr tim, IntPtr dm) => TsfNative.Fn<SetFocusFn>(tim, 8)(tim, dm);
    public static int AssociateFocus(IntPtr tim, IntPtr hwnd, IntPtr dm, out IntPtr prev) => TsfNative.Fn<AssociateFocusFn>(tim, 9)(tim, hwnd, dm, out prev);
    public static int CreateContext(IntPtr dm, uint tid, uint flags, IntPtr punk, out IntPtr ctx, out uint ec) => TsfNative.Fn<CreateContextFn>(dm, 3)(dm, tid, flags, punk, out ctx, out ec);
    public static int Push(IntPtr dm, IntPtr ctx) => TsfNative.Fn<PushFn>(dm, 4)(dm, ctx);
    public static int Pop(IntPtr dm, uint flags) => TsfNative.Fn<PopFn>(dm, 5)(dm, flags);
    public static int RequestEditSession(IntPtr ctx, uint tid, IntPtr session, uint flags, out int phr) => TsfNative.Fn<RequestEditSessionFn>(ctx, 3)(ctx, tid, session, flags, out phr);
    public static int GetStart(IntPtr ctx, uint ec, out IntPtr range) => TsfNative.Fn<GetStartFn>(ctx, 7)(ctx, ec, out range);
    public static int RangeGetText(IntPtr range, uint ec, uint flags, IntPtr buf, uint cap, out uint got) => TsfNative.Fn<RangeGetTextFn>(range, 3)(range, ec, flags, buf, cap, out got);
    public static int RangeSetText(IntPtr range, uint ec, uint flags, IntPtr text, uint len) => TsfNative.Fn<RangeSetTextFn>(range, 4)(range, ec, flags, text, len);
    public static int RangeShiftEnd(IntPtr range, uint ec, int count, out int shifted, IntPtr halt) => TsfNative.Fn<RangeShiftEndFn>(range, 9)(range, ec, count, out shifted, halt);
    public static int AdviseSink(IntPtr src, ref Guid riid, IntPtr punk, out uint cookie) => TsfNative.Fn<AdviseSinkFn>(src, 3)(src, ref riid, punk, out cookie);
    public static int TerminateComposition(IntPtr ownerServices, IntPtr comp) => TsfNative.Fn<TerminateCompositionFn>(ownerServices, 7)(ownerServices, comp);
    public static void Release(IntPtr p) { if (p != IntPtr.Zero) { try { TsfNative.Fn<ReleaseFn>(p, 2)(p); } catch { } } }
    public static IntPtr Qi(IntPtr p, ref Guid riid)
    {
        try
        {
            int hr = TsfNative.Fn<QueryInterfaceFn>(p, 0)(p, ref riid, out IntPtr ppv);
            return hr == 0 ? ppv : IntPtr.Zero;
        }
        catch { return IntPtr.Zero; }
    }
}

// ---------------------------------------------------------------------------------------------
// The composition host. Mirrors Microsoft::Console::TSF::Implementation.
// ---------------------------------------------------------------------------------------------
[ComVisible(true)]
public sealed class TsfCompositionHost : ITfContextOwner, ITfContextOwnerCompositionSink, ITfTextEditSink
{
    public event Action<string>? PreviewChanged;
    public event Action<string>? TextCommitted;
    public event Action<string>? Trace;

    private static readonly List<TsfCompositionHost> _live = new();   // a CCW must never be collected

    private readonly IntPtr _hwnd;
    private IntPtr _focusHwnd = IntPtr.Zero;   // the HWND TSF is actually associated with right now
    private IntPtr _appliedHwnd = IntPtr.Zero; // HWND of the current association (skip redundant work)
    private bool _focused;                     // is TSF currently focused on _appliedHwnd?
    private readonly Func<TsfRect> _caretRect;
    private readonly Func<TsfRect> _viewportRect;

    private IntPtr _tim = IntPtr.Zero, _dm = IntPtr.Zero, _ctx = IntPtr.Zero, _ownerServices = IntPtr.Zero;
    private uint _clientId;
    private bool _active, _sessionPending;
    private int _compositions;
    private string _lastPreview = "";
    private readonly EditSession _editSession;

    public TsfCompositionHost(IntPtr hwnd, Func<TsfRect> caretRect, Func<TsfRect> viewportRect)
    {
        _hwnd = hwnd; _caretRect = caretRect; _viewportRect = viewportRect;
        _editSession = new EditSession(this);
        lock (_live) _live.Add(this);
    }

    public bool IsActive => _active;
    private void Log(string s) => Trace?.Invoke(s);

    // WT step 1-6: thread manager -> document manager -> transitory context -> sinks -> push.
    public bool Attach()
    {
        if (_active) return true;
        try
        {
            Guid clsid = TsfNative.CLSID_TF_ThreadMgr, iid = TsfNative.IID_ITfThreadMgrEx;
            int hr = TsfNative.CoCreateInstance(ref clsid, IntPtr.Zero, TsfNative.CLSCTX_INPROC_SERVER, ref iid, out _tim);
            Log($"CoCreateInstance(TF_ThreadMgr) hr=0x{hr:X8} tim=0x{_tim:X}");
            if (hr != 0 || _tim == IntPtr.Zero) return false;

            uint flags = TsfConst.TF_TMAE_NOACTIVATETIP | TsfConst.TF_TMAE_NOACTIVATEKEYBOARDLAYOUT;
            hr = TsfCall.ActivateEx(_tim, out _clientId, flags);
            Log($"ActivateEx(flags=0x{flags:X}) hr=0x{hr:X8} clientId={_clientId}");
            // S_FALSE (1) = the thread manager was already activated on this thread. Not an error.
            if (hr != 0 && hr != 1) return false;

            hr = TsfCall.CreateDocumentMgr(_tim, out _dm);
            Log($"CreateDocumentMgr hr=0x{hr:X8} dm=0x{_dm:X}");
            if (hr != 0 || _dm == IntPtr.Zero) return false;

            IntPtr sink = Marshal.GetComInterfaceForObject(this, typeof(ITfContextOwnerCompositionSink));
            hr = TsfCall.CreateContext(_dm, _clientId, 0, sink, out _ctx, out uint ecTextStore);
            Log($"CreateContext hr=0x{hr:X8} ctx=0x{_ctx:X} ecTextStore={ecTextStore}");
            if (hr != 0 || _ctx == IntPtr.Zero) return false;

            Guid iidSrc = TsfNative.IID_ITfSource;
            IntPtr src = TsfCall.Qi(_ctx, ref iidSrc);
            if (src != IntPtr.Zero)
            {
                Guid iidOwner = TsfNative.IID_ITfContextOwner;
                IntPtr ownerIface = Marshal.GetComInterfaceForObject(this, typeof(ITfContextOwner));
                hr = TsfCall.AdviseSink(src, ref iidOwner, ownerIface, out uint cOwner);
                Log($"AdviseSink(ITfContextOwner) hr=0x{hr:X8} cookie={cOwner}");

                Guid iidEdit = TsfNative.IID_ITfTextEditSink;
                IntPtr editIface = Marshal.GetComInterfaceForObject(this, typeof(ITfTextEditSink));
                hr = TsfCall.AdviseSink(src, ref iidEdit, editIface, out uint cEdit);
                Log($"AdviseSink(ITfTextEditSink) hr=0x{hr:X8} cookie={cEdit}");
                TsfCall.Release(src);
            }
            else Log("ctx->QI(ITfSource) FAILED");

            hr = TsfCall.Push(_dm, _ctx);
            Log($"Push(ctx) hr=0x{hr:X8}");
            if (hr != 0) return false;

            // Needed to cancel a live composition when the mode stops accepting text.
            Guid iidOwnerSvc = TsfNative.IID_ITfContextOwnerCompositionServices;
            _ownerServices = TsfCall.Qi(_ctx, ref iidOwnerSvc);
            Log($"ctx->QI(ITfContextOwnerCompositionServices)=0x{_ownerServices:X}");

            _active = true;
            Log("TSF transitory context ready");
            return true;
        }
        catch (Exception ex) { Log("Attach threw " + ex.GetType().Name + ": " + ex.Message); return false; }
    }

    // WT: AssociateFocus(hwnd, dm) on window focus, then SetFocus(dm) so TSF routes keys here.
    // The HWND is re-supplied on every focus because WinUI hands keyboard ownership to whatever
    // HWND its input bridge is using, which is not always the top-level one.
    public void Focus() => FocusOn(IntPtr.Zero);

    public void FocusOn(IntPtr hwnd)
    {
        if (!_active) return;
        try
        {
            if (hwnd != IntPtr.Zero) _focusHwnd = hwnd;
            IntPtr target = _focusHwnd != IntPtr.Zero ? _focusHwnd : _hwnd;
            // Never re-associate with the same window twice. Focus is re-asserted on every keystroke
            // (the XAML path reclaims it), and each AssociateFocus+SetFocus is a synchronous trip
            // through msctf; redoing it also re-opens the window in which a transient GetFocus()
            // blip can park TSF on the wrong HWND.
            if (_focused && target == _appliedHwnd) return;
            int hr = TsfCall.AssociateFocus(_tim, target, _dm, out IntPtr prev);
            TsfCall.Release(prev);
            Log($"AssociateFocus(hwnd=0x{target:X}) hr=0x{hr:X8}");
            hr = TsfCall.SetFocus(_tim, _dm);
            Log($"SetFocus(dm) hr=0x{hr:X8}");
            _focused = true;
            _appliedHwnd = target;
        }
        catch (Exception ex) { Log("Focus threw " + ex.Message); }
    }

    // Detach the IME from this window: cancel any live composition and drop TSF focus entirely.
    // With no focused document manager msctf stops routing this thread's keys into the IME, so
    // normal-mode keys (and the IME hotkeys) reach the app untouched. That is what makes "the IME
    // is only active in text-input modes" real instead of merely hiding the preview.
    public void Unfocus()
    {
        if (!_active) return;
        try
        {
            if (_compositions > 0 && _ownerServices != IntPtr.Zero)
            {
                int hrT = TsfCall.TerminateComposition(_ownerServices, IntPtr.Zero);
                Log($"TerminateComposition hr=0x{hrT:X8}");
                _compositions = 0;
            }

            IntPtr target = _focusHwnd != IntPtr.Zero ? _focusHwnd : _hwnd;
            int hr = TsfCall.AssociateFocus(_tim, target, IntPtr.Zero, out IntPtr prev);
            TsfCall.Release(prev);
            Log($"AssociateFocus(hwnd=0x{target:X}, NULL) hr=0x{hr:X8}");

            hr = TsfCall.SetFocus(_tim, IntPtr.Zero);
            Log($"SetFocus(NULL) hr=0x{hr:X8}");
            _focused = false;
            _appliedHwnd = IntPtr.Zero;

            if (_lastPreview.Length > 0) { _lastPreview = ""; PreviewChanged?.Invoke(""); }
        }
        catch (Exception ex) { Log("Unfocus threw " + ex.Message); }
    }

    public void Detach()
    {
        try
        {
            _active = false;
            TsfCall.Release(_ownerServices); _ownerServices = IntPtr.Zero;
            TsfCall.Release(_ctx); _ctx = IntPtr.Zero;
            TsfCall.Release(_dm); _dm = IntPtr.Zero;
            if (_tim != IntPtr.Zero) { TsfCall.Deactivate(_tim); TsfCall.Release(_tim); _tim = IntPtr.Zero; }
            Log("Detach done");
        }
        catch { }
    }

    private void RequestSession(string why)
    {
        if (!_active || _ctx == IntPtr.Zero) return;
        if (_sessionPending) { Log($"RequestEditSession({why}) skipped: one already in flight"); return; }
        _sessionPending = true;
        IntPtr sess = Marshal.GetComInterfaceForObject(_editSession, typeof(ITfEditSession));
        int hr = TsfCall.RequestEditSession(_ctx, _clientId, sess, TsfConst.TF_ES_READWRITE | TsfConst.TF_ES_ASYNC, out int phr);
        Log($"RequestEditSession({why}) hr=0x{hr:X8} phr=0x{phr:X8}");
    }

    // WT _doCompositionUpdate: read the context, keep active composition as the preview, and
    // send finalized text out after erasing it from the context (we cannot un-send text).
    private void DoSession(uint ec)
    {
        _sessionPending = false;
        try
        {
            string text = ReadAllText(ec);
            bool composing = _compositions > 0;
            Log($"DoEditSession ec={ec} composing={composing} text='{text}'");
            if (composing)
            {
                if (text != _lastPreview) { _lastPreview = text; PreviewChanged?.Invoke(text); }
            }
            else
            {
                // The composition ENDED. An empty result must clear the preview just like a non-empty
                // one must be committed: backspacing the last character ends the composition with no
                // text at all, so requiring non-empty text here left the last preedit on screen
                // forever -- the reported "deleting the last preview character does nothing".
                if (_lastPreview.Length > 0) { _lastPreview = ""; PreviewChanged?.Invoke(""); }
                if (!string.IsNullOrEmpty(text))
                {
                    // Erase before publishing: text we have handed to nvim can never be un-sent, so
                    // the context must not keep a copy that a later composition could re-report.
                    EraseAll(ec);
                    TextCommitted?.Invoke(text);
                }
            }
        }
        catch (Exception ex) { Log("DoSession threw " + ex.GetType().Name + ": " + ex.Message); }
    }

    private string ReadAllText(uint ec)
    {
        IntPtr range;
        if (TsfCall.GetStart(_ctx, ec, out range) != 0 || range == IntPtr.Zero) return "";
        try
        {
            TsfCall.RangeShiftEnd(range, ec, TsfConst.LONG_MAX, out int len, IntPtr.Zero);
            if (len <= 0) return "";
            int cap = len + 1;
            IntPtr buf = Marshal.AllocHGlobal(cap * 2);
            try
            {
                if (TsfCall.RangeGetText(range, ec, 0, buf, (uint)cap, out uint got) != 0) return "";
                return got > 0 ? (Marshal.PtrToStringUni(buf, (int)got) ?? "") : "";
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { TsfCall.Release(range); }
    }

    private void EraseAll(uint ec)
    {
        IntPtr range;
        if (TsfCall.GetStart(_ctx, ec, out range) != 0 || range == IntPtr.Zero) return;
        try
        {
            TsfCall.RangeShiftEnd(range, ec, TsfConst.LONG_MAX, out int len, IntPtr.Zero);
            TsfCall.RangeSetText(range, ec, 0, IntPtr.Zero, 0);
        }
        finally { TsfCall.Release(range); }
    }

    // ---- ITfContextOwner (WT: extent for the candidate window, transitory status) -------------
    int ITfContextOwner.GetACPFromPoint(IntPtr ptScreen, uint dwFlags, out int pacp) { pacp = -1; return TsfConst.E_NOTIMPL; }
    int ITfContextOwner.GetTextExt(int acpStart, int acpEnd, IntPtr prc, IntPtr pfClipped)
    {
        try
        {
            if (prc != IntPtr.Zero) Marshal.StructureToPtr(_caretRect(), prc, false);
            if (pfClipped != IntPtr.Zero) Marshal.WriteInt32(pfClipped, 0);
        }
        catch { }
        return TsfConst.S_OK;
    }
    int ITfContextOwner.GetScreenExt(IntPtr prc)
    {
        try { if (prc != IntPtr.Zero) Marshal.StructureToPtr(_viewportRect(), prc, false); }
        catch { }
        return TsfConst.S_OK;
    }
    // TS_SS_TRANSITORY is the load-bearing flag: it turns on CUAS (the IMM32 emulation) and
    // tells TSF we keep no document state -- which is true, we erase after every commit.
    int ITfContextOwner.GetStatus(IntPtr pdcs)
    {
        try
        {
            var st = new TsfStatus { dwDynamicFlags = 0, dwStaticFlags = TsfConst.TS_SS_TRANSITORY | TsfConst.TS_SS_NOHIDDENTEXT };
            if (pdcs != IntPtr.Zero) Marshal.StructureToPtr(st, pdcs, false);
        }
        catch { }
        return TsfConst.S_OK;
    }
    int ITfContextOwner.GetWnd(out IntPtr phwnd) { phwnd = _focusHwnd != IntPtr.Zero ? _focusHwnd : _hwnd; return TsfConst.S_OK; }
    int ITfContextOwner.GetAttribute(ref Guid rguidAttribute, IntPtr pvarValue)
    {
        try { if (pvarValue != IntPtr.Zero) Marshal.WriteInt16(pvarValue, 0, 0); }  // VT_EMPTY
        catch { }
        return TsfConst.S_OK;
    }

    // ---- ITfContextOwnerCompositionSink --------------------------------------------------------
    int ITfContextOwnerCompositionSink.OnStartComposition(IntPtr pComposition, out int pfOk)
    {
        _compositions++;
        pfOk = 1;
        Log($"OnStartComposition (depth={_compositions})");
        RequestSession("start");
        return TsfConst.S_OK;
    }
    int ITfContextOwnerCompositionSink.OnUpdateComposition(IntPtr pComposition, IntPtr pRangeNew)
    {
        Log("OnUpdateComposition");
        RequestSession("update");
        return TsfConst.S_OK;
    }
    int ITfContextOwnerCompositionSink.OnEndComposition(IntPtr pComposition)
    {
        if (_compositions > 0) _compositions--;
        Log($"OnEndComposition (depth={_compositions})");
        if (_compositions == 0) RequestSession("end");
        return TsfConst.S_OK;
    }

    // ---- ITfTextEditSink -----------------------------------------------------------------------
    int ITfTextEditSink.OnEndEdit(IntPtr pic, uint ecReadOnly, IntPtr pEditRecord)
    {
        Log("OnEndEdit");
        RequestSession("endedit");
        return TsfConst.S_OK;
    }

    private sealed class EditSession : ITfEditSession
    {
        private readonly TsfCompositionHost _owner;
        public EditSession(TsfCompositionHost owner) { _owner = owner; }
        public int DoEditSession(uint ec) { try { _owner.DoSession(ec); } catch { } return TsfConst.S_OK; }
    }
}
