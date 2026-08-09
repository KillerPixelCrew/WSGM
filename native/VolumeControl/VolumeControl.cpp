#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <audiopolicy.h>
#include <endpointvolume.h>
#include <mmdeviceapi.h>
#include <mmsystem.h>
#include <propsys.h>

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <new>

// The managed shell is NativeAOT with COM interop intentionally disabled. This
// small, ABI-stable boundary owns the required Core Audio COM calls instead.

namespace
{
constexpr size_t EndpointIdUnits = 512;
constexpr size_t EndpointNameUnits = 256;
const PROPERTYKEY DeviceFriendlyNameKey =
    {{0xa45c254e, 0xdf1c, 0x4efd, {0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0}}, 14};

struct WsgmAudioEndpoint
{
    wchar_t id[EndpointIdUnits];
    wchar_t name[EndpointNameUnits];
    int isDefault;
};
static_assert(sizeof(WsgmAudioEndpoint) == 1540, "Managed endpoint ABI must match");

class ComApartment final
{
public:
    ComApartment()
        : result_(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED)),
          mustUninitialize_(SUCCEEDED(result_))
    {
        if (result_ == RPC_E_CHANGED_MODE)
        {
            result_ = S_OK;
        }
    }

    ~ComApartment()
    {
        if (mustUninitialize_)
        {
            CoUninitialize();
        }
    }

    HRESULT Result() const { return result_; }

private:
    HRESULT result_;
    bool mustUninitialize_;
};

template <typename T>
void Release(T*& value)
{
    if (value != nullptr)
    {
        value->Release();
        value = nullptr;
    }
}

HRESULT OpenEnumerator(IMMDeviceEnumerator** enumerator)
{
    return CoCreateInstance(
        __uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
        __uuidof(IMMDeviceEnumerator), reinterpret_cast<void**>(enumerator));
}

HRESULT OpenDefaultRenderVolume(
    IMMDeviceEnumerator** enumerator, IMMDevice** device, IAudioEndpointVolume** volume)
{
    HRESULT result = OpenEnumerator(enumerator);
    if (SUCCEEDED(result))
    {
        result = (*enumerator)->GetDefaultAudioEndpoint(eRender, eConsole, device);
    }
    if (SUCCEEDED(result))
    {
        result = (*device)->Activate(
            __uuidof(IAudioEndpointVolume), CLSCTX_ALL, nullptr,
            reinterpret_cast<void**>(volume));
    }
    return result;
}

HRESULT ReadVolume(IAudioEndpointVolume* volume, int* percentage, int* muted)
{
    float scalar = 0;
    BOOL isMuted = FALSE;
    HRESULT result = volume->GetMasterVolumeLevelScalar(&scalar);
    if (SUCCEEDED(result))
    {
        result = volume->GetMute(&isMuted);
    }
    if (SUCCEEDED(result))
    {
        *percentage = static_cast<int>(scalar * 100.0f + 0.5f);
        *muted = isMuted ? 1 : 0;
    }
    return result;
}

// IPolicyConfig is the shell-facing Core Audio policy interface used by the
// Windows sound UI to change the per-user default endpoint. It is intentionally
// kept inside this native helper: no COM ABI crosses into the AOT process.
struct __declspec(uuid("f8679f50-850a-41cf-9c72-430f290290c8")) IPolicyConfig : IUnknown
{
    virtual HRESULT STDMETHODCALLTYPE GetMixFormat(PCWSTR, WAVEFORMATEX**) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetDeviceFormat(PCWSTR, INT, WAVEFORMATEX**) = 0;
    virtual HRESULT STDMETHODCALLTYPE ResetDeviceFormat(PCWSTR) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetDeviceFormat(PCWSTR, WAVEFORMATEX*, WAVEFORMATEX*) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetProcessingPeriod(PCWSTR, INT, PINT64, PINT64) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetProcessingPeriod(PCWSTR, PINT64) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetShareMode(PCWSTR, void*) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetShareMode(PCWSTR, void*) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetPropertyValue(PCWSTR, const PROPERTYKEY&, PROPVARIANT*) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetPropertyValue(PCWSTR, const PROPERTYKEY&, PROPVARIANT*) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetDefaultEndpoint(PCWSTR, ERole) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetEndpointVisibility(PCWSTR, INT) = 0;
};

const CLSID CLSID_PolicyConfigClient =
    {0x870af99c, 0x171d, 0x4f9e, {0xaf, 0x0d, 0xe6, 0x3d, 0xf4, 0x0c, 0x2b, 0xc9}};

constexpr DWORD FeedbackSampleRate = 44100;
constexpr DWORD FeedbackDurationMs = 80;
constexpr size_t FeedbackSampleCount = FeedbackSampleRate * FeedbackDurationMs / 1000;
SRWLOCK FeedbackLock = SRWLOCK_INIT;
HWAVEOUT FeedbackOutput = nullptr;
WAVEHDR FeedbackHeader{};
int16_t FeedbackPcm[FeedbackSampleCount]{};
bool FeedbackPcmBuilt = false;
bool FeedbackHeaderPrepared = false;

void BuildFeedbackPcmLocked()
{
    if (FeedbackPcmBuilt)
    {
        return;
    }
    constexpr double Pi = 3.14159265358979323846;
    double phase = 0;
    for (size_t index = 0; index < FeedbackSampleCount; ++index)
    {
        const double t = static_cast<double>(index) / FeedbackSampleRate;
        const double progress = static_cast<double>(index) / FeedbackSampleCount;
        // A short, rounded two-tone drop: deliberately a soft "blob", not an
        // alert beep. Fade both ends so repeated slider ticks never click.
        const double frequency = 520.0 - 190.0 * progress;
        phase += 2.0 * Pi * frequency / FeedbackSampleRate;
        const double attack = std::min(1.0, t / 0.012);
        const double release = std::min(1.0, (1.0 - progress) / 0.35);
        const double envelope = attack * release * release;
        const double tone = std::sin(phase) + 0.28 * std::sin(phase * 0.5);
        FeedbackPcm[index] = static_cast<int16_t>(tone * envelope * 5200.0);
    }
    FeedbackPcmBuilt = true;
}

HRESULT EnsureFeedbackOutputLocked()
{
    if (FeedbackOutput != nullptr)
    {
        return S_OK;
    }
    BuildFeedbackPcmLocked();
    WAVEFORMATEX format{};
    format.wFormatTag = WAVE_FORMAT_PCM;
    format.nChannels = 1;
    format.nSamplesPerSec = FeedbackSampleRate;
    format.wBitsPerSample = 16;
    format.nBlockAlign = format.nChannels * format.wBitsPerSample / 8;
    format.nAvgBytesPerSec = format.nSamplesPerSec * format.nBlockAlign;

    MMRESULT audioResult = waveOutOpen(
        &FeedbackOutput, WAVE_MAPPER, &format, 0, 0, CALLBACK_NULL);
    if (audioResult != MMSYSERR_NOERROR)
    {
        FeedbackOutput = nullptr;
        return HRESULT_FROM_WIN32(audioResult);
    }

    FeedbackHeader = {};
    FeedbackHeader.lpData = reinterpret_cast<LPSTR>(FeedbackPcm);
    FeedbackHeader.dwBufferLength = sizeof(FeedbackPcm);
    audioResult = waveOutPrepareHeader(
        FeedbackOutput, &FeedbackHeader, sizeof(FeedbackHeader));
    if (audioResult != MMSYSERR_NOERROR)
    {
        waveOutClose(FeedbackOutput);
        FeedbackOutput = nullptr;
        return HRESULT_FROM_WIN32(audioResult);
    }
    FeedbackHeaderPrepared = true;
    return S_OK;
}

void CloseFeedbackOutputLocked()
{
    if (FeedbackOutput == nullptr)
    {
        return;
    }
    waveOutReset(FeedbackOutput);
    if (FeedbackHeaderPrepared)
    {
        waveOutUnprepareHeader(
            FeedbackOutput, &FeedbackHeader, sizeof(FeedbackHeader));
        FeedbackHeaderPrepared = false;
    }
    waveOutClose(FeedbackOutput);
    FeedbackOutput = nullptr;
    FeedbackHeader = {};
}
}

extern "C" __declspec(dllexport) HRESULT WINAPI WsgmVolumeCommand(
    int command, int* percentage, int* muted)
{
    if (percentage == nullptr || muted == nullptr)
    {
        return E_POINTER;
    }
    *percentage = 0;
    *muted = 0;

    ComApartment apartment;
    if (FAILED(apartment.Result()))
    {
        return apartment.Result();
    }

    IMMDeviceEnumerator* enumerator = nullptr;
    IMMDevice* device = nullptr;
    IAudioEndpointVolume* volume = nullptr;
    HRESULT result = OpenDefaultRenderVolume(&enumerator, &device, &volume);
    if (SUCCEEDED(result))
    {
        switch (command)
        {
        case 8: // APPCOMMAND_VOLUME_MUTE
        {
            BOOL isMuted = FALSE;
            result = volume->GetMute(&isMuted);
            if (SUCCEEDED(result))
            {
                result = volume->SetMute(!isMuted, nullptr);
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
        result = ReadVolume(volume, percentage, muted);
    }

    Release(volume);
    Release(device);
    Release(enumerator);
    return result;
}

extern "C" __declspec(dllexport) HRESULT WINAPI WsgmVolumeGet(
    int* percentage, int* muted)
{
    if (percentage == nullptr || muted == nullptr)
    {
        return E_POINTER;
    }
    *percentage = 0;
    *muted = 0;

    ComApartment apartment;
    if (FAILED(apartment.Result()))
    {
        return apartment.Result();
    }

    IMMDeviceEnumerator* enumerator = nullptr;
    IMMDevice* device = nullptr;
    IAudioEndpointVolume* volume = nullptr;
    HRESULT result = OpenDefaultRenderVolume(&enumerator, &device, &volume);
    if (SUCCEEDED(result))
    {
        result = ReadVolume(volume, percentage, muted);
    }
    Release(volume);
    Release(device);
    Release(enumerator);
    return result;
}

extern "C" __declspec(dllexport) HRESULT WINAPI WsgmVolumeSet(
    int percentage, int* muted)
{
    if (muted == nullptr)
    {
        return E_POINTER;
    }
    *muted = 0;
    percentage = std::clamp(percentage, 0, 100);

    ComApartment apartment;
    if (FAILED(apartment.Result()))
    {
        return apartment.Result();
    }

    IMMDeviceEnumerator* enumerator = nullptr;
    IMMDevice* device = nullptr;
    IAudioEndpointVolume* volume = nullptr;
    HRESULT result = OpenDefaultRenderVolume(&enumerator, &device, &volume);
    if (SUCCEEDED(result))
    {
        result = volume->SetMasterVolumeLevelScalar(percentage / 100.0f, nullptr);
    }
    if (SUCCEEDED(result) && percentage > 0)
    {
        result = volume->SetMute(FALSE, nullptr);
    }
    if (SUCCEEDED(result))
    {
        BOOL isMuted = FALSE;
        result = volume->GetMute(&isMuted);
        *muted = isMuted ? 1 : 0;
    }

    Release(volume);
    Release(device);
    Release(enumerator);
    return result;
}

extern "C" __declspec(dllexport) HRESULT WINAPI WsgmAudioListEndpoints(
    int flow, WsgmAudioEndpoint** items, uint32_t* count)
{
    if (items == nullptr || count == nullptr || (flow != 0 && flow != 1))
    {
        return E_INVALIDARG;
    }
    *items = nullptr;
    *count = 0;

    ComApartment apartment;
    if (FAILED(apartment.Result()))
    {
        return apartment.Result();
    }

    const EDataFlow dataFlow = flow == 0 ? eRender : eCapture;
    IMMDeviceEnumerator* enumerator = nullptr;
    IMMDeviceCollection* collection = nullptr;
    IMMDevice* defaultDevice = nullptr;
    LPWSTR defaultId = nullptr;
    HRESULT result = OpenEnumerator(&enumerator);
    if (SUCCEEDED(result))
    {
        result = enumerator->EnumAudioEndpoints(dataFlow, DEVICE_STATE_ACTIVE, &collection);
    }
    if (SUCCEEDED(result)
        && SUCCEEDED(enumerator->GetDefaultAudioEndpoint(dataFlow, eConsole, &defaultDevice)))
    {
        defaultDevice->GetId(&defaultId);
    }

    UINT deviceCount = 0;
    if (SUCCEEDED(result))
    {
        result = collection->GetCount(&deviceCount);
    }
    WsgmAudioEndpoint* records = nullptr;
    if (SUCCEEDED(result) && deviceCount > 0)
    {
        records = static_cast<WsgmAudioEndpoint*>(
            CoTaskMemAlloc(sizeof(WsgmAudioEndpoint) * deviceCount));
        if (records == nullptr)
        {
            result = E_OUTOFMEMORY;
        }
        else
        {
            ZeroMemory(records, sizeof(WsgmAudioEndpoint) * deviceCount);
        }
    }

    UINT written = 0;
    for (UINT index = 0; SUCCEEDED(result) && index < deviceCount; ++index)
    {
        IMMDevice* device = nullptr;
        IPropertyStore* properties = nullptr;
        LPWSTR id = nullptr;
        PROPVARIANT name;
        PropVariantInit(&name);

        HRESULT itemResult = collection->Item(index, &device);
        if (SUCCEEDED(itemResult))
        {
            itemResult = device->GetId(&id);
        }
        if (SUCCEEDED(itemResult))
        {
            auto& record = records[written];
            wcsncpy_s(record.id, id, _TRUNCATE);
            record.isDefault =
                defaultId != nullptr && wcscmp(defaultId, id) == 0 ? 1 : 0;

            itemResult = device->OpenPropertyStore(STGM_READ, &properties);
            if (SUCCEEDED(itemResult))
            {
                itemResult = properties->GetValue(DeviceFriendlyNameKey, &name);
            }
            if (SUCCEEDED(itemResult) && name.vt == VT_LPWSTR && name.pwszVal != nullptr)
            {
                wcsncpy_s(record.name, name.pwszVal, _TRUNCATE);
            }
            if (record.name[0] == L'\0')
            {
                wcsncpy_s(record.name, L"Audio device", _TRUNCATE);
            }
            ++written;
        }

        PropVariantClear(&name);
        CoTaskMemFree(id);
        Release(properties);
        Release(device);
    }

    if (SUCCEEDED(result))
    {
        *items = records;
        *count = written;
    }
    else
    {
        CoTaskMemFree(records);
    }
    CoTaskMemFree(defaultId);
    Release(defaultDevice);
    Release(collection);
    Release(enumerator);
    return result;
}

extern "C" __declspec(dllexport) void WINAPI WsgmAudioFreeEndpoints(
    WsgmAudioEndpoint* items)
{
    CoTaskMemFree(items);
}

extern "C" __declspec(dllexport) HRESULT WINAPI WsgmAudioSetDefaultEndpoint(
    const wchar_t* endpointId)
{
    if (endpointId == nullptr || endpointId[0] == L'\0')
    {
        return E_INVALIDARG;
    }

    ComApartment apartment;
    if (FAILED(apartment.Result()))
    {
        return apartment.Result();
    }

    IPolicyConfig* policy = nullptr;
    HRESULT result = CoCreateInstance(
        CLSID_PolicyConfigClient, nullptr, CLSCTX_ALL,
        __uuidof(IPolicyConfig), reinterpret_cast<void**>(&policy));
    if (SUCCEEDED(result))
    {
        result = policy->SetDefaultEndpoint(endpointId, eConsole);
    }
    if (SUCCEEDED(result))
    {
        result = policy->SetDefaultEndpoint(endpointId, eMultimedia);
    }
    if (SUCCEEDED(result))
    {
        result = policy->SetDefaultEndpoint(endpointId, eCommunications);
    }
    Release(policy);
    if (SUCCEEDED(result))
    {
        // A mapped waveOut stream stays on the endpoint it opened against.
        // Recreate the prewarmed stream now so the first cue after an output
        // switch is both low-latency and routed to the newly selected device.
        AcquireSRWLockExclusive(&FeedbackLock);
        CloseFeedbackOutputLocked();
        EnsureFeedbackOutputLocked();
        ReleaseSRWLockExclusive(&FeedbackLock);
    }
    return result;
}

extern "C" __declspec(dllexport) HRESULT WINAPI WsgmInitializeVolumeFeedback()
{
    AcquireSRWLockExclusive(&FeedbackLock);
    // WAVE_MAPPER resolves the default endpoint only when the stream opens.
    // Reinitialization is also used after endpoint enumeration detects that
    // Windows changed the default outside WSGM, so an existing stream must not
    // be retained here.
    CloseFeedbackOutputLocked();
    const HRESULT result = EnsureFeedbackOutputLocked();
    ReleaseSRWLockExclusive(&FeedbackLock);
    return result;
}

extern "C" __declspec(dllexport) HRESULT WINAPI WsgmPlayVolumeFeedback()
{
    // Never block a volume input behind the comparatively slow device-open
    // path. Managed code prewarms this stream in the background; a cue arriving
    // during that warmup is simply dropped and the volume command stays instant.
    if (!TryAcquireSRWLockExclusive(&FeedbackLock))
    {
        return S_FALSE;
    }
    HRESULT result = S_FALSE;
    if (FeedbackOutput != nullptr
        && (FeedbackHeader.dwFlags & WHDR_INQUEUE) == 0)
    {
        const MMRESULT audioResult = waveOutWrite(
            FeedbackOutput, &FeedbackHeader, sizeof(FeedbackHeader));
        result = audioResult == MMSYSERR_NOERROR
            ? S_OK
            : HRESULT_FROM_WIN32(audioResult);
        if (audioResult != MMSYSERR_NOERROR)
        {
            CloseFeedbackOutputLocked();
        }
    }
    ReleaseSRWLockExclusive(&FeedbackLock);
    return result;
}
