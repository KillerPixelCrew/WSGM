using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
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
            foreach (var (row, spatial) in AudioPlaybackRows(endpointKey, playback))
            {
                offer.Quick("audioFormat", offer.Add(rows, row, (value, token) => spatial
                    ? audioFormat.SetSpatialForEndpointAsync(endpointId, value.GetString()!, token)
                    : audioFormat.SetFormatForEndpointAsync(endpointId, value.GetString()!, token)));
            }

            Section(audio, "Output format", "advanced-playback", rows);
        }
    }

    /// <summary>The Audio page's playback rows for one endpoint: channels, sample format and spatial sound.</summary>
    /// <param name="endpointKey">The endpoint's key prefix.</param>
    /// <param name="playback">The endpoint's choices.</param>
    /// <returns>Each row, and whether it writes spatial sound rather than the format.</returns>
    /// <remarks>
    ///     The same three choices as the overlay's Audio panel. Format is the sample format of the current
    ///     channel layout, labelled with its rate and bit depth ("48 kHz · 24-bit").
    /// </remarks>
    internal static IReadOnlyList<(SteamSettingsRow Row, bool Spatial)> AudioPlaybackRows(string endpointKey,
        AudioFormatChoices playback)
    {
        List<(SteamSettingsRow, bool)> rows = [];
        Add(endpointKey + "/channels", "Channels", "audioChannels", playback.Channels, playback.CurrentChannels,
            false);
        Add(endpointKey + "/format", "Format", "audioEncoding", playback.Formats, playback.CurrentFormat, false);
        Add(endpointKey + "/spatial", "Spatial sound", "audioSpatial", playback.Spatial, playback.CurrentSpatial,
            true);
        return rows;

        void Add(string key, string label, string icon, IReadOnlyList<ChoiceRowOption> options, string current,
            bool spatial)
        {
            // The endpoint's status line is the same for all three, so only the first row says it.
            if (ChoiceRow(key, label, icon,
                    new ChoiceRowState(true, options, current, rows.Count == 0 ? playback.StatusText : string.Empty)) is
                { } row)
            {
                rows.Add((row, spatial));
            }
        }
    }
}