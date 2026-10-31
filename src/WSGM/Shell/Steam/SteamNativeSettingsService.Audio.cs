using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

internal sealed partial class SteamNativeSettingsService
{
    /// <summary>Builds the playback format rows from the endpoint that offered them, for both surfaces.</summary>
    /// <param name="audio">The Settings page's sections, appended in order.</param>
    /// <param name="offer">The read's offered command set and Quick Access sections.</param>
    /// <param name="endpointRead">The endpoint read; a default value when no format owner exists.</param>
    /// <remarks>
    ///     Keyed by the endpoint, so a choice made against one output can never be applied to the output that is
    ///     default by the time it arrives.
    /// </remarks>
    private async Task AddAudioPageAsync(List<SteamSettingsSection> audio, OfferedRows offer,
        ValueTask<(AudioFormatChoices State, string? EndpointId)> endpointRead)
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
            AudioChoice(endpointKey + "/channels", "Channels", "audioChannels", playback.Channels,
                playback.CurrentChannels,
                (value, token) => audioFormat.SetFormatForEndpointAsync(endpointId, value, token));
            AudioChoice(endpointKey + "/format", "Format", "audioEncoding", playback.Formats,
                playback.CurrentFormat,
                (value, token) => audioFormat.SetFormatForEndpointAsync(endpointId, value, token));
            AudioChoice(endpointKey + "/spatial", "Spatial sound", "audioSpatial", playback.Spatial,
                playback.CurrentSpatial,
                (value, token) => audioFormat.SetSpatialForEndpointAsync(endpointId, value, token));
            Section(audio, "Advanced playback", "advanced-playback", rows);

            void AudioChoice(string key, string label, string icon, IReadOnlyList<ChoiceRowOption> options,
                string current, Func<string, CancellationToken, Task<SteamUiCommandResult>> write)
            {
                if (ChoiceRow(key, label, icon, new ChoiceRowState(true, options, current, playback.StatusText)) is
                    { } row)
                {
                    offer.Quick("audioFormat", offer.Add(rows, row, (value, token) => write(value.GetString()!, token)));
                }
            }
        }
    }
}
