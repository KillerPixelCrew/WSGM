using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Makes Big Picture's header Wi-Fi indicator work on Windows by feeding
/// Steam's <c>SystemNetworkStore</c> the one thing the Windows backend never sends:
/// a connected wireless access point.
///
/// <para>Verified live against the store's minified source: Steam's Windows client
/// DOES push periodic <c>CMsgNetworkDevicesData</c> reports (real adapters, MACs,
/// IPs, the wireless device even reports estate Connected=5), but always with an
/// empty <c>wireless.aps</c> list — so no access point is ever "connected", the
/// header hook finds no default-route AP, and the icon draws zero bars. This class
/// injects a synthetic AP (WSGM's real SSID + signal from the radio helper) into
/// the store via its own <c>SetDeviceInfo</c> ingestion path — the same plain-object
/// shape the protobuf decoder produces — then recomputes the connected flags.</para>
///
/// <para>Residency: the backend's periodic reports expire unknown map entries via
/// each entry's <c>MarkAsNotPresent()</c>. Replacing the store's report handler is
/// useless (the backend holds the bound callback registered at store init — verified:
/// a property wrap never fires), so instead the synthetic AP instance gets a no-op
/// <c>MarkAsNotPresent</c>, which pins it across reports with no timers and no
/// flicker (device-verified on live Steam). Recovery: removing the map entry plus
/// <c>SteamClient.System.Network.ForceRefresh()</c> restores pure backend truth, as
/// does a Steam restart.</para></summary>
public static class SteamNetworkIndicator
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(8);

    // Synthetic access-point id inside the wireless device's report; any value works
    // as long as it never collides with a real one (Windows never sends any).
    private const int WapId = 990001;

    // Bump BOTH this and the literal netVer values in ResidentSetup when the resident
    // functions change: the setup block is guarded by netVer, so an old session's
    // functions are replaced on the next push after an upgrade (same pattern as the
    // badge script's BadgeScriptVersion).
    private const int ScriptVersion = 1;

    /// <summary>Maps the radio helper's 0-100 signal quality onto Steam's
    /// EWirelessEndpointStrength (1 Weak … 4 Excellent). Connected implies at
    /// least Weak — the store's None draws the same empty bars as disconnected.</summary>
    /// <param name="signalPercent">Signal quality, 0-100.</param>
    public static int MapStrength(int signalPercent) => signalPercent switch
    {
        >= 75 => 4,
        >= 50 => 3,
        >= 25 => 2,
        _ => 1,
    };

    /// <summary>Pushes the current Wi-Fi state into Steam's network store (installs
    /// the resident script if needed). Disconnected state removes the synthetic AP
    /// and hands the store back to backend truth.</summary>
    /// <param name="connected">Whether Wi-Fi is joined to a network.</param>
    /// <param name="ssid">The joined network's name (shown in Steam's UI).</param>
    /// <param name="signalPercent">Signal quality, 0-100.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>True when the store accepted the update this session.</returns>
    public static async Task<bool> PushAsync(
        bool connected, string ssid, int signalPercent,
        CancellationToken cancellationToken = default)
    {
        var info = connected && !string.IsNullOrEmpty(ssid)
            ? "{connected:true,ssid:" + SteamCef.JsString(ssid)
                + ",strength:" + MapStrength(signalPercent) + "}"
            : "{connected:false}";
        var expression =
            "(()=>{try{" + ResidentSetup +
            "return window.__wsgm.applyNetInfo(" + info + ");}" +
            "catch(e){return JSON.stringify({ok:false,err:String((e&&e.stack)||e)});}})()";
        var result = await SteamCef.EvaluateAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(result.Value);
            var root = document.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                return true;
            }
            var err = root.TryGetProperty("err", out var e) ? e.GetString() : null;
            // "no wireless device yet" is a normal early-boot state, not a fault.
            Log.Info($"Network indicator push not applied: {err}.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Network indicator parse failed: {ex.Message}");
        }
        return false;
    }

    /// <summary>Removes the synthetic AP and forces a backend refresh, restoring
    /// Steam's own (empty-bars) truth. Best-effort; a Steam restart also recovers.</summary>
    public static Task<CefEvalResult> DisableAsync(CancellationToken cancellationToken = default)
        => SteamCef.EvaluateAsync(
            "(()=>{try{const W=window.__wsgm;if(W&&W.removeNetInfo)W.removeNetInfo(true);"
            + "return JSON.stringify({ok:true});}"
            + "catch(e){return JSON.stringify({ok:false,err:String(e)});}})()",
            Budget, cancellationToken);

    // The resident setup: applyNetInfo ingests {connected,ssid,strength} through the
    // store's own SetDeviceInfo (cloning the REAL wireless device so MAC/IP/default
    // route stay truthful), pins the entry against backend expiry, and recomputes the
    // observable connected flags MobX-reactively consumed by the header. Guarded by
    // netVer so re-running only refreshes the functions. Shape notes (from the live
    // store): device.estate 5=Connected, etype 2=Wireless; ap.estrength 0-4 maps
    // 1:1 to the icon's filled arcs; the map key is "<deviceId>:<wapId>".
    private const string ResidentSetup = """
        var W=window.__wsgm=window.__wsgm||{};
        if(W.netVer!==1){
          W.netVer=1;
          W.netWapId=990001;
          W.removeNetInfo=function(refresh){try{
            var st=window.SystemNetworkStore;
            if(st&&W.netKey&&st.m_mapNetworkAccessPoints.has(W.netKey)){
              st.m_mapNetworkAccessPoints.delete(W.netKey);
              st.m_bIsConnectedToANetwork=st.IsAnyDeviceConnected();
              st.m_bIsConnectingToANetwork=st.IsAnyDeviceConnecting();}
            W.netKey=null;
            var N=window.SteamClient&&SteamClient.System&&SteamClient.System.Network;
            if(refresh&&N&&N.ForceRefresh)N.ForceRefresh();
          }catch(e){}};
          W.applyNetInfo=function(info){
            var st=window.SystemNetworkStore;
            if(!st)return JSON.stringify({ok:false,err:'no store'});
            if(!info||!info.connected||!info.ssid){W.removeNetInfo(true);return JSON.stringify({ok:true,cleared:true});}
            if(!st.m_WirelessDevice)return JSON.stringify({ok:false,err:'no wireless device yet'});
            var dev=JSON.parse(JSON.stringify(st.m_WirelessDevice));
            if(!dev.wireless)dev.wireless={aps:[],esecurity_supported:0};
            dev.estate=5;
            dev.wireless.aps=[{id:W.netWapId,esecurity:16,estrength:info.strength,ssid:info.ssid,is_active:true,is_autoconnect:true,is_hidden:false}];
            var key=dev.id.toString()+':'+W.netWapId.toString();
            if(W.netKey&&W.netKey!==key&&st.m_mapNetworkAccessPoints.has(W.netKey))st.m_mapNetworkAccessPoints.delete(W.netKey);
            st.m_mapNetworkAccessPoints.delete(key);
            st.SetDeviceInfo(dev,W.netWapId);
            var ap=st.m_mapNetworkAccessPoints.get(key);
            if(!ap)return JSON.stringify({ok:false,err:'ap not created'});
            ap.MarkAsNotPresent=function(){};
            st.m_bIsConnectedToANetwork=st.IsAnyDeviceConnected();
            st.m_bIsConnectingToANetwork=st.IsAnyDeviceConnecting();
            W.netKey=key;
            return JSON.stringify({ok:true,strength:info.strength});
          };
        }
        """;
}
