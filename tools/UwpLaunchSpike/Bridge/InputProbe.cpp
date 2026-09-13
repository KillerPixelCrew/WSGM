#include <windows.h>
#include <roapi.h>
#include <windows.gaming.input.h>
#include <wrl/client.h>
#include <cstdio>
#include <cstdarg>
#include <intrin.h>
#include "MinHook.h"

using Microsoft::WRL::ComPtr;
using namespace ABI::Windows::Gaming::Input;

namespace
{
    using GetGamepads = HRESULT (STDMETHODCALLTYPE*)(IGamepadStatics*, __FIVectorView_1_Windows__CGaming__CInput__CGamepad**);
    GetGamepads windowsGamepads = nullptr;
    GetGamepads steamGamepads = nullptr;
    volatile LONG windowsCalls = 0;
    volatile LONG steamCalls = 0;
    void* volatile windowsCaller = nullptr;
    void* volatile steamCaller = nullptr;
    HRESULT STDMETHODCALLTYPE TraceWindows(IGamepadStatics* self, __FIVectorView_1_Windows__CGaming__CInput__CGamepad** value)
    {
        InterlockedIncrement(&windowsCalls);
        InterlockedExchangePointer(&windowsCaller, _ReturnAddress());
        return windowsGamepads(self, value);
    }
    HRESULT STDMETHODCALLTYPE TraceSteam(IGamepadStatics* self, __FIVectorView_1_Windows__CGaming__CInput__CGamepad** value)
    {
        InterlockedIncrement(&steamCalls);
        InterlockedExchangePointer(&steamCaller, _ReturnAddress());
        return steamGamepads(self, value);
    }
    struct Report
    {
        char* buffer;
        size_t length = 0;
        void Write(const char* format, ...)
        {
            if (length >= 8191) return;
            va_list args;
            va_start(args, format);
            int count = vsnprintf_s(buffer + length, 8192 - length, _TRUNCATE, format, args);
            va_end(args);
            if (count > 0) length += count;
        }

        void Method(const char* label, void* object, size_t slot)
        {
            if (!object) return;
            void* address = (*reinterpret_cast<void***>(object))[slot];
            MEMORY_BASIC_INFORMATION info{};
            wchar_t module[MAX_PATH]{};
            if (VirtualQuery(address, &info, sizeof(info)))
                GetModuleFileNameW(static_cast<HMODULE>(info.AllocationBase), module, MAX_PATH);
            Write("%s slot=%zu address=%p module=%ls\n", label, slot, address, module);
        }
    };

    void Capture(Report& report)
    {
        HSTRING name = nullptr;
        constexpr wchar_t className[] = L"Windows.Gaming.Input.Gamepad";
        HRESULT result = WindowsCreateString(className, ARRAYSIZE(className) - 1, &name);
        if (FAILED(result)) { report.Write("WindowsCreateString=%08lx\n", result); return; }
        ComPtr<IGamepadStatics> factory;
        result = RoGetActivationFactory(name, IID_PPV_ARGS(&factory));
        report.Write("RoGetActivationFactory(IGamepadStatics)=%08lx object=%p\n", result, factory.Get());
        ComPtr<IActivationFactory> activation;
        HRESULT generic = RoGetActivationFactory(name, IID_PPV_ARGS(&activation));
        WindowsDeleteString(name);
        report.Write("RoGetActivationFactory(IActivationFactory)=%08lx object=%p\n", generic, activation.Get());
        if (activation)
        {
            report.Method("activation", activation.Get(), 6);
            ComPtr<IGamepadStatics> queried;
            generic = activation.As(&queried);
            report.Write("activation.QueryInterface(IGamepadStatics)=%08lx object=%p\n", generic, queried.Get());
            report.Method("queried.get_Gamepads", queried.Get(), 10);
            if (queried)
            {
                ComPtr<__FIVectorView_1_Windows__CGaming__CInput__CGamepad> nativePads;
                HRESULT nativeResult = queried->get_Gamepads(&nativePads);
                unsigned int nativeCount = 0;
                if (nativePads) nativePads->get_Size(&nativeCount);
                report.Write("Windows get_Gamepads=%08lx count=%u\n", nativeResult, nativeCount);
            }
        }
        if (!factory) return;
        report.Method("factory.QueryInterface", factory.Get(), 0);
        report.Method("factory.get_Gamepads", factory.Get(), 10);
        ComPtr<__FIVectorView_1_Windows__CGaming__CInput__CGamepad> gamepads;
        result = factory->get_Gamepads(&gamepads);
        report.Write("get_Gamepads=%08lx object=%p\n", result, gamepads.Get());
        if (!gamepads) return;
        unsigned int count = 0;
        result = gamepads->get_Size(&count);
        report.Write("get_Size=%08lx count=%u\n", result, count);
        report.Method("vector.GetAt", gamepads.Get(), 6);
        for (unsigned int index = 0; index < count && index < 4; ++index)
        {
            ComPtr<IGamepad> gamepad;
            result = gamepads->GetAt(index, &gamepad);
            report.Write("GetAt(%u)=%08lx object=%p\n", index, result, gamepad.Get());
            if (!gamepad) continue;
            report.Method("gamepad.GetCurrentReading", gamepad.Get(), 8);
            GamepadReading reading{};
            result = gamepad->GetCurrentReading(&reading);
            report.Write("reading=%08lx timestamp=%llu buttons=%08x LT=%.3f RT=%.3f LX=%.3f LY=%.3f RX=%.3f RY=%.3f\n",
                result, reading.Timestamp, static_cast<unsigned int>(reading.Buttons), reading.LeftTrigger,
                reading.RightTrigger, reading.LeftThumbstickX, reading.LeftThumbstickY,
                reading.RightThumbstickX, reading.RightThumbstickY);
        }
    }
}

// The attended caller owns an 8192-byte result allocation until this thread exits.
extern "C" __declspec(dllexport) DWORD WINAPI CaptureInput(void* output)
{
    if (!output) return ERROR_INVALID_PARAMETER;
    Report report{static_cast<char*>(output)};
    report.buffer[0] = '\0';
    report.Write("trace Windows calls=%ld caller=%p Steam calls=%ld caller=%p\n", windowsCalls, windowsCaller, steamCalls, steamCaller);
    HRESULT initialized = RoInitialize(RO_INIT_MULTITHREADED);
    report.Write("RoInitialize=%08lx pid=%lu\n", initialized, GetCurrentProcessId());
    if (SUCCEEDED(initialized))
    {
        Capture(report);
        RoUninitialize();
    }
    return 0;
}

// Installs pass-through counters only. The DLL must remain loaded until game exit.
extern "C" __declspec(dllexport) DWORD WINAPI StartInputTrace(void* output)
{
    if (!output) return ERROR_INVALID_PARAMETER;
    Report report{static_cast<char*>(output)};
    report.buffer[0] = '\0';
    if (windowsGamepads || steamGamepads) { report.Write("Trace already attempted; no retry.\n"); return ERROR_ALREADY_EXISTS; }
    HRESULT initialized = RoInitialize(RO_INIT_MULTITHREADED);
    if (FAILED(initialized)) return initialized;
    {
        HSTRING name = nullptr;
        constexpr wchar_t className[] = L"Windows.Gaming.Input.Gamepad";
        WindowsCreateString(className, ARRAYSIZE(className) - 1, &name);
        ComPtr<IActivationFactory> activation;
        ComPtr<IGamepadStatics> nativeFactory, steamFactory;
        RoGetActivationFactory(name, IID_PPV_ARGS(&activation));
        if (activation) activation.As(&nativeFactory);
        RoGetActivationFactory(name, IID_PPV_ARGS(&steamFactory));
        WindowsDeleteString(name);
        if (nativeFactory && steamFactory)
        {
            void* nativeMethod = (*reinterpret_cast<void***>(nativeFactory.Get()))[10];
            void* steamMethod = (*reinterpret_cast<void***>(steamFactory.Get()))[10];
            MH_STATUS status = MH_Initialize();
            report.Write("MH_Initialize=%d\n", status);
            if (status == MH_OK && nativeMethod != steamMethod)
            {
                status = MH_CreateHook(nativeMethod, reinterpret_cast<void*>(&TraceWindows), reinterpret_cast<void**>(&windowsGamepads));
                report.Write("Windows CreateHook=%d\n", status);
                if (status == MH_OK) report.Write("Windows EnableHook=%d\n", MH_EnableHook(nativeMethod));
                status = MH_CreateHook(steamMethod, reinterpret_cast<void*>(&TraceSteam), reinterpret_cast<void**>(&steamGamepads));
                report.Write("Steam CreateHook=%d\n", status);
                if (status == MH_OK) report.Write("Steam EnableHook=%d\n", MH_EnableHook(steamMethod));
            }
        }
        else report.Write("Missing native or Steam factory.\n");
    }
    RoUninitialize();
    return 0;
}
