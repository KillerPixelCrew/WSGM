using System.Text;
using WSGM.Device.Contracts.Packaging;

namespace WSGM.Device.Contracts.Tests;

/// <summary>
/// The executable specification of manifest parsing: what a package may say, and what it may not.
/// </summary>
public class PluginManifestReaderTests
{
    [Fact]
    public void Read_TheClawReferenceManifest_IsValid()
    {
        PluginManifestReadResult result = ReadFixture("claw-8-a2vm.valid.json");

        Assert.True(result.IsValid, Describe(result));
        Assert.Equal("wsgm.device.msi.claw-8-a2vm", result.Manifest!.Id);
        Assert.Single(result.Manifest.Devices);
        Assert.Equal("ms-1t52", result.Manifest.Devices[0].Id);
    }

    [Fact]
    public void Read_AManifestFromANewerSchema_IsRejectedRatherThanPartiallyUnderstood()
    {
        // The fields a newer schema adds are exactly the ones carrying its new rules, so reading
        // such a document with this build's understanding is not a conservative fallback.
        PluginManifestReadResult result = ReadFixture("forward-compatible.json");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == ManifestValidationCode.UnsupportedSchemaVersion);
    }

    [Fact]
    public void Read_AnUnknownMember_IsAnErrorAndNotSilentlyDropped()
    {
        // "grantsElevation" is invented, but a package that believes it is meaningful must not be
        // activated under the assumption that it was understood and denied.
        PluginManifestReadResult result = ReadFixture("unknown-member.json");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == ManifestValidationCode.UnknownMember);
        Assert.Null(result.Manifest);
    }

    [Fact]
    public void Read_TruncatedJson_ReportsMalformedWithoutThrowing()
    {
        PluginManifestReadResult result = ReadFixture("malformed.json");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == ManifestValidationCode.MalformedDocument);
    }

    [Fact]
    public void Read_EmptyInput_IsMalformed()
    {
        PluginManifestReadResult result = PluginManifestReader.Read([]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == ManifestValidationCode.MalformedDocument);
    }

    [Fact]
    public void Read_ADocumentOverTheSizeLimit_IsRejectedBeforeParsing()
    {
        byte[] oversized = new byte[ManifestLimits.MaxDocumentBytes + 1];
        oversized.AsSpan().Fill((byte)' ');

        PluginManifestReadResult result = PluginManifestReader.Read(oversized);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == ManifestValidationCode.DocumentTooLarge);
    }

    [Fact]
    public void Read_MarketingTextUsedAsAHardGate_IsRejected()
    {
        // On the reference unit the marketing string is the Type 1 product while the exact board is
        // the Type 2 baseboard product. Gating on the former matches by text that marketing owns.
        PluginManifestReadResult result = ReadFixture("marketing-name-gate.json");

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count(e =>
            e.Code == ManifestValidationCode.MarketingNameAsHardGate));
    }

    [Fact]
    public void Read_IdentityWithNoHardConstraint_IsRejected()
    {
        // Weighted signals order candidates that already passed a gate; alone they would let a
        // package be selected for hardware it was never written for.
        PluginManifestReadResult result = ReadFixture("score-only-identity.json");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Code == ManifestValidationCode.NoHardIdentityConstraint);
    }

    [Fact]
    public void ToCanonicalUtf8_IsStableAcrossMemberOrderAndWhitespace()
    {
        // Canonicalization exists so an evidence lock can hash a manifest. If a reordered but
        // semantically identical document hashed differently, the lock would report a change that
        // did not happen.
        PluginManifest fromFixture = ReadFixture("claw-8-a2vm.valid.json").Manifest!;

        string reordered = File.ReadAllText(FixturePath("claw-8-a2vm.valid.json"))
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        PluginManifest fromReordered = PluginManifestReader
            .Read(Encoding.UTF8.GetBytes(reordered)).Manifest!;

        Assert.Equal(
            PluginManifestReader.ToCanonicalUtf8(fromFixture),
            PluginManifestReader.ToCanonicalUtf8(fromReordered));
    }

    [Fact]
    public void ToCanonicalUtf8_RoundTripsThroughRead()
    {
        PluginManifest original = ReadFixture("claw-8-a2vm.valid.json").Manifest!;

        byte[] canonical = PluginManifestReader.ToCanonicalUtf8(original);
        PluginManifestReadResult reread = PluginManifestReader.Read(canonical);

        Assert.True(reread.IsValid, Describe(reread));
        Assert.Equal(canonical, PluginManifestReader.ToCanonicalUtf8(reread.Manifest!));
    }

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static PluginManifestReadResult ReadFixture(string name) =>
        PluginManifestReader.Read(File.ReadAllBytes(FixturePath(name)));

    private static string Describe(PluginManifestReadResult result) =>
        string.Join("; ", result.Errors.Select(e => $"{e.Path}: {e.Code} {e.Message}"));
}
