using System.Collections.Generic;
using WindowsDeviceControl;

namespace WSGM.Shell;

/// <summary>One read of both directions' master volume and, when asked for, both endpoint lists.</summary>
/// <param name="VolumeResult">HRESULT of the render volume read.</param>
/// <param name="Volume">Render volume, 0 to 100, when the read succeeded.</param>
/// <param name="Muted">Render mute state, when the read succeeded.</param>
/// <param name="VolumeRevision">The render volume revision the read started under.</param>
/// <param name="InputVolumeResult">HRESULT of the capture volume read.</param>
/// <param name="InputVolume">Capture volume, 0 to 100, when the read succeeded.</param>
/// <param name="InputMuted">Capture mute state, when the read succeeded.</param>
/// <param name="InputVolumeRevision">The capture volume revision the read started under.</param>
/// <param name="IncludedEndpoints">Whether the endpoint lists were read.</param>
/// <param name="OutputResult">HRESULT of the playback endpoint listing.</param>
/// <param name="Outputs">Active playback endpoints.</param>
/// <param name="InputResult">HRESULT of the recording endpoint listing.</param>
/// <param name="Inputs">Active recording endpoints.</param>
internal sealed record AudioSnapshot(
    int VolumeResult,
    int Volume,
    bool Muted,
    int VolumeRevision,
    int InputVolumeResult,
    int InputVolume,
    bool InputMuted,
    int InputVolumeRevision,
    bool IncludedEndpoints,
    int OutputResult,
    IReadOnlyList<CoreAudio.AudioEndpoint> Outputs,
    int InputResult,
    IReadOnlyList<CoreAudio.AudioEndpoint> Inputs)
{
    /// <summary>Reads Core Audio's current state. Blocking; call it on a worker.</summary>
    /// <param name="includeEndpoints">Whether to list both directions' endpoints as well.</param>
    /// <param name="volumeRevision">The render volume revision current when the read was queued.</param>
    /// <param name="inputVolumeRevision">The capture volume revision current when the read was queued.</param>
    /// <returns>The snapshot; failures are carried as HRESULTs rather than thrown.</returns>
    internal static AudioSnapshot Read(
        bool includeEndpoints,
        int volumeRevision,
        int inputVolumeRevision)
    {
        var volumeResult = CoreAudio.GetVolume(out var volume, out var muted);
        var inputVolumeResult = CoreAudio.GetVolume(
            CoreAudio.AudioDirection.Capture,
            out var inputVolume,
            out var inputMuted);
        if (!includeEndpoints)
        {
            return new AudioSnapshot(
                volumeResult,
                volume,
                muted != 0,
                volumeRevision,
                inputVolumeResult,
                inputVolume,
                inputMuted != 0,
                inputVolumeRevision,
                IncludedEndpoints: false,
                OutputResult: 0,
                Outputs: [],
                InputResult: 0,
                Inputs: []);
        }

        var outputResult = CoreAudio.ListEndpoints(CoreAudio.AudioDirection.Render, out var outputs);
        var inputResult = CoreAudio.ListEndpoints(CoreAudio.AudioDirection.Capture, out var inputs);
        return new AudioSnapshot(
            volumeResult,
            volume,
            muted != 0,
            volumeRevision,
            inputVolumeResult,
            inputVolume,
            inputMuted != 0,
            inputVolumeRevision,
            IncludedEndpoints: true,
            outputResult,
            outputs,
            inputResult,
            inputs);
    }
}
