// notchbar-tap: makes the Windows 11 taskbar's own background invisible so notchbar's glass boxes show.
//
// notchbar loads this into explorer.exe with InitializeXamlDiagnosticsEx (the XAML diagnostics API that
// Visual Studio's Live Visual Tree uses). Explorer then creates our "TAP" object and hands it the XAML
// diagnostics interface; we watch the taskbar's visual tree and set the two rectangles that paint its
// background (BackgroundFill and BackgroundStroke under Taskbar.TaskbarBackground) to Opacity 0.
// Nothing else in Explorer is touched. When notchbar exits (its single-instance mutex is released or
// abandoned) the rectangles are made visible again.

#include <windows.h>
#include <ocidl.h>
#include <xamlom.h>
#include <wrl.h>
#include <wrl/module.h>
#include <windows.ui.xaml.h>
#include <windows.ui.core.h>
#include <mutex>
#include <vector>
#include <unordered_map>
#include <unordered_set>

using namespace Microsoft::WRL;
namespace Xaml = ABI::Windows::UI::Xaml;
namespace Core = ABI::Windows::UI::Core;

static std::mutex g_lock;
static std::unordered_map<InstanceHandle, InstanceHandle> g_parent;   // element -> parent, for the walk up
static std::unordered_set<InstanceHandle> g_backgrounds;              // Taskbar.TaskbarBackground instances
static std::unordered_set<InstanceHandle> g_hiddenHandles;
static std::vector<ComPtr<Xaml::IUIElement>> g_hidden;                 // the rectangles we made invisible
static volatile LONG g_active = 0, g_watching = 0;

static bool Is(BSTR s, const wchar_t* v) { return s && wcscmp(s, v) == 0; }

/// Run on the XAML thread that owns the rectangles.
static void SetOpacity(double opacity)
{
    std::vector<ComPtr<Xaml::IUIElement>> els;
    { std::lock_guard<std::mutex> l(g_lock); els = g_hidden; }
    if (els.empty()) return;
    ComPtr<Xaml::IDependencyObject> obj;
    ComPtr<Core::ICoreDispatcher> dispatcher;
    if (FAILED(els[0].As(&obj)) || FAILED(obj->get_Dispatcher(&dispatcher)) || !dispatcher) return;
    auto work = Callback<Implements<RuntimeClassFlags<ClassicCom>, Core::IDispatchedHandler, FtmBase>>(
        [els, opacity]() -> HRESULT { for (auto& e : els) e->put_Opacity(opacity); return S_OK; });
    ComPtr<ABI::Windows::Foundation::IAsyncAction> action;
    dispatcher->RunAsync(Core::CoreDispatcherPriority_Normal, work.Get(), &action);
}

/// Wait for notchbar to exit, then give the taskbar its background back.
static DWORD WINAPI WatchNotchbar(void*)
{
    HANDLE m = OpenMutexW(SYNCHRONIZE | MUTEX_MODIFY_STATE, FALSE, L"Local\\notchbar.singleton");
    if (m)
    {
        DWORD r = WaitForSingleObject(m, INFINITE);
        if (r == WAIT_OBJECT_0 || r == WAIT_ABANDONED) ReleaseMutex(m);
        CloseHandle(m);   // promptly: a restarting notchbar checks that this mutex no longer exists
    }
    InterlockedExchange(&g_active, 0);
    SetOpacity(1.0);
    InterlockedExchange(&g_watching, 0);
    return 0;
}

class TreeWatcher : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IVisualTreeServiceCallback2>
{
    ComPtr<IXamlDiagnostics> m_diag;
public:
    TreeWatcher(IXamlDiagnostics* diag) : m_diag(diag) {}

    // Called on the UI thread: first for every element already in the tree (parents before children),
    // then for each one added or removed later (the taskbar rebuilds parts of itself now and then).
    IFACEMETHODIMP OnVisualTreeChange(ParentChildRelation rel, VisualElement el, VisualMutationType mutation) override
    {
        std::lock_guard<std::mutex> l(g_lock);
        if (mutation == Remove)
        {
            g_parent.erase(el.Handle);
            g_backgrounds.erase(el.Handle);
            return S_OK;
        }
        g_parent[el.Handle] = rel.Parent;
        if (Is(el.Type, L"Taskbar.TaskbarBackground")) g_backgrounds.insert(el.Handle);
        if (!Is(el.Type, L"Windows.UI.Xaml.Shapes.Rectangle") || !(Is(el.Name, L"BackgroundFill") || Is(el.Name, L"BackgroundStroke")))
            return S_OK;
        if (g_hiddenHandles.count(el.Handle)) return S_OK;
        bool inTaskbar = false;
        InstanceHandle p = rel.Parent;
        for (int i = 0; i < 6 && p; i++)
        {
            if (g_backgrounds.count(p)) { inTaskbar = true; break; }
            auto it = g_parent.find(p);
            if (it == g_parent.end()) break;
            p = it->second;
        }
        if (!inTaskbar) return S_OK;
        ComPtr<IInspectable> insp;
        ComPtr<Xaml::IUIElement> ui;
        if (FAILED(m_diag->GetIInspectableFromHandle(el.Handle, &insp)) || FAILED(insp.As(&ui))) return S_OK;
        if (g_active) ui->put_Opacity(0.0);
        g_hidden.push_back(ui);
        g_hiddenHandles.insert(el.Handle);
        return S_OK;
    }

    IFACEMETHODIMP OnElementStateChanged(InstanceHandle, VisualElementState, LPCWSTR) override { return S_OK; }
};

static DWORD WINAPI Advise(void* p)
{
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    {
        ComPtr<IXamlDiagnostics> diag;
        diag.Attach(static_cast<IXamlDiagnostics*>(p));
        ComPtr<IVisualTreeService3> tree;
        if (SUCCEEDED(diag.As(&tree)))
        {
            auto watcher = Make<TreeWatcher>(diag.Get());
            tree->AdviseVisualTreeChange(watcher.Get());
        }
    }
    CoUninitialize();
    return 0;
}

class __declspec(uuid("6F1A3B8E-2C4D-4E5F-9A7B-1C2D3E4F5A6B")) Tap
    : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IObjectWithSite>
{
    ComPtr<IUnknown> m_site;
public:
    IFACEMETHODIMP SetSite(IUnknown* site) override
    {
        m_site = site;
        if (!site) return S_OK;
        ComPtr<IXamlDiagnostics> diag;
        if (FAILED(site->QueryInterface(IID_PPV_ARGS(&diag)))) return S_OK;
        InterlockedExchange(&g_active, 1);
        SetOpacity(0.0);   // loaded again after notchbar restarted: hide what we already know about
        if (InterlockedCompareExchange(&g_watching, 1, 0) == 0)
            if (HANDLE t = CreateThread(nullptr, 0, WatchNotchbar, nullptr, 0, nullptr)) CloseHandle(t);
        // AdviseVisualTreeChange replays the whole tree on the UI thread and waits for it: never call it here
        if (HANDLE t = CreateThread(nullptr, 0, Advise, diag.Detach(), 0, nullptr)) CloseHandle(t);
        return S_OK;
    }

    IFACEMETHODIMP GetSite(REFIID riid, void** ppv) override
    {
        return m_site ? m_site.CopyTo(riid, ppv) : E_FAIL;
    }
};

CoCreatableClass(Tap);

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv)
{
    return Module<InProc>::GetModule().GetClassObject(clsid, riid, ppv);
}

STDAPI DllCanUnloadNow() { return S_FALSE; }   // our threads live in here

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, void*)
{
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(inst);
    return TRUE;
}
