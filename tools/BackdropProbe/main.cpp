// Standalone compositor experiment for issue #183. No WSGM or machine configuration writes.
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
#include <fstream>
#include <filesystem>
#include <string>
#include <stdexcept>
#include <cmath>
#include <vector>

using Microsoft::WRL::ComPtr;
using CreateShared = HRESULT(WINAPI*)(HWND, void*, void**, HTHUMBNAIL*);
using UpdateShared = HRESULT(WINAPI*)(HTHUMBNAIL, HWND*, DWORD, HWND*, DWORD, RECT*, SIZE*, DWORD);
using CreateThumbnail = HRESULT(WINAPI*)(HWND, HWND, DWORD, DWM_THUMBNAIL_PROPERTIES*, void*, void**, HTHUMBNAIL*);
struct CompositionAttribute { int attribute; void* data; SIZE_T size; };
using SetAttribute = BOOL(WINAPI*)(HWND, CompositionAttribute*);

static std::ofstream logFile;
static HWND mainWindow, animationWindow, statusWindow, controlsWindow;
static HFONT uiFont;
static HMODULE dwmLibrary;
static CreateShared createShared;
static UpdateShared updateShared;
static CreateThumbnail createThumbnail;
static SetAttribute setAttribute;
static bool fullScreen;
static RECT savedRect;
static int currentMode;
static std::wstring failure;
static std::filesystem::path logPath;
static DWORD windowsBuild;
static HWND blurSlider, blurLabel;
static int blurStrength = 18;
static std::vector<HWINEVENTHOOK> windowHooks;
static bool refreshPending;
static constexpr UINT_PTR refreshTimer = 2;

static void Log(const std::string& text)
{
    SYSTEMTIME now{};
    GetLocalTime(&now);
    logFile << now.wHour << ':' << now.wMinute << ':' << now.wSecond << " " << text << std::endl;
}

static void Check(HRESULT result, const char* operation, bool quiet = false)
{
    char line[256]{};
    sprintf_s(line, "%s: 0x%08lX", operation, static_cast<unsigned long>(result));
    if (!quiet || FAILED(result))
        Log(line);
    if (FAILED(result))
        throw std::runtime_error(line);
}

struct Backdrop
{
    ComPtr<ID3D11Device> graphics;
    ComPtr<IDCompositionDesktopDevice> desktop;
    ComPtr<IDCompositionDevice3> effects;
    ComPtr<IDCompositionTarget> target;
    ComPtr<IDCompositionVisual2> visual;
    ComPtr<IDCompositionVisual2> group;
    std::vector<ComPtr<IDCompositionVisual2>> shellVisuals;
    std::vector<HTHUMBNAIL> shellThumbnails;
    ComPtr<IDCompositionGaussianBlurEffect> blur;
    HTHUMBNAIL thumbnail{};

    void Clear()
    {
        if (target)
            target->SetRoot(nullptr);
        if (desktop)
            desktop->Commit();
        if (thumbnail)
            DwmUnregisterThumbnail(thumbnail);
        thumbnail = nullptr;
        for (const auto shellThumbnail : shellThumbnails)
            DwmUnregisterThumbnail(shellThumbnail);
        shellThumbnails.clear();
        shellVisuals.clear();
        group.Reset();
        visual.Reset();
        blur.Reset();
        target.Reset();
        effects.Reset();
        desktop.Reset();
        graphics.Reset();
    }

    void Update(bool quiet = false)
    {
        if (!thumbnail)
            return;
        RECT client{};
        GetClientRect(mainWindow, &client);
        if (client.right <= 0 || client.bottom <= 0 || IsIconic(mainWindow))
            return;
        POINT origin{};
        ClientToScreen(mainWindow, &origin);
        const auto left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        const auto top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        const auto width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        const auto height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        RECT source{left, top, left + width, top + height};
        SIZE size{width, height};
        HWND excluded[]{mainWindow, controlsWindow};
        Check(updateShared(thumbnail, nullptr, 0, excluded, 2, &source, &size, 1),
              "DwmpUpdateSharedMultiWindowVisual", quiet);
        Check(group->SetOffsetX(static_cast<float>(left - origin.x)), "Set group X offset", quiet);
        Check(group->SetOffsetY(static_cast<float>(top - origin.y)), "Set group Y offset", quiet);
        if (!quiet)
            Log("Shared source rect " + std::to_string(source.left) + "," + std::to_string(source.top)
            + " to " + std::to_string(source.right) + "," + std::to_string(source.bottom)
            + "; destination " + std::to_string(size.cx) + "x" + std::to_string(size.cy));
        Check(desktop->Commit(), "DirectComposition Commit", quiet);
    }

    void AddShell()
    {
        if (!createThumbnail)
            throw std::runtime_error("Shared thumbnail export 147 unavailable");
        std::vector<HWND> windows;
        EnumWindows([](HWND window, LPARAM parameter) -> BOOL
        {
            wchar_t name[64]{};
            GetClassNameW(window, name, 64);
            if (IsWindowVisible(window) && (wcscmp(name, L"Progman") == 0 || wcscmp(name, L"WorkerW") == 0))
                reinterpret_cast<std::vector<HWND>*>(parameter)->push_back(window);
            return TRUE;
        }, reinterpret_cast<LPARAM>(&windows));
        for (auto item = windows.rbegin(); item != windows.rend(); ++item)
        {
            RECT rect{};
            GetWindowRect(*item, &rect);
            DWM_THUMBNAIL_PROPERTIES properties{};
            properties.dwFlags = DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_RECTDESTINATION
                | DWM_TNP_RECTSOURCE | DWM_TNP_SOURCECLIENTAREAONLY | 0x04000000;
            properties.fVisible = TRUE;
            properties.opacity = 255;
            properties.rcSource = RECT{0, 0, rect.right - rect.left, rect.bottom - rect.top};
            OffsetRect(&rect, -GetSystemMetrics(SM_XVIRTUALSCREEN), -GetSystemMetrics(SM_YVIRTUALSCREEN));
            properties.rcDestination = rect;
            ComPtr<IDCompositionVisual2> shell;
            HTHUMBNAIL handle{};
            const auto result = createThumbnail(mainWindow, *item, 2, &properties, desktop.Get(),
                reinterpret_cast<void**>(shell.GetAddressOf()), &handle);
            if (FAILED(result))
            {
                Log("Shell thumbnail unavailable: " + std::to_string(static_cast<unsigned long>(result)));
                continue;
            }
            shellThumbnails.push_back(handle);
            shellVisuals.push_back(shell);
            // With no reference, FALSE appends above all siblings (TRUE inserts below all).
            Check(group->AddVisual(shell.Get(), FALSE, nullptr), "Add shell visual above previous shell layer");
        }
        Log("Shell visuals added: " + std::to_string(shellVisuals.size()));
    }

    void Start(bool blurred)
    {
        if (!createShared || !updateShared || !setAttribute)
            throw std::runtime_error("Required private compositor exports are unavailable");
        BOOL excluded = TRUE;
        CompositionAttribute attribute{13, &excluded, sizeof(excluded)};
        if (!setAttribute(mainWindow, &attribute))
            throw std::runtime_error("WCA_EXCLUDED_FROM_LIVEPREVIEW failed");
        if (!setAttribute(controlsWindow, &attribute))
            throw std::runtime_error("Control palette live-preview exclusion failed");
        Log("WCA_EXCLUDED_FROM_LIVEPREVIEW enabled for probe window only");
        Check(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
              D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION,
              &graphics, nullptr, nullptr), "D3D11CreateDevice hardware");
        ComPtr<IDXGIDevice> dxgi;
        Check(graphics.As(&dxgi), "Query IDXGIDevice");
        Check(DCompositionCreateDevice3(dxgi.Get(), IID_PPV_ARGS(&desktop)), "DCompositionCreateDevice3");
        Check(desktop.As(&effects), "Query IDCompositionDevice3");
        Check(desktop->CreateTargetForHwnd(mainWindow, FALSE, &target), "CreateTargetForHwnd");
        Check(effects->CreateVisual(&group), "Create app-owned effect group");
        AddShell();
        Check(createShared(mainWindow, desktop.Get(), reinterpret_cast<void**>(visual.GetAddressOf()),
              &thumbnail), "DwmpCreateSharedMultiWindowVisual");
        Check(group->AddVisual(visual.Get(), FALSE, nullptr), "Add shared windows above all shell layers");
        if (blurred)
        {
            Check(effects->CreateGaussianBlurEffect(&blur), "CreateGaussianBlurEffect");
            Check(blur->SetStandardDeviation(static_cast<float>(blurStrength)), "Set blur sigma");
            Check(blur->SetBorderMode(D2D1_BORDER_MODE_HARD), "Blur hard border");
            Check(group->SetEffect(blur.Get()), "Attach blur to complete app-owned group");
        }
        Check(target->SetRoot(group.Get()), "Set compositor root");
        Update();
    }
    ~Backdrop() { Clear(); }
};

static Backdrop backdrop;

static void QueueBackdropRefresh()
{
    // One-shot throttle, not a trailing debounce: dragging must keep updating.
    if (backdrop.thumbnail && !refreshPending)
        refreshPending = SetTimer(mainWindow, refreshTimer, 16, nullptr) != 0;
}

static void CALLBACK WindowEvent(HWINEVENTHOOK, DWORD event, HWND window,
    LONG object, LONG child, DWORD, DWORD)
{
    // OUTOFCONTEXT delivers callbacks on the message-loop thread. Do not touch COM here.
    if (!window || window == mainWindow || window == controlsWindow
        || IsChild(mainWindow, window) || IsChild(controlsWindow, window))
        return;
    if (event >= EVENT_OBJECT_CREATE && (object != OBJID_WINDOW || child != 0))
        return;
    // A destroyed HWND cannot be queried. Reorder can describe the desktop's children.
    if (event != EVENT_OBJECT_DESTROY && event != EVENT_OBJECT_REORDER
        && GetAncestor(window, GA_ROOT) != window)
        return;
    QueueBackdropRefresh();
}

static void StopWindowTracking()
{
    for (const auto hook : windowHooks)
        UnhookWinEvent(hook);
    windowHooks.clear();
    KillTimer(mainWindow, refreshTimer);
    refreshPending = false;
}

static bool StartWindowTracking()
{
    const DWORD events[]{EVENT_OBJECT_CREATE, EVENT_OBJECT_DESTROY, EVENT_OBJECT_SHOW,
        EVENT_OBJECT_HIDE, EVENT_OBJECT_REORDER, EVENT_OBJECT_LOCATIONCHANGE,
        EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED, EVENT_SYSTEM_FOREGROUND,
        EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND};
    for (const auto event : events)
    {
        const auto hook = SetWinEventHook(event, event, nullptr, WindowEvent, 0, 0,
            WINEVENT_OUTOFCONTEXT);
        if (!hook)
        {
            Log("Window event hook failed: " + std::to_string(GetLastError()));
            StopWindowTracking();
            return false;
        }
        windowHooks.push_back(hook);
    }
    Log("Automatic window tracking enabled; event-driven 16ms coalescing, no capture loop");
    return true;
}

static std::wstring SettingsDescription()
{
    DWORD transparency{}, size = sizeof(transparency);
    const auto result = RegGetValueW(HKEY_CURRENT_USER,
        L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize",
        L"EnableTransparency", RRF_RT_REG_DWORD, nullptr, &transparency, &size);
    BOOL composition{};
    DwmIsCompositionEnabled(&composition);
    return L"Windows transparency: " + std::wstring(result == ERROR_SUCCESS
        ? (transparency ? L"ON" : L"OFF") : L"not specified")
        + L" | DWM composition: " + (composition ? L"ON" : L"OFF");
}

static void UpdateStatus()
{
    const wchar_t* names[]{L"0 - Clear control (no blur)", L"1 - Windows system acrylic",
        L"2 - Private shared visual (no blur)", L"3 - Private shared visual + Gaussian blur"};
    std::wstring text = std::wstring(names[currentMode]) + L" | Windows build " + std::to_wstring(windowsBuild)
        + L"\r\n" + SettingsDescription() + L" | Tool window: "
        + ((GetWindowLongPtrW(mainWindow, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) ? L"ON" : L"OFF")
        + L"\r\n" + (failure.empty() ? L"API calls succeeded. Confirm the picture moves; this is not proof of visible blur." : failure)
        + L"\r\nLog: " + logPath.wstring();
    SetWindowTextW(statusWindow, text.c_str());
}

static void SelectMode(int mode)
{
    Log("Selecting mode " + std::to_string(mode));
    KillTimer(mainWindow, refreshTimer);
    refreshPending = false;
    backdrop.Clear();
    BOOL excluded = FALSE;
    CompositionAttribute attribute{13, &excluded, sizeof(excluded)};
    if (setAttribute)
    {
        setAttribute(mainWindow, &attribute);
        setAttribute(controlsWindow, &attribute);
    }
    int none = 1;
    DwmSetWindowAttribute(mainWindow, static_cast<DWMWINDOWATTRIBUTE>(38), &none, sizeof(none));
    currentMode = mode;
    failure.clear();
    try
    {
        if (mode == 1)
        {
            int acrylic = 3;
            Check(DwmSetWindowAttribute(mainWindow, static_cast<DWMWINDOWATTRIBUTE>(38),
                  &acrylic, sizeof(acrylic)), "DWMWA_SYSTEMBACKDROP_TYPE acrylic");
        }
        else if (mode >= 2)
            backdrop.Start(mode == 3);
    }
    catch (const std::exception& error)
    {
        const std::string message = error.what();
        Log("FAILED: " + message);
        failure = L"FAILED: " + std::wstring(message.begin(), message.end());
        backdrop.Clear();
    }
    UpdateStatus();
    InvalidateRect(mainWindow, nullptr, TRUE);
}

static void AdjustBlur()
{
    blurStrength = static_cast<int>(SendMessageW(blurSlider, TBM_GETPOS, 0, 0));
    const auto label = L"Blur: " + std::to_wstring(blurStrength) + L" px (mode 3)";
    SetWindowTextW(blurLabel, label.c_str());
    if (!backdrop.blur)
        return;
    try
    {
        Check(backdrop.blur->SetStandardDeviation(static_cast<float>(blurStrength)), "Adjust blur sigma", true);
        Check(backdrop.desktop->Commit(), "Commit blur adjustment", true);
    }
    catch (const std::exception& error)
    {
        const std::string text = error.what();
        failure = L"Blur adjustment failed: " + std::wstring(text.begin(), text.end());
        backdrop.Clear();
        UpdateStatus();
    }
}

static void ToggleFullscreen()
{
    if (!fullScreen)
    {
        GetWindowRect(mainWindow, &savedRect);
        MONITORINFO monitor{sizeof(monitor)};
        GetMonitorInfoW(MonitorFromWindow(mainWindow, MONITOR_DEFAULTTONEAREST), &monitor);
        SetWindowLongPtrW(mainWindow, GWL_STYLE, WS_POPUP | WS_VISIBLE | WS_CLIPCHILDREN);
        SetWindowPos(mainWindow, HWND_TOPMOST, monitor.rcMonitor.left, monitor.rcMonitor.top,
            monitor.rcMonitor.right - monitor.rcMonitor.left, monitor.rcMonitor.bottom - monitor.rcMonitor.top,
            SWP_FRAMECHANGED);
    }
    else
    {
        SetWindowLongPtrW(mainWindow, GWL_STYLE, WS_OVERLAPPEDWINDOW | WS_VISIBLE | WS_CLIPCHILDREN);
        SetWindowPos(mainWindow, HWND_TOPMOST, savedRect.left, savedRect.top,
            savedRect.right - savedRect.left, savedRect.bottom - savedRect.top, SWP_FRAMECHANGED);
    }
    fullScreen = !fullScreen;
}

static LRESULT CALLBACK AnimationProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
{
    if (message == WM_CREATE)
    {
        SetTimer(window, 1, 33, nullptr);
        return 0;
    }
    if (message == WM_TIMER)
    {
        InvalidateRect(window, nullptr, FALSE);
        return 0;
    }
    if (message == WM_ERASEBKGND)
        return 1;
    if (message == WM_PAINT)
    {
        PAINTSTRUCT paint{};
        const auto dc = BeginPaint(window, &paint);
        RECT area{};
        GetClientRect(window, &area);
        const auto memory = CreateCompatibleDC(dc);
        const auto bitmap = CreateCompatibleBitmap(dc, std::max(1L, area.right), std::max(1L, area.bottom));
        const auto oldBitmap = SelectObject(memory, bitmap);
        FillRect(memory, &area, static_cast<HBRUSH>(GetStockObject(WHITE_BRUSH)));
        const int phase = static_cast<int>((GetTickCount64() / 12) % 160);
        for (int x = -160 + phase; x < area.right; x += 160)
        {
            RECT stripe{x, 0, x + 80, area.bottom};
            const auto brush = CreateSolidBrush(RGB(15, 45, 105));
            FillRect(memory, &stripe, brush);
            DeleteObject(brush);
        }
        const auto circle = CreateSolidBrush(RGB(255, 95, 25));
        const auto oldBrush = SelectObject(memory, circle);
        const int center = area.right / 2 + static_cast<int>(std::sin(GetTickCount64() / 800.0) * area.right / 3);
        Ellipse(memory, center - 100, area.bottom / 2 - 100, center + 100, area.bottom / 2 + 100);
        SelectObject(memory, oldBrush);
        DeleteObject(circle);
        SetBkColor(memory, RGB(255, 255, 255));
        SetTextColor(memory, RGB(0, 0, 0));
        const auto oldFont = SelectObject(memory, uiFont);
        const auto label = L"LIVE BACKGROUND  -  " + std::to_wstring(GetTickCount64() / 1000) + L" seconds";
        TextOutW(memory, 24, 24, label.c_str(), static_cast<int>(label.size()));
        SelectObject(memory, oldFont);
        BitBlt(dc, 0, 0, area.right, area.bottom, memory, 0, 0, SRCCOPY);
        SelectObject(memory, oldBitmap);
        DeleteObject(bitmap);
        DeleteDC(memory);
        EndPaint(window, &paint);
        return 0;
    }
    if (message == WM_CLOSE)
    {
        ShowWindow(window, SW_HIDE);
        KillTimer(window, 1);
        return 0;
    }
    return DefWindowProcW(window, message, wParam, lParam);
}

static LRESULT CALLBACK ControlsProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
{
    if (message == WM_HSCROLL && reinterpret_cast<HWND>(lParam) == blurSlider)
    {
        AdjustBlur();
        return 0;
    }
    if (message == WM_COMMAND || message == WM_CLOSE)
    {
        PostMessageW(mainWindow, message, wParam, lParam);
        return 0;
    }
    return DefWindowProcW(window, message, wParam, lParam);
}

static LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
{
    if (message == WM_GETMINMAXINFO)
    {
        const auto limits = reinterpret_cast<MINMAXINFO*>(lParam);
        limits->ptMinTrackSize = POINT{1040, 480};
        return 0;
    }
    if (message == WM_ERASEBKGND)
        return 1;
    if (message == WM_PAINT)
    {
        PAINTSTRUCT paint{};
        BeginPaint(window, &paint);
        EndPaint(window, &paint);
        return 0;
    }
    if (message == WM_COMMAND)
    {
        const int command = LOWORD(wParam);
        if (command >= 100 && command <= 103)
            SelectMode(command - 100);
        if (command == 104)
        {
            if (IsWindowVisible(animationWindow))
            {
                ShowWindow(animationWindow, SW_HIDE);
                KillTimer(animationWindow, 1);
            }
            else
            {
                RECT position{};
                GetWindowRect(window, &position);
                SetWindowPos(animationWindow, HWND_NOTOPMOST, position.left - 40, position.top - 40,
                    position.right - position.left + 80, position.bottom - position.top + 80,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW);
                SetTimer(animationWindow, 1, 33, nullptr);
            }
            Log("Animated background toggled");
        }
        if (command == 105)
            ToggleFullscreen();
        if (command == 106)
            SelectMode(currentMode);
        if (command == 107)
            PostMessageW(window, WM_CLOSE, 0, 0);
        return 0;
    }
    if (message == WM_TIMER && wParam == refreshTimer)
    {
        KillTimer(window, refreshTimer);
        refreshPending = false;
        try { backdrop.Update(true); }
        catch (const std::exception& error)
        {
            const std::string text = error.what();
            failure = L"Automatic refresh failed: " + std::wstring(text.begin(), text.end());
            backdrop.Clear();
            UpdateStatus();
        }
        return 0;
    }
    if (message == WM_SIZE || message == WM_MOVE)
    {
        try { backdrop.Update(); }
        catch (const std::exception& error)
        {
            Log(error.what());
            const std::string text = error.what();
            failure = L"Resize failed: " + std::wstring(text.begin(), text.end());
            backdrop.Clear();
            UpdateStatus();
        }
    }
    if (message == WM_SETTINGCHANGE)
        UpdateStatus();
    if (message == WM_DESTROY)
    {
        StopWindowTracking();
        backdrop.Clear();
        DestroyWindow(animationWindow);
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(window, message, wParam, lParam);
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int)
{
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    INITCOMMONCONTROLSEX commonControls{sizeof(commonControls), ICC_BAR_CLASSES};
    if (!InitCommonControlsEx(&commonControls))
        return 1;
    wchar_t executable[MAX_PATH]{};
    GetModuleFileNameW(nullptr, executable, MAX_PATH);
    SYSTEMTIME now{};
    GetLocalTime(&now);
    wchar_t name[128]{};
    swprintf_s(name, L"BackdropProbe-%04u%02u%02u-%02u%02u%02u-%lu.log",
        now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond, GetCurrentProcessId());
    logPath = std::filesystem::path(executable).parent_path() / name;
    logFile.open(logPath);
    if (!logFile)
    {
        logPath = std::filesystem::temp_directory_path() / name;
        logFile.clear();
        logFile.open(logPath);
    }
    Log("WSGM BackdropProbe v6 - adjustable live blur, automatic window tracking");
    using GetVersion = LONG(WINAPI*)(OSVERSIONINFOW*);
    const auto versionFunction = reinterpret_cast<GetVersion>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "RtlGetVersion"));
    OSVERSIONINFOW version{sizeof(version)};
    if (versionFunction)
        versionFunction(&version);
    windowsBuild = version.dwBuildNumber;
    Log("Windows " + std::to_string(version.dwMajorVersion) + "." + std::to_string(version.dwMinorVersion)
        + " build " + std::to_string(version.dwBuildNumber));
    const auto settings = SettingsDescription();
    std::string settingsText;
    for (const auto character : settings)
        settingsText.push_back(static_cast<char>(character)); // SettingsDescription is ASCII-only.
    Log(settingsText);
    if (version.dwBuildNumber < 22000)
    {
        MessageBoxW(nullptr, L"This probe targets Windows 11 build 22000 or later.", L"BackdropProbe", MB_ICONERROR);
        return 1;
    }
    const auto comResult = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(comResult))
    {
        Log("COM initialization failed");
        return 2;
    }
    dwmLibrary = LoadLibraryExW(L"dwmapi.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (dwmLibrary)
    {
        createShared = reinterpret_cast<CreateShared>(GetProcAddress(dwmLibrary, MAKEINTRESOURCEA(163)));
        updateShared = reinterpret_cast<UpdateShared>(GetProcAddress(dwmLibrary, MAKEINTRESOURCEA(164)));
        createThumbnail = reinterpret_cast<CreateThumbnail>(GetProcAddress(dwmLibrary, MAKEINTRESOURCEA(147)));
    }
    setAttribute = reinterpret_cast<SetAttribute>(GetProcAddress(GetModuleHandleW(L"user32.dll"), "SetWindowCompositionAttribute"));
    Log(std::string("Private exports: create=") + (createShared ? "yes" : "no")
        + " update=" + (updateShared ? "yes" : "no") + " attribute=" + (setAttribute ? "yes" : "no"));
    uiFont = CreateFontW(-17, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
        OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
    WNDCLASSW type{};
    type.lpfnWndProc = WindowProc;
    type.hInstance = instance;
    type.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    type.lpszClassName = L"WSGMBackdropProbe";
    RegisterClassW(&type);
    type.lpfnWndProc = AnimationProc;
    type.lpszClassName = L"WSGMBackdropProbeAnimation";
    RegisterClassW(&type);
    animationWindow = CreateWindowExW(WS_EX_NOACTIVATE, type.lpszClassName, L"BackdropProbe live motion source",
        WS_OVERLAPPEDWINDOW, 80, 80, 1120, 860, nullptr, nullptr, instance, nullptr);
    KillTimer(animationWindow, 1);
    mainWindow = CreateWindowExW(WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOPMOST | WS_EX_TOOLWINDOW, L"WSGMBackdropProbe",
        L"WSGM BackdropProbe v6 - Esc closes | F11 fullscreen | H controls | T tool flag", WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN,
        120, 120, 1060, 760, nullptr, nullptr, instance, nullptr);
    if (!mainWindow)
    {
        CoUninitialize();
        return 2;
    }
    type.lpfnWndProc = ControlsProc;
    type.lpszClassName = L"WSGMBackdropProbeControls";
    type.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_BTNFACE + 1);
    RegisterClassW(&type);
    controlsWindow = CreateWindowExW(WS_EX_TOOLWINDOW, type.lpszClassName,
        L"BackdropProbe v6 controls - H hides/shows this panel",
        WS_POPUP | WS_CAPTION | WS_SYSMENU, 145, 160, 1040, 320, mainWindow, nullptr, instance, nullptr);
    const wchar_t* labels[]{L"0  Clear", L"1  System acrylic", L"2  Shared sharp", L"3  Shared blur",
        L"Motion background", L"Fullscreen", L"Reapply / retry", L"Close"};
    for (int i = 0; i < 8; ++i)
    {
        const auto button = CreateWindowExW(0, L"BUTTON", labels[i], WS_CHILD | WS_VISIBLE | WS_TABSTOP,
            12 + (i % 4) * 246, 12 + (i / 4) * 50, 238, 44, controlsWindow,
            reinterpret_cast<HMENU>(static_cast<INT_PTR>(100 + i)), instance, nullptr);
        SendMessageW(button, WM_SETFONT, reinterpret_cast<WPARAM>(uiFont), TRUE);
    }
    blurLabel = CreateWindowExW(0, L"STATIC", L"Blur: 18 px (mode 3)", WS_CHILD | WS_VISIBLE,
        12, 122, 200, 28, controlsWindow, nullptr, instance, nullptr);
    SendMessageW(blurLabel, WM_SETFONT, reinterpret_cast<WPARAM>(uiFont), TRUE);
    blurSlider = CreateWindowExW(0, TRACKBAR_CLASSW, L"Blur strength", WS_CHILD | WS_VISIBLE | WS_TABSTOP | TBS_HORZ,
        220, 114, 766, 40, controlsWindow, nullptr, instance, nullptr);
    SendMessageW(blurSlider, TBM_SETRANGE, TRUE, MAKELPARAM(0, 60));
    SendMessageW(blurSlider, TBM_SETPAGESIZE, 0, 5);
    SendMessageW(blurSlider, TBM_SETPOS, TRUE, blurStrength);
    statusWindow = CreateWindowExW(0, L"STATIC", L"", WS_CHILD | WS_VISIBLE | SS_LEFT,
        12, 166, 1016, 120, controlsWindow, nullptr, instance, nullptr);
    SendMessageW(statusWindow, WM_SETFONT, reinterpret_cast<WPARAM>(uiFont), TRUE);
    ShowWindow(mainWindow, SW_SHOW);
    ShowWindow(controlsWindow, SW_SHOW);
    SelectMode(0);
    if (!StartWindowTracking())
    {
        failure = L"Window tracking unavailable; use Reapply / retry manually.";
        UpdateStatus();
    }
    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0)
    {
        if (message.message == WM_KEYDOWN)
        {
            if (message.wParam == VK_ESCAPE)
                PostMessageW(mainWindow, WM_CLOSE, 0, 0);
            else if (message.wParam == VK_F11)
                ToggleFullscreen();
            else if (message.wParam == 'T')
            {
                const auto style = GetWindowLongPtrW(mainWindow, GWL_EXSTYLE) ^ WS_EX_TOOLWINDOW;
                SetWindowLongPtrW(mainWindow, GWL_EXSTYLE, style);
                SetWindowPos(mainWindow, nullptr, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                Log(std::string("Tool window: ") + ((style & WS_EX_TOOLWINDOW) ? "ON" : "OFF"));
                UpdateStatus();
            }
            else if (message.wParam == 'H')
                ShowWindow(controlsWindow, IsWindowVisible(controlsWindow) ? SW_HIDE : SW_SHOW);
            else if (message.wParam >= '0' && message.wParam <= '3')
                SelectMode(static_cast<int>(message.wParam - '0'));
        }
        if (!IsDialogMessageW(controlsWindow, &message))
        {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }
    DeleteObject(uiFont);
    if (dwmLibrary)
        FreeLibrary(dwmLibrary);
    CoUninitialize();
    Log("Normal exit");
    return 0;
}
