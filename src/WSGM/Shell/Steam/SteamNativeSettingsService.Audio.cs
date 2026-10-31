using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

internal sealed partial class SteamNativeSettingsService
{
    /// <summary>Builds the Audio page from the playback endpoint that offered its formats.</summary>
    /// <param name="audio">The page's sections, appended in order.</param>
    /// <param name="offer">The read's offered command set.</param>
    /// <param name="endpointRead">The endpoint read; a default value when no format owner exists.</param>
    private async Task AddAudioPageAsync(List<SteamSettingsSection> audio, OfferedRows offer,
        ValueTask<(SteamAudioFormatState State, string? EndpointId)> endpointRead)
    {
        if (audioFormat is null)
        {
            return;
        }

        var endpoint = await endpointRead.ConfigureAwait(false);
        if (endpoint.EndpointId is { } endpointId && endpoint.State is { Available: true } playback)
        {
            var endpointKey =
                "audio/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpointId)));
            List<SteamSettingsRow> rows = [];
            AudioChoice(endpointKey + "/channels", "Playback channel layout", playback.ChannelOptions,
                playback.CurrentChannels,
                (value, token) => audioFormat.SetFormatForEndpointAsync(endpointId, value, token));
            AudioChoice(endpointKey + "/format", "Default audio format", playback.FormatOptions,
                playback.CurrentFormat,
                (value, token) => audioFormat.SetFormatForEndpointAsync(endpointId, value, token));
            AudioChoice(endpointKey + "/spatial", "Spatial sound", playback.SpatialOptions,
                playback.CurrentSpatial,
                (value, token) => audioFormat.SetSpatialForEndpointAsync(endpointId, value, token));
            Section(audio, "Advanced playback", "advanced-playback", rows);

            void AudioChoice(string key, string label, IReadOnlyList<SteamAudioFormatOption> options,
                string current,
                Func<string, CancellationToken, Task<SteamUiCommandResult>> write)
            {
                if (options.Count == 0)
                {
                    return;
                }

                offer.Add(rows, new SteamSettingsRow(key, SteamSettingsRowKind.Choice, label, playback.StatusText,
                        Text: current,
                        Choices:
                        [
                            .. options.Select(option => new SteamSettingsChoice(option.Id, option.Label))
                        ]),
                    (value, token) => write(value.GetString()!, token));
            }
        }
    }
}
