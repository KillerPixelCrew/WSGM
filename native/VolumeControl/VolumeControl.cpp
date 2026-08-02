#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <mmdeviceapi.h>
#include <endpointvolume.h>

// The managed shell is NativeAOT with COM interop intentionally disabled. This
// small, ABI-stable boundary owns the required Core Audio COM calls instead.
extern "C" __declspec(dllexport) HRESULT WINAPI WsgmVolumeCommand(
    int command, int* percentage, int* muted)
{
    if (percentage == nullptr || muted == nullptr)
    {
        return E_POINTER;
    }
    *percentage = 0;
    *muted = 0;

    const HRESULT initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    const bool mustUninitialize = SUCCEEDED(initialized);
    if (FAILED(initialized) && initialized != RPC_E_CHANGED_MODE)
    {
        return initialized;
    }

    IMMDeviceEnumerator* enumerator = nullptr;
    IMMDevice* device = nullptr;
    IAudioEndpointVolume* volume = nullptr;
    HRESULT result = CoCreateInstance(
        __uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
        __uuidof(IMMDeviceEnumerator), reinterpret_cast<void**>(&enumerator));
    if (SUCCEEDED(result))
    {
        result = enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &device);
    }
    if (SUCCEEDED(result))
    {
        result = device->Activate(
            __uuidof(IAudioEndpointVolume), CLSCTX_ALL, nullptr,
            reinterpret_cast<void**>(&volume));
    }
    if (SUCCEEDED(result))
    {
        switch (command)
        {
        case 8: // APPCOMMAND_VOLUME_MUTE
        {
            BOOL muted = FALSE;
            result = volume->GetMute(&muted);
            if (SUCCEEDED(result))
            {
                result = volume->SetMute(!muted, nullptr);
            }
            break;
        }
        case 9: // APPCOMMAND_VOLUME_DOWN
            result = volume->VolumeStepDown(nullptr);
            break;
        case 10: // APPCOMMAND_VOLUME_UP
            result = volume->VolumeStepUp(nullptr);
            break;
        default:
            result = E_INVALIDARG;
            break;
        }
    }
    if (SUCCEEDED(result))
    {
        float scalar = 0;
        BOOL isMuted = FALSE;
        result = volume->GetMasterVolumeLevelScalar(&scalar);
        if (SUCCEEDED(result))
        {
            result = volume->GetMute(&isMuted);
        }
        if (SUCCEEDED(result))
        {
            *percentage = static_cast<int>(scalar * 100.0f + 0.5f);
            *muted = isMuted ? 1 : 0;
        }
    }

    if (volume != nullptr)
    {
        volume->Release();
    }
    if (device != nullptr)
    {
        device->Release();
    }
    if (enumerator != nullptr)
    {
        enumerator->Release();
    }
    if (mustUninitialize)
    {
        CoUninitialize();
    }
    return result;
}
