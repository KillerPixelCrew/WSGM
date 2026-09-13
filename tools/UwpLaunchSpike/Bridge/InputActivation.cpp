#include <windows.h>
#include <roapi.h>
#include <windows.gaming.input.h>
#include <wrl/client.h>
#include <intrin.h>
#include "MinHook.h"

using Microsoft::WRL::ComPtr;
using namespace ABI::Windows::Gaming::Input;

namespace
{
    using QueryInterface = HRESULT (STDMETHODCALLTYPE*)(IActivationFactory*, REFIID, void**);
    QueryInterface originalQuery = nullptr;
    IActivationFactory* gamepadActivation = nullptr;
    HSTRING gamepadName = nullptr;
    thread_local bool resolving = false;
    volatile LONG routed = 0;

    bool IsGameCaller(void* caller)
    {
        MEMORY_BASIC_INFORMATION info{};
        if (!VirtualQuery(caller, &info, sizeof(info))) return false;
        return info.AllocationBase == GetModuleHandleW(L"GameAssembly.dll") ||
            info.AllocationBase == GetModuleHandleW(L"UnityPlayer.dll") ||
            info.AllocationBase == GetModuleHandleW(L"vccorlib140_app.dll");
    }

    HRESULT STDMETHODCALLTYPE GamepadQuery(IActivationFactory* self, REFIID iid, void** value)
    {
        // C++/WinRT requests IActivationFactory first, then queries the statics. Steam
        // currently wraps direct requests for the statics, but misses this second step.
        // Keep Steam/combase queries on the original factory. Routing those too would
        // make Steam wrap its own emulated controller again and enumerate it twice.
        if (!resolving && self == gamepadActivation && value &&
            (iid == __uuidof(IGamepadStatics) || iid == __uuidof(IGamepadStatics2)) && IsGameCaller(_ReturnAddress()))
        {
            resolving = true;
            HRESULT result = RoGetActivationFactory(gamepadName, iid, value);
            resolving = false;
            if (SUCCEEDED(result)) InterlockedIncrement(&routed);
            return result;
        }
        return originalQuery(self, iid, value);
    }

    bool IsSteamMethod(void* object, size_t slot)
    {
        MEMORY_BASIC_INFORMATION info{};
        void* method = (*reinterpret_cast<void***>(object))[slot];
        return VirtualQuery(method, &info, sizeof(info)) &&
            info.AllocationBase == GetModuleHandleW(L"GameOverlayRenderer64.dll");
    }
}

// Called after renderer injection. Handles and the factory live until game exit.
extern "C" __declspec(dllexport) DWORD WINAPI InitializeInputBridge(void*)
{
    if (originalQuery) return ERROR_ALREADY_INITIALIZED;
    HRESULT initialized = RoInitialize(RO_INIT_MULTITHREADED);
    if (FAILED(initialized)) return initialized;
    DWORD error = ERROR_NOT_READY;
    {
        HSTRING name = nullptr;
        constexpr wchar_t className[] = L"Windows.Gaming.Input.Gamepad";
        HRESULT created = WindowsCreateString(className, ARRAYSIZE(className) - 1, &name);
        if (SUCCEEDED(created))
        {
            ComPtr<IActivationFactory> activation;
            HRESULT activated = RoGetActivationFactory(name, IID_PPV_ARGS(&activation));
            if (SUCCEEDED(activated))
            {
                // Renderer setup may still be finishing on its own thread. Only bridge
                // a factory after proving the direct statics request belongs to Steam.
                for (unsigned int attempt = 0; attempt < 100; ++attempt)
                {
                    ComPtr<IGamepadStatics> statics;
                    HRESULT result = RoGetActivationFactory(name, IID_PPV_ARGS(&statics));
                    if (SUCCEEDED(result) && IsSteamMethod(statics.Get(), 10))
                    {
                        void* target = (*reinterpret_cast<void***>(activation.Get()))[0];
                        MH_STATUS status = MH_CreateHook(target, reinterpret_cast<void*>(&GamepadQuery), reinterpret_cast<void**>(&originalQuery));
                        if (status != MH_OK) { error = ERROR_INVALID_FUNCTION; break; }
                        gamepadActivation = activation.Detach();
                        gamepadName = name;
                        name = nullptr;
                        status = MH_EnableHook(target);
                        error = status == MH_OK ? ERROR_SUCCESS : ERROR_DLL_INIT_FAILED;
                        break;
                    }
                    Sleep(10);
                }
            }
            else error = activated;
            if (name) WindowsDeleteString(name);
        }
        else error = created;
    }
    RoUninitialize();
    return error;
}

extern "C" __declspec(dllexport) DWORD WINAPI InputBridgeRoutes(void*)
{
    return static_cast<DWORD>(InterlockedCompareExchange(&routed, 0, 0));
}
