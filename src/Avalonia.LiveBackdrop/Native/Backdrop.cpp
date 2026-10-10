// Windows 11 shared-visual backend. See REFERENCE-LICENSE.txt for upstream attribution.
#define UNICODE
#define _UNICODE
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <commctrl.h>
#include <dwmapi.h>
#include <d3d11.h>
#include <dcomp.h>
#include <d2d1.h>
#include <dxgi.h>
#include <wrl/client.h>
#include <algorithm>
#include <cmath>
#include <memory>
#include <vector>
#include <unordered_map>
#include "Geometry.h"

using Microsoft::WRL::ComPtr;
using CreateShared = HRESULT(WINAPI*)(HWND, void*, void**, HTHUMBNAIL*);
using UpdateShared = HRESULT(WINAPI*)(HTHUMBNAIL, HWND*, DWORD, HWND*, DWORD, RECT*, SIZE*, DWORD);
using CreateThumbnail = HRESULT(WINAPI*)(HWND, HWND, DWORD, DWM_THUMBNAIL_PROPERTIES*, void*, void**, HTHUMBNAIL*);
struct CompositionAttribute { int attribute; void* data; SIZE_T size; };
using SetAttribute = BOOL(WINAPI*)(HWND, CompositionAttribute*);
using FailureCallback = void(__cdecl*)(HRESULT);

static void Check(HRESULT result) { if (FAILED(result)) throw result; }
static void Require(BOOL success)
{
    if (!success)
    {
        const auto error = GetLastError();
        throw HRESULT_FROM_WIN32(error ? error : ERROR_GEN_FAILURE);
    }
}

// Win32 and DXGI otherwise virtualize rectangles according to the caller's DPI context.
// The helper HWND and all source/destination coordinates must use the same physical pixels.
class PhysicalPixels
{
    DPI_AWARENESS_CONTEXT previous{};
public:
    PhysicalPixels()
    {
        previous = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        Require(previous != nullptr);
    }
    ~PhysicalPixels() { SetThreadDpiAwarenessContext(previous); }
};

struct ShellWindow
{
    HWND window;
    RECT rect;
    bool operator==(const ShellWindow& other) const
    {
        return window == other.window && EqualRect(&rect, &other.rect);
    }
};

class Session;
static thread_local std::unordered_map<HWINEVENTHOOK, Session*> sessions;

/// Owns hooks, thumbnails, compositor objects and the helper HWND on its creating UI thread.
/// Destruction restores only the host style bits this session changes. No member is thread-safe.
class Session
{
    HWND owner{}, helper{};
    HMODULE dwm{}, module{};
    bool comInitialized{}, subclassed{}, styleChanged{}, pending{}, failed{}, updating{}, displayChanged{};
    LONG_PTR savedStyleBits{};
    DWORD thread = GetCurrentThreadId();
    FailureCallback notify{};
    float blurRadius{};
    HMONITOR monitor{};
    LUID adapterId{};
    RECT monitorBounds{}, appliedBounds{}, shellSource{};
    CreateShared createShared{};
    UpdateShared updateShared{};
    CreateThumbnail createThumbnail{};
    ComPtr<ID3D11Device> graphics;
    ComPtr<IDXGIFactory1> displayFactory;
    ComPtr<IDCompositionDesktopDevice> desktop;
    ComPtr<IDCompositionDevice3> effects;
    ComPtr<IDCompositionTarget> target;
    ComPtr<IDCompositionVisual2> group, appVisual;
    ComPtr<IDCompositionGaussianBlurEffect> blur;
    // Private shared-visual identifiers are borrowed update tokens. The visual owns the
    // registration; they are not registrations acquired through DwmRegisterThumbnail.
    HTHUMBNAIL thumbnail{};
    std::vector<ComPtr<IDCompositionVisual2>> shellVisuals;
    std::vector<ShellWindow> shellWindows;
    std::vector<HWINEVENTHOOK> hooks;
    static constexpr LONG_PTR styleMask = WS_EX_TOOLWINDOW | WS_EX_APPWINDOW;

    static LRESULT CALLBACK HelperProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
    {
        auto self = reinterpret_cast<Session*>(GetWindowLongPtrW(window, GWLP_USERDATA));
        if (message == WM_NCCREATE)
        {
            self = static_cast<Session*>(reinterpret_cast<CREATESTRUCTW*>(lParam)->lpCreateParams);
            SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
        }
        if (message == WM_NCHITTEST) return HTTRANSPARENT;
        if (message == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
        if (message == WM_ERASEBKGND) return 1;
        if (message == WM_PAINT)
        {
            PAINTSTRUCT paint{};
            BeginPaint(window, &paint);
            EndPaint(window, &paint);
            return 0;
        }
        if (self && message == WM_TIMER && wParam == 1)
        {
            KillTimer(window, 1);
            self->pending = false;
            self->Refresh();
            return 0;
        }
        if (self && message == WM_DISPLAYCHANGE)
        {
            self->displayChanged = true;
            self->Queue();
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }

    static LRESULT CALLBACK OwnerProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam,
        UINT_PTR id, DWORD_PTR data)
    {
        auto self = reinterpret_cast<Session*>(data);
        if (message == WM_NCDESTROY)
        {
            RemoveWindowSubclass(window, OwnerProc, id);
            self->subclassed = false;
            self->owner = nullptr;
            ShowWindow(self->helper, SW_HIDE);
        }
        else if (message == WM_SHOWWINDOW && !wParam)
            ShowWindow(self->helper, SW_HIDE);
        else if (message == WM_WINDOWPOSCHANGED || message == WM_SIZE
            || message == WM_DPICHANGED || message == WM_DISPLAYCHANGE)
        {
            if (message == WM_DISPLAYCHANGE) self->displayChanged = true;
            self->Refresh();
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    static void CALLBACK WindowEvent(HWINEVENTHOOK hook, DWORD event, HWND window,
        LONG object, LONG child, DWORD, DWORD)
    {
        const auto found = sessions.find(hook);
        if (found == sessions.end() || !window) return;
        auto self = found->second;
        // Ignore every owned companion, including those attached to another host. Moving a
        // helper must not schedule a new composition update in another backdrop session.
        for (const auto& entry : sessions)
            if (window == entry.second->helper) return;
        if (window == self->owner && event != EVENT_SYSTEM_FOREGROUND) return;
        if (event >= EVENT_OBJECT_CREATE && (object != OBJID_WINDOW || child != 0)) return;
        if (event != EVENT_OBJECT_DESTROY && event != EVENT_OBJECT_REORDER
            && GetAncestor(window, GA_ROOT) != window) return;
        self->Queue();
    }

    void Queue()
    {
        if (!failed && !pending && !updating && helper && owner && IsWindowVisible(owner))
        {
            pending = SetTimer(helper, 1, 16, nullptr) != 0;
            if (!pending) Fail(HRESULT_FROM_WIN32(ERROR_NOT_ENOUGH_MEMORY));
        }
    }

    void Fail(HRESULT result)
    {
        if (failed) return;
        failed = true;
        if (helper) KillTimer(helper, 1);
        pending = false;
        if (helper) ShowWindow(helper, SW_HIDE);
        // Managed callback only queues notification. It must not dispose this session here.
        if (notify) notify(result);
    }

    void ClearShell()
    {
        for (const auto& visual : shellVisuals) if (group) group->RemoveVisual(visual.Get());
        shellVisuals.clear();
        shellWindows.clear();
    }

    void SyncShell(const RECT& source)
    {
        std::vector<ShellWindow> windows;
        EnumWindows([](HWND window, LPARAM data) -> BOOL
        {
            wchar_t name[64]{};
            GetClassNameW(window, name, 64);
            if (IsWindowVisible(window) && (wcscmp(name, L"Progman") == 0 || wcscmp(name, L"WorkerW") == 0))
            {
                RECT rect{};
                if (GetWindowRect(window, &rect))
                    reinterpret_cast<std::vector<ShellWindow>*>(data)->push_back({window, rect});
            }
            return TRUE;
        }, reinterpret_cast<LPARAM>(&windows));
        if (windows == shellWindows && EqualRect(&source, &shellSource)) return;
        ClearShell();
        shellVisuals.reserve(windows.size());
        // EnumWindows is front-to-back; insert each shell beneath the preceding shell.
        for (const auto& item : windows)
        {
            DWM_THUMBNAIL_PROPERTIES properties{};
            if (!ShellThumbnailBounds(item.rect, source, properties.rcSource, properties.rcDestination)) continue;
            properties.dwFlags = DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_RECTDESTINATION
                | DWM_TNP_RECTSOURCE | DWM_TNP_SOURCECLIENTAREAONLY | 0x04000000;
            properties.fVisible = TRUE;
            properties.opacity = 255;
            ComPtr<IDCompositionVisual2> visual;
            HTHUMBNAIL handle{};
            Check(createThumbnail(helper, item.window, 2, &properties, desktop.Get(),
                reinterpret_cast<void**>(visual.GetAddressOf()), &handle));
            shellVisuals.push_back(visual);
            Check(group->AddVisual(visual.Get(), TRUE, nullptr));
        }
        shellWindows = std::move(windows);
        shellSource = source;
    }

    static ComPtr<IDXGIAdapter1> DisplayAdapter(HMONITOR display, ComPtr<IDXGIFactory1>& factory)
    {
        Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)));
        for (UINT index = 0; ; ++index)
        {
            ComPtr<IDXGIAdapter1> adapter;
            const auto result = factory->EnumAdapters1(index, &adapter);
            if (result == DXGI_ERROR_NOT_FOUND) break;
            Check(result);
            for (UINT outputIndex = 0; ; ++outputIndex)
            {
                ComPtr<IDXGIOutput> output;
                const auto outputResult = adapter->EnumOutputs(outputIndex, &output);
                if (outputResult == DXGI_ERROR_NOT_FOUND) break;
                Check(outputResult);
                DXGI_OUTPUT_DESC description{};
                Check(output->GetDesc(&description));
                if (description.AttachedToDesktop && description.Monitor == display) return adapter;
            }
        }
        // Do not silently route a monitor's shared visuals through an unrelated adapter.
        throw DXGI_ERROR_NOT_FOUND;
    }

    void ReleaseComposition() noexcept
    {
        if (helper) { KillTimer(helper, 1); ShowWindow(helper, SW_HIDE); }
        pending = false;
        if (target) target->SetRoot(nullptr);
        if (desktop && SUCCEEDED(desktop->Commit())) desktop->WaitForCommitCompletion();
        ClearShell();
        thumbnail = nullptr;
        blur.Reset(); appVisual.Reset(); group.Reset(); target.Reset();
        effects.Reset(); desktop.Reset(); graphics.Reset();
        displayFactory.Reset();
        if (helper) { DestroyWindow(helper); helper = nullptr; }
        monitor = nullptr;
        adapterId = {};
        monitorBounds = {};
        appliedBounds = {};
        shellSource = {};
    }

    void CreateComposition(const RECT& bounds, IDXGIAdapter1* adapter)
    {
        // The destination already belongs to the host's output before DWM binds its visual.
        helper = CreateWindowExW(WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT,
            L"AvaloniaLiveBackdropHost", L"Avalonia live backdrop", WS_POPUP | WS_DISABLED,
            bounds.left, bounds.top, bounds.right - bounds.left, bounds.bottom - bounds.top,
            nullptr, nullptr, module, this);
        Require(helper != nullptr);
        const auto setAttribute = reinterpret_cast<SetAttribute>(GetProcAddress(GetModuleHandleW(L"user32.dll"), "SetWindowCompositionAttribute"));
        if (!setAttribute) throw E_NOTIMPL;
        BOOL exclude = TRUE;
        CompositionAttribute attribute{13, &exclude, sizeof(exclude)};
        Require(setAttribute(helper, &attribute));
        // An explicit DXGI adapter requires UNKNOWN. NULL/HARDWARE chooses adapter zero,
        // which need not own this output on an Optimus or multi-adapter desktop.
        Check(D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr, 0, D3D11_SDK_VERSION, &graphics, nullptr, nullptr));
        ComPtr<IDXGIDevice> dxgi;
        Check(graphics.As(&dxgi));
        Check(DCompositionCreateDevice3(dxgi.Get(), IID_PPV_ARGS(&desktop)));
        Check(desktop.As(&effects));
        Check(desktop->CreateTargetForHwnd(helper, FALSE, &target));
        Check(effects->CreateVisual(&group));
        Check(createShared(helper, desktop.Get(), reinterpret_cast<void**>(appVisual.GetAddressOf()), &thumbnail));
        Check(group->AddVisual(appVisual.Get(), FALSE, nullptr));
        Check(effects->CreateGaussianBlurEffect(&blur));
        Check(blur->SetStandardDeviation(blurRadius));
        Check(blur->SetBorderMode(D2D1_BORDER_MODE_HARD));
        Check(group->SetEffect(blur.Get()));
        Check(target->SetRoot(group.Get()));
    }

    void Update()
    {
        const PhysicalPixels physicalPixels;
        if (!owner || !IsWindowVisible(owner) || IsIconic(owner))
        {
            ShowWindow(helper, SW_HIDE);
            return;
        }
        DWORD cloaked{};
        if (SUCCEEDED(DwmGetWindowAttribute(owner, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))) && cloaked)
        {
            ShowWindow(helper, SW_HIDE);
            return;
        }
        RECT client{};
        Require(GetClientRect(owner, &client));
        if (IsRectEmpty(&client)) { ShowWindow(helper, SW_HIDE); return; }
        POINT origin{};
        Require(ClientToScreen(owner, &origin));
        const auto bounds = ClientScreenBounds(origin, client);
        const auto display = MonitorFromRect(&bounds, MONITOR_DEFAULTTONEAREST);
        Require(display != nullptr);
        const auto topologyChanged = !displayFactory || !displayFactory->IsCurrent();
        if (!helper || display != monitor || displayChanged || topologyChanged)
        {
            ComPtr<IDXGIFactory1> nextFactory;
            const auto adapter = DisplayAdapter(display, nextFactory);
            DXGI_ADAPTER_DESC1 description{};
            Check(adapter->GetDesc1(&description));
            MONITORINFO information{};
            information.cbSize = sizeof(information);
            Require(GetMonitorInfoW(display, &information));
            if (!helper || display != monitor || topologyChanged
                || description.AdapterLuid.LowPart != adapterId.LowPart
                || description.AdapterLuid.HighPart != adapterId.HighPart
                || !EqualRect(&information.rcMonitor, &monitorBounds))
            {
                // Shared visuals, destination HWND and GPU form one owned binding. Do not
                // display an old binding on a new output. A repeated unchanged display
                // notification does not replace it or cause another hide/show transition.
                ReleaseComposition();
                CreateComposition(bounds, adapter.Get());
                monitor = display;
                adapterId = description.AdapterLuid;
                monitorBounds = information.rcMonitor;
            }
            displayFactory = std::move(nextFactory);
            displayChanged = false;
        }
        const auto geometryChanged = !EqualRect(&bounds, &appliedBounds);
        if (geometryChanged && IsWindowVisible(helper)) ShowWindow(helper, SW_HIDE);
        // Preserve the host's other style bits. Chromium ignores tool windows for occlusion.
        auto style = GetWindowLongPtrW(owner, GWL_EXSTYLE);
        if ((style & styleMask) != WS_EX_TOOLWINDOW)
            SetWindowLongPtrW(owner, GWL_EXSTYLE, (style & ~styleMask) | WS_EX_TOOLWINDOW);
        RECT actual{};
        Require(GetWindowRect(helper, &actual));
        UINT flags = SWP_NOACTIVATE | SWP_NOOWNERZORDER;
        if (actual.left == origin.x && actual.top == origin.y) flags |= SWP_NOMOVE;
        const auto width = bounds.right - bounds.left;
        const auto height = bounds.bottom - bounds.top;
        if (actual.right - actual.left == width && actual.bottom - actual.top == height) flags |= SWP_NOSIZE;
        if (GetWindow(helper, GW_HWNDPREV) == owner) flags |= SWP_NOZORDER;
        constexpr UINT unchanged = SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER;
        if ((flags & unchanged) != unchanged)
            Require(SetWindowPos(helper, owner, bounds.left, bounds.top, width, height, flags));
        // Despite OUT annotations in the original example, ordinal 164 reads both pointers
        // into its request. The rectangle is a screen-space crop; destination size controls
        // scaling. A 1:1 client crop avoids combining unrelated monitor-sized surfaces.
        RECT source = bounds;
        SIZE size{width, height};
        std::vector<HWND> excluded;
        EnumWindows([](HWND window, LPARAM data) -> BOOL
        {
            DWORD process{};
            GetWindowThreadProcessId(window, &process);
            if (process == GetCurrentProcessId())
                reinterpret_cast<std::vector<HWND>*>(data)->push_back(window);
            return TRUE;
        }, reinterpret_cast<LPARAM>(&excluded));
        SyncShell(source);
        Check(updateShared(thumbnail, nullptr, 0, excluded.data(), static_cast<DWORD>(excluded.size()), &source, &size, 1));
        Check(desktop->Commit());
        // HWND geometry and DirectComposition are separate queues. Only a real geometry
        // transition needs synchronization before exposing the replacement frame. Ordinary
        // window/foreground refreshes do not hide, wait or stop compositor-driven pixels.
        if (geometryChanged) Check(desktop->WaitForCommitCompletion());
        appliedBounds = bounds;
        if (!IsWindowVisible(helper))
            Require(SetWindowPos(helper, owner, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_SHOWWINDOW));
    }

public:
    ~Session()
    {
        notify = nullptr;
        for (const auto hook : hooks) { sessions.erase(hook); UnhookWinEvent(hook); }
        if (helper) KillTimer(helper, 1);
        if (subclassed) RemoveWindowSubclass(owner, OwnerProc, reinterpret_cast<UINT_PTR>(this));
        ReleaseComposition();
        if (styleChanged && owner && IsWindow(owner))
        {
            const auto style = GetWindowLongPtrW(owner, GWL_EXSTYLE);
            SetWindowLongPtrW(owner, GWL_EXSTYLE, (style & ~styleMask) | savedStyleBits);
            SetWindowPos(owner, nullptr, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        if (dwm) FreeLibrary(dwm);
        if (comInitialized) CoUninitialize();
    }

    /// Borrows a same-process, same-thread host; records callback only after the first successful update.
    /// Throws an HRESULT on initialization failure; destruction also handles partial initialization.
    void Start(HWND host, float sigma, FailureCallback callback)
    {
        DWORD process{};
        if (GetWindowThreadProcessId(host, &process) != thread || process != GetCurrentProcessId()) throw E_INVALIDARG;
        owner = host;
        // Avalonia owns the COM apartment. Balance a successful initialization only.
        const auto com = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        Check(com);
        comInitialized = true;
        dwm = LoadLibraryExW(L"dwmapi.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        Require(dwm != nullptr);
        createShared = reinterpret_cast<CreateShared>(GetProcAddress(dwm, MAKEINTRESOURCEA(163)));
        updateShared = reinterpret_cast<UpdateShared>(GetProcAddress(dwm, MAKEINTRESOURCEA(164)));
        createThumbnail = reinterpret_cast<CreateThumbnail>(GetProcAddress(dwm, MAKEINTRESOURCEA(147)));
        if (!createShared || !updateShared || !createThumbnail) throw E_NOTIMPL;
        Require(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(HelperProc), &module));
        WNDCLASSW type{};
        type.lpfnWndProc = HelperProc;
        type.hInstance = module;
        type.lpszClassName = L"AvaloniaLiveBackdropHost";
        if (!RegisterClassW(&type) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) Require(FALSE);
        blurRadius = sigma;
        const auto style = GetWindowLongPtrW(owner, GWL_EXSTYLE);
        savedStyleBits = style & styleMask;
        styleChanged = true;
        SetWindowLongPtrW(owner, GWL_EXSTYLE, (style & ~styleMask) | WS_EX_TOOLWINDOW);
        Require(SetWindowSubclass(owner, OwnerProc, reinterpret_cast<UINT_PTR>(this), reinterpret_cast<DWORD_PTR>(this)));
        subclassed = true;
        const DWORD events[]{EVENT_OBJECT_CREATE, EVENT_OBJECT_DESTROY, EVENT_OBJECT_SHOW, EVENT_OBJECT_HIDE,
            EVENT_OBJECT_REORDER, EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED,
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND};
        for (const auto event : events)
        {
            const auto hook = SetWinEventHook(event, event, nullptr, WindowEvent, 0, 0, WINEVENT_OUTOFCONTEXT);
            Require(hook != nullptr);
            hooks.push_back(hook);
            sessions.emplace(hook, this);
        }
        Check(Refresh());
        notify = callback;
    }

    /// Refreshes desktop geometry/composition; reentrant updates are no-ops. Reports the first failure once.
    HRESULT Refresh() noexcept
    {
        if (GetCurrentThreadId() != thread) return RPC_E_WRONG_THREAD;
        if (failed) return E_FAIL;
        if (updating) return S_OK;
        updating = true;
        HRESULT result = S_OK;
        try { Update(); }
        catch (HRESULT error) { result = error; }
        catch (...) { result = E_FAIL; }
        updating = false;
        if (FAILED(result)) Fail(result);
        return result;
    }

    HRESULT SetBlur(float sigma) noexcept
    {
        if (GetCurrentThreadId() != thread) return RPC_E_WRONG_THREAD;
        if (!std::isfinite(sigma) || sigma < 0 || sigma > 60) return E_INVALIDARG;
        if (failed) return E_FAIL;
        auto result = blur ? blur->SetStandardDeviation(sigma) : E_UNEXPECTED;
        if (SUCCEEDED(result)) result = desktop->Commit();
        if (SUCCEEDED(result)) blurRadius = sigma;
        if (FAILED(result)) Fail(result);
        return result;
    }
};

/// Creates an owned session on owner's UI thread. output must be non-null and is cleared on failure.
/// sigma is a finite physical-pixel Gaussian deviation in [0, 60]. callback is borrowed until destroy;
/// it must not throw or destroy the session inline. Success transfers ownership to *output.
extern "C" __declspec(dllexport) HRESULT __cdecl BackdropCreate(HWND owner, float sigma, FailureCallback callback, Session** output) noexcept
{
    if (!output) return E_POINTER;
    *output = nullptr;
    if (!std::isfinite(sigma) || sigma < 0 || sigma > 60) return E_INVALIDARG;
    try
    {
        auto session = std::make_unique<Session>();
        session->Start(owner, sigma, callback);
        *output = session.release();
        return S_OK;
    }
    catch (HRESULT error) { return error; }
    catch (...) { return E_FAIL; }
}

/// Changes a live session on its creating thread. Null returns E_POINTER; failed sessions return E_FAIL.
extern "C" __declspec(dllexport) HRESULT __cdecl BackdropSetBlur(Session* session, float sigma) noexcept
{
    return session ? session->SetBlur(sigma) : E_POINTER;
}

/// Consumes a session once on its creating thread, releasing native callbacks before their managed root.
/// Null is harmless; the caller must prevent double destruction and destruction from a failure callback.
extern "C" __declspec(dllexport) void __cdecl BackdropDestroy(Session* session) noexcept
{
    delete session;
}
