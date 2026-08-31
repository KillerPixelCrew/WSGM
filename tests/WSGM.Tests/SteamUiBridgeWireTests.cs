using System.Text.Json;
using WSGM.Core;

namespace WSGM.Tests;

/// <summary>
/// The wire contract between the injected bootstrap and the bridge host.
/// </summary>
/// <remarks>
/// The bootstrap serialises its envelope in camelCase. The host's source-generated context matched
/// PascalCase with case-insensitivity explicitly disabled, so every property took its default:
/// Version arrived as 0 and the request was refused as a schema version mismatch with an empty
/// patch id. Every native-QAM command had been rejected since the bridge was written, and it stayed
/// invisible because no row rendered to send one until the panel was fixed.
/// <para>
/// The literal below is a real envelope captured from the live Runtime binding on the reference
/// Claw, so this test fails if either side of the contract moves.
/// </para>
/// </remarks>
public sealed class SteamUiBridgeWireTests
{
    /// <summary>The generations the captured envelope was produced under.</summary>
    /// <remarks>
    /// Only the execution-context and document generations take part in authorization; the rest
    /// identify the transport and are irrelevant to the envelope's validity.
    /// </remarks>
    private static SteamUiGenerations Generations() => new(0, 0, 0, 0, 2, 1);

    private const string CapturedEnvelope = """
        {"version":1,"type":"request","patchId":"wsgm.native-qam.frame-limit",
        "command":"setFrameLimit","sequence":6,"actionGeneration":99,
        "contextGeneration":2,"documentGeneration":1,"payload":{"value":60}}
        """;

    [Fact]
    public void TheBootstrapsCamelCaseEnvelopeDecodesIntoEveryField()
    {
        SteamUiBridgeRequest? request = JsonSerializer.Deserialize(
            CapturedEnvelope,
            SteamUiBridgeJsonContext.Default.SteamUiBridgeRequest);

        Assert.NotNull(request);
        Assert.Equal(SteamUiBridgeHost.SchemaVersion, request.Version);
        Assert.Equal("request", request.Type);
        Assert.Equal("wsgm.native-qam.frame-limit", request.PatchId);
        Assert.Equal("setFrameLimit", request.Command);
        Assert.Equal(6, request.Sequence);
        Assert.Equal(99, request.ActionGeneration);
        Assert.Equal(2, request.ContextGeneration);
        Assert.Equal(1, request.DocumentGeneration);
        Assert.Equal(60, request.Payload.GetProperty("value").GetInt32());
    }

    [Fact]
    public void ADecodedEnvelopeIsAuthorizedRatherThanRefusedAsAVersionMismatch()
    {
        SteamUiBridgeRequest request = JsonSerializer.Deserialize(
            CapturedEnvelope,
            SteamUiBridgeJsonContext.Default.SteamUiBridgeRequest)!;
        SteamUiBridgeAuthorizer authorizer = new(Generations());

        SteamUiBridgeAuthorizationResult result = authorizer.Authorize(request);

        Assert.True(result.Accepted, result.Reason);
    }

    [Fact]
    public void EveryNativeQamComponentsCommandIsAuthorized()
    {
        // The bootstrap declares one command per component and gates subscriptions on the same
        // allowlist. AutoTDP was missing from it, so its row threw on every render.
        (string PatchId, string Command)[] declared =
        [
            ("wsgm.native-qam.tdp", "setPrimaryLimit"),
            ("wsgm.native-qam.auto-tdp", "setAutoTdp"),
            ("wsgm.native-qam.frame-limit", "setFrameLimit"),
            ("wsgm.native-qam.controller-target", "setControllerTarget"),
        ];

        long sequence = 0;
        foreach ((string patchId, string command) in declared)
        {
            SteamUiBridgeAuthorizer authorizer = new(Generations());
            SteamUiBridgeRequest request = new(
                SteamUiBridgeHost.SchemaVersion,
                "request",
                patchId,
                command,
                ++sequence,
                1,
                2,
                1,
                JsonDocument.Parse("{}").RootElement);

            SteamUiBridgeAuthorizationResult result = authorizer.Authorize(request);

            Assert.True(result.Accepted, $"{patchId}/{command}: {result.Reason}");
        }
    }
}
