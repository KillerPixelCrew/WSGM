using System;
using System.Collections.Generic;
using System.Text;
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

    /// <summary>One access point as Steam's network list should show it.</summary>
    /// <param name="Ssid">Network name.</param>
    /// <param name="SignalPercent">Signal quality, 0-100.</param>
    /// <param name="Secured">Whether joining needs a credential.</param>
    /// <param name="Connected">Whether this is the joined network.</param>
    public readonly record struct SteamNetworkAccessPoint(
        string Ssid,
        int SignalPercent,
        bool Secured,
        bool Connected
    );

    /// <summary>
    /// Publishes the whole visible network list into Steam's store.
    /// </summary>
    /// <param name="networks">Networks to show, in the order they should appear.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>True when the store accepted the list.</returns>
    /// <remarks>
    /// The header indicator needs only the joined network, which is why <see cref="PushAsync"/>
    /// exists and stays the cheap path. This is the list the Internet page and the Wi-Fi row show,
    /// and it is only worth sending once that surface is revealed.
    /// <para>
    /// Each entry gets a synthetic access-point id derived from its position, so the identifiers
    /// stay stable while the list does and Steam's map is updated rather than rebuilt. The Windows
    /// backend never reports an access point of its own, so no real id can collide.
    /// </para>
    /// </remarks>
    public static async Task<bool> PushNetworksAsync(
        IReadOnlyList<SteamNetworkAccessPoint> networks,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(networks);
        StringBuilder list = new("[");
        for (int index = 0; index < networks.Count && index < MaxPublishedNetworks; index++)
        {
            SteamNetworkAccessPoint network = networks[index];
            if (string.IsNullOrEmpty(network.Ssid))
            {
                continue;
            }

            if (list.Length > 1)
            {
                list.Append(',');
            }

            list.Append("{ssid:")
                .Append(SteamCef.JsString(network.Ssid))
                .Append(",strength:")
                .Append(MapStrength(network.SignalPercent))
                .Append(",secured:")
                .Append(network.Secured ? "true" : "false")
                .Append(",connected:")
                .Append(network.Connected ? "true" : "false")
                .Append('}');
        }

        list.Append(']');
        string expression =
            "(()=>{try{" + ResidentSetup +
            "return window.__wsgm.applyNetworks(" + list + ");}" +
            "catch(e){return JSON.stringify({ok:false,err:String((e&&e.stack)||e)});}})()";
        CefEvalResult result = await SteamCef.EvaluateAsync(expression, Budget, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.Value);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            string? error = root.TryGetProperty("err", out JsonElement e) ? e.GetString() : null;
            Log.Info($"Network list push not applied: {error}.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Network list parse failed: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Most networks published at once.
    /// </summary>
    /// <remarks>
    /// A dense area produces dozens of results and every one of them costs an entry in Steam's map
    /// plus a row a user has to thumb past. The cap is on what is worth showing, not on what the
    /// radio can see.
    /// </remarks>
    private const int MaxPublishedNetworks = 24;

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
    //
    // When the resident functions change, bump BOTH netVer literals below (the
    // "W.netVer!==1" guard and the "W.netVer=1" assignment) — a live Steam session
    // keeps the functions already installed into it, so without the bump an upgraded
    // WSGM keeps calling the OLD applyNetInfo until Steam restarts (same pattern as
    // the badge script's BadgeScriptVersion). netWapId is the synthetic access-point
    // id; any value works as long as it never collides with a real one (the Windows
    // backend never reports any).
    //
    // v2 publishes a LIST. The correction that only a live run revealed: one map entry
    // is ONE access point, keyed by its own m_DeviceWapId, and the entry's SetDeviceInfo
    // scans dev.wireless.aps for the id matching that key. So the whole device (carrying
    // every AP) is passed once PER access point — a single SetDeviceInfo call registers
    // exactly one entry no matter how many APs the device lists, which is why the first
    // attempt reported created:1 for a three-network list. applyNetInfo now delegates to
    // applyNetworks with a one-element list so the header indicator keeps its contract.
    //
    // Verifying this from a probe while WSGM is ALSO running looks like a bug and is not:
    // the resident NetworkIndicatorService re-pushes every 10 s, and its stale cleanup
    // deletes the synthetic ids not in its own one-element list — including a probe's,
    // because both start from netWapId. A probe wanting to observe several entries has to
    // use ids outside that range, or stop the running instance first.
    private const string ResidentSetup = """
        var W=window.__wsgm=window.__wsgm||{};
        if(W.netVer!==2){
          W.netVer=2;
          W.netWapId=990001;
          W.netKeys=W.netKeys||[];
          W.removeNetInfo=function(refresh){try{
            var st=window.SystemNetworkStore;
            if(st){
              var keys=(W.netKeys||[]).slice();
              if(W.netKey)keys.push(W.netKey);
              for(var i=0;i<keys.length;i++){
                if(st.m_mapNetworkAccessPoints.has(keys[i]))st.m_mapNetworkAccessPoints.delete(keys[i]);}
              st.m_bIsConnectedToANetwork=st.IsAnyDeviceConnected();
              st.m_bIsConnectingToANetwork=st.IsAnyDeviceConnecting();}
            W.netKey=null;W.netKeys=[];
            var N=window.SteamClient&&SteamClient.System&&SteamClient.System.Network;
            if(refresh&&N&&N.ForceRefresh)N.ForceRefresh();
          }catch(e){}};
          W.applyNetworks=function(list){
            var st=window.SystemNetworkStore;
            if(!st)return JSON.stringify({ok:false,err:'no store'});
            if(!list||!list.length){W.removeNetInfo(true);return JSON.stringify({ok:true,cleared:true});}
            if(!st.m_WirelessDevice)return JSON.stringify({ok:false,err:'no wireless device yet'});
            var dev=JSON.parse(JSON.stringify(st.m_WirelessDevice));
            if(!dev.wireless)dev.wireless={aps:[],esecurity_supported:0};
            var joined=false,aps=[],keys=[];
            for(var i=0;i<list.length;i++){
              var n=list[i],id=W.netWapId+i;
              if(n.connected)joined=true;
              aps.push({id:id,esecurity:n.secured?16:0,estrength:n.strength,ssid:n.ssid,
                is_active:!!n.connected,is_autoconnect:!!n.connected,is_hidden:false});
              keys.push(dev.id.toString()+':'+id.toString());}
            dev.estate=joined?5:dev.estate;
            dev.wireless.aps=aps;
            var stale=(W.netKeys||[]).concat(W.netKey?[W.netKey]:[]);
            for(var j=0;j<stale.length;j++){
              if(keys.indexOf(stale[j])<0&&st.m_mapNetworkAccessPoints.has(stale[j]))
                st.m_mapNetworkAccessPoints.delete(stale[j]);}
            for(var k=0;k<keys.length;k++)st.m_mapNetworkAccessPoints.delete(keys[k]);
            for(var p=0;p<aps.length;p++)st.SetDeviceInfo(dev,aps[p].id);
            var created=0;
            for(var m=0;m<keys.length;m++){
              var ap=st.m_mapNetworkAccessPoints.get(keys[m]);
              if(ap){ap.MarkAsNotPresent=function(){};created++;}}
            if(!created)return JSON.stringify({ok:false,err:'no ap created'});
            st.m_bIsConnectedToANetwork=st.IsAnyDeviceConnected();
            st.m_bIsConnectingToANetwork=st.IsAnyDeviceConnecting();
            W.netKeys=keys;W.netKey=joined?keys[0]:null;
            return JSON.stringify({ok:true,created:created,requested:list.length});
          };
          W.applyNetInfo=function(info){
            if(!info||!info.connected||!info.ssid){W.removeNetInfo(true);return JSON.stringify({ok:true,cleared:true});}
            return W.applyNetworks([{ssid:info.ssid,strength:info.strength,secured:true,connected:true}]);
          };
        }
        """;
}
