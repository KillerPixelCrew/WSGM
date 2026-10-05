using System.Text.Json;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Core;

/// <summary>
///     Adds Name / Size / Type sort buttons to the header of Big Picture's
///     download queue ("Up Next"), reordering the queue through Steam's own
///     <c>SteamClient.Downloads.SetQueueIndex</c>. The script is the
///     <c>wsgmDownloadSort</c> gate in <c>SteamUiAssets\Source\download-sort.ts</c>.
///     The device-verified findings it rests on — the <c>Focusable</c> requirement,
///     the JSX-runtime injection point, the tight component predicates, the
///     whole-pending-list scope and the unknown-size ranking — are in
///     <c>docs\steam-cef.md</c> §12; re-probe with
///     <c>tools/WsgmLibTest/run-prod-sort.mjs</c> before shipping a change there.
/// </summary>
internal static class SteamDownloadSort
{
    /// <summary>The download sorter's stable patch id.</summary>
    internal const string PatchId = "wsgm.download-sort";

    /// <summary>The gate: installed while the sort switch is on.</summary>
    /// <remarks>
    ///     The queue is rendered into the Big Picture document, but it is rendered BY SharedJSContext:
    ///     the JSX-runtime claim the gate's transform registers on, the module registry and the React
    ///     reconciler that re-renders the queue all live there, and the Big Picture window carries the
    ///     DOM and no webpack global at all.
    /// </remarks>
    internal static ISteamUiPatch Patch { get; } = new SteamGatePatch(
        PatchId,
        "wsgmDownloadSort",
        "download-sort-v1:jsx-runtime+focusable+queue-header",
        "(()=>{try{return JSON.stringify({ok:true,runtime:!!window.webpackChunksteamui});}"
        + "catch(e){return JSON.stringify({ok:false,error:String(e)});}})()",
        root => SteamUiPatchEvaluation.Flag(root, "ok") && SteamUiPatchEvaluation.Flag(root, "runtime"),
        "status.installed&&status.registered",
        "!status.installed&&!status.registered",
        "Download queue sort");

    /// <summary>Declares the sort gate and the one report it sends back.</summary>
    /// <returns>The module.</returns>
    internal static ISteamUiModule Module()
    {
        return new SteamUiModule(
            "download-sort",
            [Patch],
            commands:
            [
                SteamUiModuleBuilder.Command<(int Refused, int Total, string First)>(
                    PatchId,
                    "refused",
                    TryReadRefused,
                    (report, _) => Task.FromResult(LogRefused(report)),
                    "The refused-position report names refused, total and first.")
            ]);
    }

    /// <summary>Reads a finished run's refusal count, the run's length and Steam's first error.</summary>
    internal static bool TryReadRefused(JsonElement payload, out (int Refused, int Total, string First) report)
    {
        report = default;
        if (!SteamUiPayload.HasExactly(payload, 3)
            || !SteamUiPayload.TryReadInt(payload, "total", 1, int.MaxValue, out var total)
            || !SteamUiPayload.TryReadInt(payload, "refused", 1, total, out var refused)
            || !SteamUiPayload.TryReadString(payload, "first", out var first))
        {
            return false;
        }

        report = (refused, total, first);
        return true;
    }

    private static SteamUiCommandResult LogRefused((int Refused, int Total, string First) report)
    {
        Log.Warn($"Download queue sort: Steam refused {report.Refused} of {report.Total} queue positions; "
                 + $"first: {report.First}");
        return new SteamUiCommandResult(true, null);
    }
}
