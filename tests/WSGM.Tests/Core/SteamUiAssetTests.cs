using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class SteamUiAssetTests
{
    [Fact]
    public void NativeQamBootstrapIsHashLockedAndHasNoBroadRuntimeAuthority()
    {
        var source = SteamUiAssetCatalog.LoadNativeQamBootstrap();

        Assert.Contains("__STEAM_UI_CONFIGURATION_JSON__", source, StringComparison.Ordinal);
        Assert.DoesNotContain("eval(", source, StringComparison.Ordinal);
        // The sound gate decodes bounded audio data URLs before allowing playback. Permit that
        // one read inside its owner; another fetch anywhere in the asset still fails this check.
        var soundStart = source.IndexOf("function createSoundOverrides()", StringComparison.Ordinal);
        Assert.InRange(soundStart, 0, source.Length - 1);
        var soundEnd = source.IndexOf("registerGate(\"soundOverrides\", createSoundOverrides());", soundStart,
            StringComparison.Ordinal);
        Assert.InRange(soundEnd, soundStart + 1, source.Length - 1);
        var soundGate = source[soundStart..soundEnd];
        Assert.Contains("""!/^data:audio\/[a-z0-9.+-]+;base64,[A-Za-z0-9+/=]+$/u.test(url)""", soundGate,
            StringComparison.Ordinal);
        Assert.Contains("url.length > 1400000", soundGate, StringComparison.Ordinal);
        Assert.Contains("total > 24000000", soundGate, StringComparison.Ordinal);
        Assert.Contains("await context.decodeAudioData(bytes)", soundGate, StringComparison.Ordinal);
        const string approvedDataRead = "fetch(url)";
        var dataRead = soundGate.IndexOf(approvedDataRead, StringComparison.Ordinal);
        Assert.InRange(dataRead, 0, soundGate.Length - 1);
        var withoutSoundDataRead = source.Remove(soundStart + dataRead, approvedDataRead.Length);
        Assert.DoesNotContain("fetch(", withoutSoundDataRead, StringComparison.Ordinal);
        Assert.DoesNotContain("WebSocket", source, StringComparison.Ordinal);
        Assert.DoesNotContain("performanceProfile", source, StringComparison.Ordinal);

        // The filesystem check is about reaching a filesystem, not about the word. Steam's own
        // block-device message declares a filesystem_type enum, and the storage gate publishes it
        // because a field the client declares and this side omits is a field the client reads as
        // undefined. That one token is removed before the check so every other use still fails.
        var withoutDeclaredFields = source.Replace(
            "filesystem_type", "", StringComparison.Ordinal);
        Assert.DoesNotContain("filesystem", withoutDeclaredFields, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeQamComponentsUseValveFieldsWithoutPlatformOrDeviceSpoofing()
    {
        var source = SteamUiAssetCatalog.LoadNativeQamBootstrap();

        Assert.Contains("DialogSlider_Container", source, StringComparison.Ordinal);
        Assert.Contains("DropDownField", source, StringComparison.Ordinal);
        Assert.Contains("PanelSectionRow", source, StringComparison.Ordinal);
        Assert.Contains("LocalizeString", source, StringComparison.Ordinal);
        Assert.Contains("steam-ui.power-limit", source, StringComparison.Ordinal);
        Assert.Contains("steam-ui.frame-limit", source, StringComparison.Ordinal);
        Assert.Contains("steam-ui.controller-target", source, StringComparison.Ordinal);
        Assert.Contains("steam-ui.device-controls", source, StringComparison.Ordinal);
        Assert.Contains("setPrimaryLimit", source, StringComparison.Ordinal);
        Assert.Contains("setFrameLimit", source, StringComparison.Ordinal);
        Assert.Contains("setControllerTarget", source, StringComparison.Ordinal);
        Assert.Contains("setChargeLimit", source, StringComparison.Ordinal);
        Assert.Contains("setLightingBrightness", source, StringComparison.Ordinal);
        Assert.Contains("setLightingColor", source, StringComparison.Ordinal);
        Assert.Contains("onChangeComplete", source, StringComparison.Ordinal);
        Assert.Contains("persistence: \"automatic\"", source, StringComparison.Ordinal);
        Assert.Contains("latestStates.set(envelope.patchId, envelope.payload)", source,
            StringComparison.Ordinal);
        Assert.Contains("callback(latestStates.get(patchId))", source, StringComparison.Ordinal);
        Assert.DoesNotContain("force_deck_perf_tab", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IS_STEAMOS =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PLATFORM =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamClient.SteamOSManager", source, StringComparison.Ordinal);
    }
}
