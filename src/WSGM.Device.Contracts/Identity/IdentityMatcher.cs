using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WSGM.Device.Contracts.Packaging;

namespace WSGM.Device.Contracts.Identity;

/// <summary>Why a device definition did or did not match a machine.</summary>
public enum IdentityMatchOutcome
{
    /// <summary>A hard constraint failed. The definition is out, regardless of any score.</summary>
    Rejected,

    /// <summary>Every hard constraint passed.</summary>
    Matched,
}

/// <summary>
/// One evaluated observation, kept so every acceptance and rejection can be explained.
/// </summary>
/// <param name="Signal">The signal that was evaluated.</param>
/// <param name="Strength">How the definition bound to it.</param>
/// <param name="Satisfied">Whether the observation was satisfied.</param>
/// <param name="ScoreContribution">Points this observation added, always zero for hard constraints.</param>
/// <param name="Explanation">Human-readable reason, shown in Device Lab and diagnostics.</param>
public sealed record IdentityMatchExplanation(
    IdentitySignal Signal,
    IdentityStrength Strength,
    bool Satisfied,
    int ScoreContribution,
    string Explanation);

/// <summary>The result of matching one device definition against one machine.</summary>
/// <param name="Outcome">Whether every hard constraint passed.</param>
/// <param name="Score">Summed weight of satisfied weighted observations. Ordering only.</param>
/// <param name="Explanations">Every observation evaluated, in declaration order.</param>
public sealed record IdentityMatchResult(
    IdentityMatchOutcome Outcome,
    int Score,
    IReadOnlyList<IdentityMatchExplanation> Explanations)
{
    /// <summary>Explanations for hard constraints that failed. Empty when nothing was rejected.</summary>
    public IEnumerable<IdentityMatchExplanation> Rejections =>
        Explanations.Where(e => !e.Satisfied
            && e.Strength is IdentityStrength.Required or IdentityStrength.Excluded);
}

/// <summary>
/// Evaluates a device definition's identity observations against an observed machine.
/// </summary>
/// <remarks>
/// This lives in the contract rather than in Device Lab or the host because both must agree on what
/// selection means: Device Lab explains candidates to a developer, and the runtime picks the package
/// that will own the user's hardware. Two implementations of "matched" would eventually disagree, and
/// the disagreement would surface as a plugin activating on the wrong device.
/// <para>
/// Hard constraints decide the outcome on their own. A weighted observation can never rescue a failed
/// <see cref="IdentityStrength.Required"/> or a satisfied <see cref="IdentityStrength.Excluded"/> one:
/// similarity nominates a candidate, it never selects one.
/// </para>
/// </remarks>
public static class IdentityMatcher
{
    /// <summary>
    /// Matches one device definition against one machine snapshot.
    /// </summary>
    /// <param name="device">The definition to evaluate.</param>
    /// <param name="snapshot">Normalized facts observed on the machine.</param>
    /// <returns>The outcome, the ranking score, and an explanation per observation.</returns>
    public static IdentityMatchResult Match(DeviceDefinition device, DeviceIdentitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(snapshot);

        List<IdentityMatchExplanation> explanations = new(device.Identity.Count);
        bool rejected = false;
        int score = 0;

        foreach (IdentityObservation observation in device.Identity)
        {
            bool matched = observation.Values
                .Any(expected => SignalMatches(observation, expected, snapshot));

            switch (observation.Strength)
            {
                case IdentityStrength.Required:
                    rejected |= !matched;
                    explanations.Add(Explain(observation, matched, 0, matched
                        ? "matched a required value."
                        : $"did not match any of [{string.Join(", ", observation.Values)}]."));
                    break;

                case IdentityStrength.Excluded:
                    rejected |= matched;
                    explanations.Add(Explain(observation, !matched, 0, matched
                        ? "matched an excluded value."
                        : "matched no excluded value."));
                    break;

                case IdentityStrength.Weighted:
                    int contribution = matched ? observation.Weight : 0;
                    score += contribution;
                    explanations.Add(Explain(observation, matched, contribution, matched
                        ? $"matched, adding {contribution} to the rank."
                        : "did not match; no rank contribution."));
                    break;

                default:
                    explanations.Add(Explain(observation, matched, 0,
                        "is informational and does not affect matching."));
                    break;
            }
        }

        // A rejected definition keeps its score in the result so a developer can see "this was close,
        // and a hard constraint killed it". Consumers must branch on Outcome, never on Score.
        return new IdentityMatchResult(
            rejected ? IdentityMatchOutcome.Rejected : IdentityMatchOutcome.Matched,
            score,
            explanations);
    }

    private static IdentityMatchExplanation Explain(
        IdentityObservation observation,
        bool satisfied,
        int contribution,
        string detail) =>
        new(observation.Signal, observation.Strength, satisfied, contribution,
            $"{observation.Signal} {detail}");

    private static bool SignalMatches(
        IdentityObservation observation,
        string expected,
        DeviceIdentitySnapshot snapshot) => observation.Signal switch
        {
            IdentitySignal.SmbiosSystemManufacturer =>
                IdentityText.Matches(snapshot.SystemManufacturer, expected),
            IdentitySignal.SmbiosSystemProduct => IdentityText.Matches(snapshot.SystemProduct, expected),
            IdentitySignal.SmbiosSystemSku => IdentityText.Matches(snapshot.SystemSku, expected),
            IdentitySignal.SmbiosSystemFamily => IdentityText.Matches(snapshot.SystemFamily, expected),
            IdentitySignal.SmbiosBaseboardProduct =>
                IdentityText.Matches(snapshot.BaseboardProduct, expected),
            IdentitySignal.SmbiosBaseboardVersion =>
                IdentityText.Matches(snapshot.BaseboardVersion, expected),
            IdentitySignal.BiosVersion => IdentityText.Matches(snapshot.BiosVersion, expected),
            IdentitySignal.EcFirmwareVersion => IdentityText.Matches(snapshot.EcFirmwareVersion, expected),
            IdentitySignal.McuFirmwareVersion => IdentityText.Matches(snapshot.McuFirmwareVersion, expected),
            IdentitySignal.CpuIdentity => IdentityText.Matches(snapshot.CpuIdentity, expected),
            IdentitySignal.WmiProviderSignature =>
                snapshot.WmiProviderSignatures.Any(s => IdentityText.Matches(s, expected)),
            _ => EndpointSignalMatches(observation, expected, snapshot),
        };

    private static bool EndpointSignalMatches(
        IdentityObservation observation,
        string expected,
        DeviceIdentitySnapshot snapshot) =>
        snapshot.UsbEndpoints.Any(endpoint => observation.Signal switch
        {
            IdentitySignal.UsbVendorId => IdentityText.Matches(endpoint.VendorId, expected),
            IdentitySignal.UsbProductId => IdentityText.Matches(endpoint.ProductId, expected),
            IdentitySignal.UsbDeviceRelease => IdentityText.Matches(endpoint.DeviceRelease, expected),
            IdentitySignal.HidReportDescriptorHash =>
                IdentityText.Matches(endpoint.ReportDescriptorHash, expected),
            IdentitySignal.UsbInterfaceNumber =>
                int.TryParse(expected, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                    && endpoint.InterfaceNumber == number,
            IdentitySignal.HidReportLength =>
                int.TryParse(expected, NumberStyles.None, CultureInfo.InvariantCulture, out int length)
                    && endpoint.ReportLengths.Contains(length),
            _ => false,
        });
}
