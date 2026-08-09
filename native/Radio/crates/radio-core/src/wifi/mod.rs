//! Wi-Fi scan, connect and disconnect over the Win32 native WLAN API.
//!
//! WinRT's `WiFiAdapter` is not an option here: `RequestAccessAsync` is
//! documented to *always* return `DeniedBySystem` unless the app declares the
//! `wiFiControl` capability in a package manifest, and an unpackaged shell has
//! no manifest to declare it in. `wlanapi` is the documented, current Win32 API
//! that `netsh wlan` itself sits on, and it is the only path that scans and
//! connects with a password from this process.
//!
//! One caveat dominates everything in this module: since Windows 11 24H2, the
//! scan and current-connection entry points return `ERROR_ACCESS_DENIED` for any
//! app that lacks precise-location consent. That is reported as
//! [`Error::Win32`] with code 5 so the UI can explain it rather than showing an
//! empty list.

pub mod notify;
pub mod profile;
pub mod reason;

use std::ffi::c_void;
use std::ptr::null_mut;

use windows::Win32::Foundation::{ERROR_SUCCESS, HANDLE};
use windows::Win32::NetworkManagement::WiFi::{
    DOT11_AUTH_ALGO_80211_OPEN, DOT11_AUTH_ALGO_80211_SHARED_KEY, DOT11_AUTH_ALGO_OWE,
    DOT11_AUTH_ALGO_RSNA,
    DOT11_AUTH_ALGO_RSNA_PSK, DOT11_AUTH_ALGO_WPA, DOT11_AUTH_ALGO_WPA3,
    DOT11_AUTH_ALGO_WPA3_ENT, DOT11_AUTH_ALGO_WPA3_ENT_192, DOT11_AUTH_ALGO_WPA3_SAE,
    DOT11_AUTH_ALGO_WPA_PSK, WLAN_AVAILABLE_NETWORK_LIST, WLAN_CONNECTION_ATTRIBUTES,
    WLAN_CONNECTION_PARAMETERS, WLAN_INTERFACE_INFO_LIST, WLAN_INTERFACE_STATE,
    WLAN_PROFILE_INFO_LIST, WlanCloseHandle, WlanConnect, WlanDeleteProfile, WlanDisconnect,
    WlanEnumInterfaces, WlanFreeMemory, WlanGetAvailableNetworkList, WlanGetProfile,
    WlanGetProfileList, WlanOpenHandle, WlanQueryInterface, WlanScan, WlanSetProfile,
    dot11_BSS_type_infrastructure, wlan_connection_mode_profile,
    wlan_intf_opcode_current_connection,
};
use windows_core::{GUID, PCWSTR};

use crate::error::{Error, Result, win32};

pub use profile::Security;

/// The state of a WLAN interface, as far as the UI needs to distinguish it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum InterfaceState {
    /// Associated with a network.
    Connected,
    /// Association in progress.
    Connecting,
    /// Present but idle.
    Disconnected,
    /// Present but not ready (radio off, driver not started).
    Unavailable,
}

impl InterfaceState {
    fn from_raw(state: WLAN_INTERFACE_STATE) -> Self {
        // wlan_interface_state_*: 0 not_ready, 1 connected, 2 ad_hoc_formed,
        // 3 disconnecting, 4 disconnected, 5 associating, 6 discovering,
        // 7 authenticating.
        match state.0 {
            1 | 2 => Self::Connected,
            5..=7 => Self::Connecting,
            3 | 4 => Self::Disconnected,
            _ => Self::Unavailable,
        }
    }
}

/// One visible network, flattened for the UI.
#[derive(Debug, Clone)]
pub struct Network {
    /// The SSID as text, for display. Lossy: an SSID is a byte string, so this
    /// is not an identity — see [`Network::raw_ssid`].
    pub ssid: String,
    /// The SSID exactly as advertised. The real identity of the network.
    pub raw_ssid: Vec<u8>,
    /// Signal quality, 0-100 as Windows reports it.
    pub signal: u32,
    /// How the network is protected.
    pub security: Security,
    /// Whether a saved profile already exists, so no password is needed.
    pub saved: bool,
    /// Whether Windows believes the network can currently be joined.
    pub connectable: bool,
    /// Whether this is the network currently joined.
    pub connected: bool,
}

/// A WLAN client handle that closes itself.
struct Client(HANDLE);

impl Client {
    fn open() -> Result<Self> {
        let mut negotiated = 0u32;
        let mut handle = HANDLE::default();
        // Client version 2 is the Vista-and-later interface; version 1 hides
        // the WPA2+ authentication algorithms we need to classify networks.
        let status = unsafe { WlanOpenHandle(2, None, &mut negotiated, &mut handle) };
        win32("WlanOpenHandle", status)?;
        Ok(Self(handle))
    }
}

impl Drop for Client {
    fn drop(&mut self) {
        unsafe {
            let _ = WlanCloseHandle(self.0, None);
        }
    }
}

/// A `WlanFreeMemory` allocation that frees itself.
struct WlanBuffer<T>(*mut T);

impl<T> Drop for WlanBuffer<T> {
    fn drop(&mut self) {
        if !self.0.is_null() {
            unsafe { WlanFreeMemory(self.0.cast()) };
        }
    }
}

/// Every WLAN interface on the machine, with its state.
///
/// All of them, because one adapter is not the machine: a second Wi-Fi adapter
/// can be the one that sees the network the user wants, or the only one that is
/// usable at all, and keying every operation to a single GUID made those
/// networks simply never appear.
fn all_interfaces(client: &Client) -> Result<Vec<(GUID, InterfaceState)>> {
    let mut list: *mut WLAN_INTERFACE_INFO_LIST = null_mut();
    let status = unsafe { WlanEnumInterfaces(client.0, None, &mut list) };
    win32("WlanEnumInterfaces", status)?;
    let _owned = WlanBuffer(list);
    if list.is_null() {
        return Err(Error::NotFound("WLAN interface"));
    }
    // SAFETY: the list is a valid allocation and dwNumberOfItems describes how
    // many records follow the header, despite the [T; 1] in the declaration.
    let (count, items) = unsafe {
        let count = (*list).dwNumberOfItems as usize;
        (
            count,
            std::slice::from_raw_parts((*list).InterfaceInfo.as_ptr(), count),
        )
    };
    if count == 0 {
        return Err(Error::NotFound("WLAN interface"));
    }
    Ok(items
        .iter()
        .map(|i| (i.InterfaceGuid, InterfaceState::from_raw(i.isState)))
        .collect())
}

/// The GUID of the first WLAN interface, and its state.
fn first_interface(client: &Client) -> Result<(GUID, InterfaceState)> {
    let mut list: *mut WLAN_INTERFACE_INFO_LIST = null_mut();
    let status = unsafe { WlanEnumInterfaces(client.0, None, &mut list) };
    win32("WlanEnumInterfaces", status)?;
    let _owned = WlanBuffer(list);
    if list.is_null() {
        return Err(Error::NotFound("WLAN interface"));
    }
    // SAFETY: the list is a valid allocation and dwNumberOfItems describes how
    // many records follow the header, despite the [T; 1] in the declaration.
    let (count, items) = unsafe {
        let count = (*list).dwNumberOfItems as usize;
        (
            count,
            std::slice::from_raw_parts((*list).InterfaceInfo.as_ptr(), count),
        )
    };
    if count == 0 {
        return Err(Error::NotFound("WLAN interface"));
    }
    // Prefer a connected interface: a disconnected onboard adapter must not
    // mask a connected USB one, which is the same rule the taskbar tile uses.
    let chosen = items
        .iter()
        .find(|i| InterfaceState::from_raw(i.isState) == InterfaceState::Connected)
        .unwrap_or(&items[0]);
    Ok((chosen.InterfaceGuid, InterfaceState::from_raw(chosen.isState)))
}

/// Reads the state of the Wi-Fi interface.
pub fn state() -> Result<InterfaceState> {
    let client = Client::open()?;
    Ok(first_interface(&client)?.1)
}

/// The interface state together with the joined network's name and signal.
///
/// One call rather than three because the taskbar tile needs all of it on every
/// status tick: reading the signal only while the panel is open left the tile
/// showing no bars until the panel had been opened once.
pub fn status() -> Result<(InterfaceState, String, u32)> {
    let client = Client::open()?;
    let (guid, state) = first_interface(&client)?;
    let (ssid, signal) = current_connection(&client, &guid).unwrap_or_default();
    Ok((state, ssid, signal))
}

/// The names of every saved profile on an interface.
///
/// This is the authoritative answer to "is this network saved", and the reason
/// it exists: `WLAN_AVAILABLE_NETWORK.strProfileName` is not reliably populated
/// for every visible entry of a saved network, so deciding from the scan list
/// alone can report a saved network as new — and then ask for a password that
/// is already stored.
fn profile_names(client: &Client, guid: &GUID) -> Result<Vec<String>> {
    let mut list: *mut WLAN_PROFILE_INFO_LIST = null_mut();
    let status = unsafe { WlanGetProfileList(client.0, guid, None, &mut list) };
    win32("WlanGetProfileList", status)?;
    let _owned = WlanBuffer(list);
    if list.is_null() {
        return Ok(Vec::new());
    }
    // SAFETY: dwNumberOfItems describes the real length behind the [T; 1].
    let entries = unsafe {
        let count = (*list).dwNumberOfItems as usize;
        std::slice::from_raw_parts((*list).ProfileInfo.as_ptr(), count)
    };
    Ok(entries
        .iter()
        .map(|entry| decode_fixed(&entry.strProfileName))
        .filter(|name| !name.is_empty())
        .collect())
}

/// What the scan list knows about one SSID.
#[derive(Debug, Default, Clone)]
struct ScanFacts {
    /// The profile Windows would use, which is NOT always the SSID.
    profile: Option<String>,
    /// The SSID's bytes exactly as advertised. Empty when the network is not
    /// currently visible.
    raw_ssid: Vec<u8>,
    /// How the network protects itself, when it was seen.
    security: Option<Security>,
    /// The advertised authentication algorithm, which decides the profile
    /// shape — a legacy WPA/TKIP access point needs a different document from a
    /// WPA2 one, and installing the wrong one succeeds and then never connects.
    auth: Option<i32>,
    /// Set when two DIFFERENT advertised SSIDs share this display text, which
    /// invalid UTF-8 can produce. The name then identifies nothing, and acting
    /// on a guess would join or forget a network the user never picked.
    ambiguous: bool,
}

/// Everything `connect` needs from the scan list, in one pass.
///
/// The profile name matters because assuming it equals the SSID is a real
/// failure mode: when a profile of that name already exists Windows creates the
/// new one as "<SSID> 2", so a connect keyed on the SSID matches nothing and
/// the network sits there looking saved but unjoinable. The scan list's own
/// profile name is authoritative, with an exact name match as the fallback for
/// a network that is saved but out of range.
///
/// The raw bytes matter because an SSID is a byte string: the text form is
/// lossy, and a profile authored from it names a different network.
fn scan_facts(client: &Client, guid: &GUID, ssid: &str) -> ScanFacts {
    let mut facts = ScanFacts::default();
    let mut list: *mut WLAN_AVAILABLE_NETWORK_LIST = null_mut();
    let status = unsafe { WlanGetAvailableNetworkList(client.0, guid, 0, None, &mut list) };
    if status == 0 && !list.is_null() {
        let _owned = WlanBuffer(list);
        // SAFETY: dwNumberOfItems describes the real length behind the [T; 1].
        let entries = unsafe {
            let count = (*list).dwNumberOfItems as usize;
            std::slice::from_raw_parts((*list).Network.as_ptr(), count)
        };
        for entry in entries {
            let length = (entry.dot11Ssid.uSSIDLength as usize).min(entry.dot11Ssid.ucSSID.len());
            let name = decode_ssid(&entry.dot11Ssid.ucSSID, length);
            if name != ssid {
                continue;
            }
            let raw = entry.dot11Ssid.ucSSID[..length].to_vec();
            if facts.raw_ssid.is_empty() {
                facts.raw_ssid = raw;
            } else if facts.raw_ssid != raw {
                // Same text, different bytes: this name cannot address one
                // network. Recorded rather than resolved by guessing.
                facts.ambiguous = true;
            }
            facts.security.get_or_insert(classify(
                entry.bSecurityEnabled.as_bool(),
                entry.dot11DefaultAuthAlgorithm.0,
            ));
            facts.auth.get_or_insert(entry.dot11DefaultAuthAlgorithm.0);
            let profile = decode_fixed(&entry.strProfileName);
            if facts.profile.is_none() && !profile.is_empty() {
                facts.profile = Some(profile);
            }
        }
    }
    if facts.profile.is_none() {
        // Matched on the profile's own SSID, so a generated "<SSID> 2" name is
        // still recognised as this network's profile.
        let target: Vec<u8> = if facts.raw_ssid.is_empty() {
            ssid.as_bytes().to_vec()
        } else {
            facts.raw_ssid.clone()
        };
        facts.profile = profile_ssids(client, guid)
            .into_iter()
            .find(|(_, saved)| *saved == target)
            .map(|(name, _)| name);
    }
    facts
}

/// The SSID of the network currently joined, if any.
///
/// Gated by the same location consent as the scan on Windows 11 24H2, so a
/// failure here is not fatal — it only costs the "connected" marker.
fn current_ssid(client: &Client, guid: &GUID) -> Option<String> {
    current_connection(client, guid).map(|(ssid, _)| ssid)
}

/// The joined network's SSID and signal quality.
fn current_connection(client: &Client, guid: &GUID) -> Option<(String, u32)> {
    let mut size = 0u32;
    let mut data: *mut c_void = null_mut();
    let status = unsafe {
        WlanQueryInterface(
            client.0,
            guid,
            wlan_intf_opcode_current_connection,
            None,
            &mut size,
            &mut data,
            None,
        )
    };
    if status != 0 || data.is_null() {
        return None;
    }
    let _owned = WlanBuffer(data);
    // SAFETY: a successful current_connection query returns exactly one
    // WLAN_CONNECTION_ATTRIBUTES.
    let attributes = unsafe { &*(data as *const WLAN_CONNECTION_ATTRIBUTES) };
    if InterfaceState::from_raw(attributes.isState) != InterfaceState::Connected {
        return None;
    }
    let association = &attributes.wlanAssociationAttributes;
    let ssid = &association.dot11Ssid;
    let text = decode_ssid(&ssid.ucSSID, ssid.uSSIDLength as usize);
    (!text.is_empty()).then_some((text, association.wlanSignalQuality))
}

/// Decodes a NUL-terminated fixed-width UTF-16 field.
fn decode_fixed(field: &[u16]) -> String {
    let end = field.iter().position(|&c| c == 0).unwrap_or(field.len());
    String::from_utf16_lossy(&field[..end])
}

/// Asks the drivers to start a scan.
///
/// Returns as soon as the requests are accepted; results appear in the
/// available network list a few seconds later, which is why the caller polls
/// rather than expecting fresh results immediately. Every adapter is asked, so
/// a network only the second one can hear still turns up.
pub fn request_scan() -> Result<()> {
    let client = Client::open()?;
    let mut last = Err(Error::NotFound("WLAN interface"));
    let mut any_ok = false;
    for (guid, _) in all_interfaces(&client)? {
        let status = unsafe { WlanScan(client.0, &guid, None, None, None) };
        // EVERY adapter is asked before returning: stopping at the first
        // success left the others un-scanned, so a network only a later one
        // can hear never refreshed. One adapter refusing (radio off, driver
        // not started) must not stop the rest.
        match win32("WlanScan", status) {
            Ok(()) => any_ok = true,
            Err(error) => last = Err(error),
        }
    }
    if any_ok { Ok(()) } else { last }
}

/// Lists the networks the driver currently knows about.
///
/// Fails with [`Error::Win32`] code 5 when precise-location consent is missing;
/// that is the 24H2 gate, not a missing adapter.
pub fn networks() -> Result<Vec<Network>> {
    let client = Client::open()?;
    let interfaces = all_interfaces(&client)?;
    let mut networks: Vec<Network> = Vec::new();
    let mut last_error = None;
    let mut any_ok = false;
    // Every adapter contributes: the merge below keeps the strongest sighting
    // of each SSID, so a network only the second radio can hear still appears.
    for (guid, _) in &interfaces {
        match networks_on(&client, guid, &mut networks) {
            Ok(()) => any_ok = true,
            Err(error) => last_error = Some(error),
        }
    }
    // Only a TOTAL failure is reported. Keyed on whether any adapter answered,
    // not on whether the list came back non-empty: an adapter that legitimately
    // sees nothing is a successful empty scan, and reporting that as an error
    // because a second adapter is disabled would turn "no networks here" into
    // "scanning failed".
    if !any_ok
        && let Some(error) = last_error
    {
        return Err(error);
    }
    networks.sort_by(|a, b| b.signal.cmp(&a.signal).then_with(|| a.ssid.cmp(&b.ssid)));
    Ok(networks)
}

/// Merges one interface's visible networks into `networks`.
fn networks_on(client: &Client, guid: &GUID, networks: &mut Vec<Network>) -> Result<()> {
    let mut list: *mut WLAN_AVAILABLE_NETWORK_LIST = null_mut();
    // Flag 0: only networks the driver can actually see, and one entry per
    // visible SSID rather than one per profile.
    let status = unsafe { WlanGetAvailableNetworkList(client.0, guid, 0, None, &mut list) };
    win32("WlanGetAvailableNetworkList", status)?;
    let _owned = WlanBuffer(list);
    if list.is_null() {
        return Ok(());
    }
    // SAFETY: as in first_interface — dwNumberOfItems describes the real length.
    let entries = unsafe {
        let count = (*list).dwNumberOfItems as usize;
        std::slice::from_raw_parts((*list).Network.as_ptr(), count)
    };

    // Both read from the service rather than being inferred from the scan list:
    // getting "saved" wrong is what makes the UI ask for a password it already
    // has, and then overwrite the stored one.
    // Keyed by the SSID each profile joins, not by its name: a profile Windows
    // generated as "<SSID> 2" belongs to this network just as much.
    let saved_profiles = profile_ssids(client, guid);
    let connected = current_ssid(client, guid);

    for entry in entries {
        let length = (entry.dot11Ssid.uSSIDLength as usize).min(entry.dot11Ssid.ucSSID.len());
        let ssid = decode_ssid(&entry.dot11Ssid.ucSSID, length);
        // A hidden network broadcasts an empty SSID. Connecting names the
        // profile by SSID, so there is nothing this panel could do with the
        // entry, and an unjoinable blank row is worse than no row. Joining one
        // means typing its name, which is a flow we do not offer.
        if ssid.is_empty() {
            continue;
        }
        let security = classify(
            entry.bSecurityEnabled.as_bool(),
            entry.dot11DefaultAuthAlgorithm.0,
        );
        let raw = entry.dot11Ssid.ucSSID[..length].to_vec();
        let saved = entry.strProfileName[0] != 0
            || saved_profiles.iter().any(|(_, target)| *target == raw);
        let is_connected = connected.as_deref() == Some(ssid.as_str());
        // Merged on the SSID's BYTES, not its display text. Two different
        // networks whose invalid UTF-8 decodes to the same replacement string
        // are different networks, and folding them into one row would combine
        // their saved/joinable facts and let an action land on the wrong one.
        if let Some(existing) = networks.iter_mut().find(|n| n.raw_ssid == raw) {
            existing.signal = existing.signal.max(entry.wlanSignalQuality);
            existing.saved |= saved;
            existing.connectable |= entry.bNetworkConnectable.as_bool();
            existing.connected |= is_connected;
            continue;
        }
        networks.push(Network {
            ssid,
            raw_ssid: raw,
            signal: entry.wlanSignalQuality,
            security,
            saved,
            connectable: entry.bNetworkConnectable.as_bool(),
            connected: is_connected,
        });
    }
    Ok(())
}

/// An SSID is a byte string, not text. Windows shows it as UTF-8, and a
/// non-decodable byte must not lose the whole entry, so this is lossy.
fn decode_ssid(bytes: &[u8; 32], len: usize) -> String {
    let len = len.min(bytes.len());
    String::from_utf8_lossy(&bytes[..len]).into_owned()
}

/// Maps the advertised authentication algorithm onto what the profile builder
/// needs to know.
fn classify(secured: bool, auth: i32) -> Security {
    if !secured {
        return Security::Open;
    }
    match auth {
        // Enhanced Open is passwordless like an open network but encrypted, and
        // needs its own profile shape — sharing Security::Open authored an
        // open/none profile that could never join it.
        a if a == DOT11_AUTH_ALGO_OWE.0 => Security::EnhancedOpen,
        // Open-system auth on a SECURED network is WEP, not an open network:
        // the flag is the only thing telling them apart, and treating it as
        // open skipped the password prompt and then authored open/none.
        // Shared-key is WEP's other half. Neither has a profile shape here.
        a if a == DOT11_AUTH_ALGO_80211_OPEN.0 || a == DOT11_AUTH_ALGO_80211_SHARED_KEY.0 => {
            Security::Unsupported
        }
        a if a == DOT11_AUTH_ALGO_WPA_PSK.0
            || a == DOT11_AUTH_ALGO_RSNA_PSK.0
            || a == DOT11_AUTH_ALGO_WPA3_SAE.0 =>
        {
            Security::PersonalPsk
        }
        a if a == DOT11_AUTH_ALGO_WPA.0
            || a == DOT11_AUTH_ALGO_RSNA.0
            || a == DOT11_AUTH_ALGO_WPA3.0
            || a == DOT11_AUTH_ALGO_WPA3_ENT.0
            || a == DOT11_AUTH_ALGO_WPA3_ENT_192.0 =>
        {
            Security::Enterprise
        }
        // Anything unrecognised but secured is treated as a pre-shared key,
        // which at worst asks for a password the network does not want.
        _ => Security::PersonalPsk,
    }
}

fn wide(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(std::iter::once(0)).collect()
}

/// Installs a profile and connects to it.
///
/// `passphrase` is `None` for an open network. WPA3 transition-mode profiles are
/// tried first on a secured network and retried as plain WPA2-PSK if Windows
/// rejects the profile, which is what happens on Windows 10 and on adapters
/// whose driver predates WPA3.
///
/// Returns the raw WLAN reason code on a profile rejection so the caller can
/// render it with [`reason::describe`].
pub fn connect(ssid: &str, passphrase: Option<&str>) -> Result<()> {
    if ssid.is_empty() {
        return Err(Error::InvalidArgument("empty SSID"));
    }
    if let Some(pass) = passphrase
        && !profile::passphrase_is_valid(pass)
    {
        return Err(Error::InvalidArgument(
            "the password must be 8-63 characters, or 64 hex digits",
        ));
    }

    let client = Client::open()?;
    // The adapter that can actually see this network, not simply the first one:
    // with two radios the network may be visible to only one of them, and
    // connecting on the other can never work.
    let interfaces = all_interfaces(&client)?;
    let mut chosen = None;
    let mut chosen_facts = ScanFacts::default();
    let mut chosen_rank = 0u8;
    for (candidate, _) in &interfaces {
        let facts = scan_facts(&client, candidate, ssid);
        let visible = !facts.raw_ssid.is_empty();
        let saved = facts.profile.is_some();
        // Ranked, not first-past-the-post. The merged UI row reports "saved"
        // when ANY adapter holds the profile, so a keyless attempt routed to a
        // different adapter that merely sees the network finds no profile and
        // fails asking for a password the row does not offer. An adapter that
        // both sees it and holds the profile is the only one certain to work.
        let rank = match (visible, saved) {
            (true, true) => 4,
            // With no passphrase the profile is what makes the join possible;
            // with one, seeing the network is.
            (false, true) => {
                if passphrase.is_none() {
                    3
                } else {
                    1
                }
            }
            (true, false) => 2,
            (false, false) => 0,
        };
        if rank > chosen_rank {
            chosen_rank = rank;
            chosen = Some(*candidate);
            chosen_facts = facts;
        }
    }
    let guid = match chosen {
        Some(guid) => guid,
        None => first_interface(&client)?.0,
    };
    let facts = chosen_facts;
    // Refused rather than guessed: joining "the first network whose lossy name
    // matches" could authenticate against an access point the user never
    // selected, and hand it the password they typed.
    if facts.ambiguous {
        return Err(Error::InvalidArgument(
            "more than one network advertises this name; it cannot be identified",
        ));
    }
    let raw = (!facts.raw_ssid.is_empty()).then_some(facts.raw_ssid.as_slice());
    let existing = facts.profile.clone();

    // A profile is authored ONLY when the caller supplied a passphrase. Writing
    // one for a network that already has a saved profile would overwrite it —
    // and with no passphrase to put in it, an open-network profile would replace
    // the user's stored credentials and destroy them. Joining a saved network
    // means connecting to the profile that is already there, untouched.
    let mut authored: Option<String> = None;
    match passphrase {
        Some(pass) => {
            // Ordered by what the access point actually advertises, not by
            // preference: WlanSetProfile accepts a document whether or not it
            // describes the network, so the FIRST attempt is almost always the
            // one that gets installed — and a WPA2/AES profile authored for a
            // legacy WPA/TKIP network installs cleanly and then never connects.
            let flavors: &[profile::PskFlavor] = match facts.auth {
                Some(a) if a == DOT11_AUTH_ALGO_WPA_PSK.0 => &[
                    profile::PskFlavor::WpaTkip,
                    profile::PskFlavor::Wpa2Aes,
                    profile::PskFlavor::Wpa3Transition,
                ],
                // WPA3 transition mode covers WPA3-Personal and WPA2-PSK alike;
                // the plain WPA2 document is the fallback for Windows 10 and
                // adapters whose driver predates WPA3.
                _ => &[
                    profile::PskFlavor::Wpa3Transition,
                    profile::PskFlavor::Wpa2Aes,
                ],
            };
            let attempts: Vec<String> = flavors
                .iter()
                .map(|flavor| profile::psk_profile(ssid, raw, pass, *flavor))
                .collect();
            let mut last: Option<Error> = None;
            let mut installed = false;
            for xml in &attempts {
                match set_profile(&client, &guid, xml) {
                    Ok(()) => {
                        installed = true;
                        break;
                    }
                    Err(e) => last = Some(e),
                }
            }
            if !installed {
                return Err(
                    last.unwrap_or(Error::InvalidArgument("profile could not be installed"))
                );
            }
            authored = Some(ssid.to_owned());
        }
        None => {
            // No passphrase: only ever create a profile when the network has
            // none at all AND needs no key. Anything else is either already
            // joinable or genuinely missing a password.
            if existing.is_none() {
                // And only when the scan says it really is keyless. The caller
                // arrives here whenever it believes a profile exists — a belief
                // that goes stale when the profile was deleted elsewhere, or
                // when its name differs from the SSID. Authoring open/none for
                // a secured access point installs cleanly and then cannot
                // connect, so say what is actually missing instead.
                match facts.security {
                    Some(Security::Unsupported) => {
                        return Err(Error::InvalidArgument(
                            "this network's security (WEP) is not supported",
                        ));
                    }
                    Some(Security::Open) | Some(Security::EnhancedOpen) => {
                        let owe = facts.security == Some(Security::EnhancedOpen);
                        set_profile(&client, &guid, &profile::open_profile(ssid, raw, owe))?;
                        authored = Some(ssid.to_owned());
                    }
                    _ => {
                        return Err(Error::InvalidArgument(
                            "this network needs a password and has no saved profile",
                        ));
                    }
                }
            }
        }
    }

    // Connect by the profile's real name. Using the SSID here is what left a
    // network stuck on "saved": the profile was called "<SSID> 2" and nothing
    // matched. After authoring a profile ourselves the name IS the SSID,
    // because that is what we wrote into it.
    let profile_name = match authored {
        Some(ref name) => name.clone(),
        None => existing.unwrap_or_else(|| ssid.to_owned()),
    };
    let name = wide(&profile_name);
    let parameters = WLAN_CONNECTION_PARAMETERS {
        wlanConnectionMode: wlan_connection_mode_profile,
        strProfile: PCWSTR(name.as_ptr()),
        pDot11Ssid: null_mut(),
        pDesiredBssidList: null_mut(),
        dot11BssType: dot11_BSS_type_infrastructure,
        dwFlags: 0,
    };

    // Armed BEFORE the call: WlanConnect only reports that the request was
    // ACCEPTED, and a fast verdict would otherwise land before anyone listens.
    let watch = notify::ConnectionWatch::start(guid, &profile_name).ok();
    let status = unsafe { WlanConnect(client.0, &guid, &parameters, None) };
    if let Err(error) = win32("WlanConnect", status) {
        // Rejected outright (adapter gone, radio switched off between the two
        // calls): the profile this call authored was never tried, so leaving it
        // saved would suppress the password prompt on every later attempt.
        roll_back_authored(&client, &guid, authored.as_deref());
        return Err(error);
    }

    let Some(watch) = watch else {
        // No registration to wait on — but the request being accepted is NOT a
        // connection, and returning Ok here would report a join that may still
        // fail while leaving the profile just authored in place. Fall back to
        // asking the interface itself.
        let polled = poll_for_connection(&client, &guid, ssid, CONNECT_TIMEOUT);
        if polled.is_err() {
            // Verified not connected, so a profile authored by THIS call is
            // unproven: leaving it makes the network read as saved, which stops
            // the panel ever asking for the password again.
            roll_back_authored(&client, &guid, authored.as_deref());
        }
        return polled;
    };
    match watch.wait(CONNECT_TIMEOUT) {
        Some(outcome) if outcome.succeeded => Ok(()),
        Some(outcome) => {
            // A key the AP rejected leaves a saved profile carrying the wrong
            // password behind. Left there, the network reads as "saved", the
            // panel stops asking for a password, and every later attempt fails
            // in silence — so the profile this call authored is rolled back.
            if matches!(
                reason::verdict(outcome.reason),
                reason::Verdict::WrongPassword | reason::Verdict::BadProfile
            ) {
                roll_back_authored(&client, &guid, authored.as_deref());
            }
            Err(notify::connection_error(outcome))
        }
        // Silence is not success: report it as a failure the user can retry
        // rather than claiming a connection that never happened. The profile
        // goes only if the interface confirms nothing came up, so a verdict
        // lost in transit cannot destroy a working profile.
        None => {
            if current_connection(&client, &guid).is_none_or(|(joined, _)| joined != ssid) {
                roll_back_authored(&client, &guid, authored.as_deref());
            }
            Err(Error::TimedOut("the connection attempt"))
        }
    }
}

/// Removes a profile this call authored, after the attempt it was written for
/// failed. A pre-existing profile is never touched: the user's stored
/// credentials are not ours to delete.
fn roll_back_authored(client: &Client, guid: &GUID, authored: Option<&str>) {
    if let Some(name) = authored {
        delete_profile(client, guid, name);
    }
}

/// Watches the interface itself for the requested SSID to come up.
///
/// The fallback for when notifications could not be registered. Slower and
/// blunter than a verdict — it cannot tell a wrong password from an absent
/// network, so it reports a timeout and lets the caller say so — but it never
/// claims a connection that did not happen.
fn poll_for_connection(
    client: &Client,
    guid: &GUID,
    ssid: &str,
    timeout: std::time::Duration,
) -> Result<()> {
    let deadline = std::time::Instant::now() + timeout;
    while std::time::Instant::now() < deadline {
        if let Some((joined, _)) = current_connection(client, guid)
            && joined == ssid
        {
            return Ok(());
        }
        std::thread::sleep(std::time::Duration::from_millis(500));
    }
    Err(Error::TimedOut("the connection attempt"))
}

/// How long to wait for the WLAN service's verdict on a connection attempt.
/// Association and authentication are seconds; this only has to outlast a slow
/// AP, not DHCP (`connection_complete` fires at L2).
const CONNECT_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(25);

/// Deletes one profile, ignoring failure: this is cleanup on an error path and
/// the error already being reported is the one that matters.
fn delete_profile(client: &Client, guid: &GUID, name: &str) {
    let wide_name = wide(name);
    unsafe {
        let _ = WlanDeleteProfile(client.0, guid, PCWSTR(wide_name.as_ptr()), None);
    }
}

/// Installs one profile document.
///
/// `dwFlags = 0` deliberately: that creates an **all-user** profile. A user-scope
/// profile is unreachable from outside the interactive session, which makes
/// `WlanConnect` fail `PmVerifyProfileAccess`, and it would also be invisible to
/// the normal Windows network list afterwards. Overwrite is on so reconnecting
/// with a corrected password replaces the bad profile instead of failing.
fn set_profile(client: &Client, guid: &GUID, xml: &str) -> Result<()> {
    let document = wide(xml);
    let mut reason_code = 0u32;
    let status = unsafe {
        WlanSetProfile(
            client.0,
            guid,
            0,
            PCWSTR(document.as_ptr()),
            PCWSTR::null(),
            true,
            None,
            &mut reason_code,
        )
    };
    if status == ERROR_SUCCESS.0 {
        return Ok(());
    }
    // A non-success reason code is far more specific than the status: it names
    // the element that was wrong (bad passphrase length, unsupported auth).
    if reason_code != 0 {
        return Err(Error::Win32 {
            api: "WlanSetProfile",
            code: reason_code,
        });
    }
    win32("WlanSetProfile", status)
}

/// Disconnects every connected Wi-Fi interface.
///
/// All of them: with two adapters the connected one is not necessarily the one
/// a single-interface disconnect would have picked, and leaving it joined looks
/// exactly like the button doing nothing.
pub fn disconnect() -> Result<()> {
    let client = Client::open()?;
    let interfaces = all_interfaces(&client)?;
    let mut last = Ok(());
    let mut acted = false;
    for (guid, state) in &interfaces {
        if !matches!(state, InterfaceState::Connected | InterfaceState::Connecting) {
            continue;
        }
        acted = true;
        let status = unsafe { WlanDisconnect(client.0, guid, None) };
        if let Err(error) = win32("WlanDisconnect", status) {
            last = Err(error);
        }
    }
    if !acted {
        // Nothing was joined, which is the state the caller asked for.
        return Ok(());
    }
    last
}

/// Every saved profile paired with the SSID it actually joins.
///
/// Read from each profile's XML rather than taken from its name, because the
/// two are not the same thing: Windows names a second profile for one network
/// "<SSID> 2", so a name comparison reports that network as unsaved and sends
/// the user back to re-type a password Windows already has.
fn profile_ssids(client: &Client, guid: &GUID) -> Vec<(String, Vec<u8>)> {
    let mut found = Vec::new();
    for name in profile_names(client, guid).unwrap_or_default() {
        let ssid = profile_xml(client, guid, &name)
            .and_then(|xml| profile::ssid_of_profile(&xml))
            // An unreadable document falls back to the name, which is what the
            // profile is called for a network of the same name anyway.
            .unwrap_or_else(|| name.as_bytes().to_vec());
        found.push((name, ssid));
    }
    found
}

/// Reads one saved profile's XML document.
fn profile_xml(client: &Client, guid: &GUID, name: &str) -> Option<String> {
    let wide_name = wide(name);
    let mut document = windows_core::PWSTR::null();
    let status = unsafe {
        WlanGetProfile(
            client.0,
            guid,
            PCWSTR(wide_name.as_ptr()),
            None,
            &mut document,
            None,
            None,
        )
    };
    if status != 0 || document.is_null() {
        return None;
    }
    // SAFETY: a successful call returns a NUL-terminated WlanFreeMemory
    // allocation, which the buffer guard releases.
    unsafe {
        let _owned = WlanBuffer(document.0);
        document.to_string().ok()
    }
}

/// Removes a saved profile, so the network stops joining automatically.
pub fn forget(ssid: &str) -> Result<()> {
    let client = Client::open()?;
    let mut last = Ok(());
    // Profiles are per-interface, so a second adapter keeps its own copy and
    // would happily rejoin a network the user just forgot.
    for (guid, _) in all_interfaces(&client)? {
        if let Err(error) = forget_on(&client, &guid, ssid) {
            last = Err(error);
        }
    }
    last
}

/// Removes every profile for `ssid` from one interface.
fn forget_on(client: &Client, guid: &GUID, ssid: &str) -> Result<()> {
    let guid = *guid;
    let facts = scan_facts(client, &guid, ssid);
    // Same rule as connect: deleting the profile of a network the user did not
    // pick is worse than declining to act.
    if facts.ambiguous {
        return Err(Error::InvalidArgument(
            "more than one network advertises this name; it cannot be identified",
        ));
    }
    // Matched on each profile's OWN SSID, read back out of its document, not on
    // its name: Windows names the second profile for one network "<SSID> 2",
    // so a name comparison leaves every duplicate behind to keep auto-joining
    // the network the user just forgot.
    let target: Vec<u8> = if facts.raw_ssid.is_empty() {
        ssid.as_bytes().to_vec()
    } else {
        facts.raw_ssid.clone()
    };
    let mut names: Vec<String> = Vec::new();
    for (name, saved) in profile_ssids(client, &guid) {
        if saved == target && !names.contains(&name) {
            names.push(name);
        }
    }
    if let Some(bound) = facts.profile
        && !names.contains(&bound)
    {
        names.push(bound);
    }
    if names.is_empty() {
        return Ok(());
    }
    let mut last = Ok(());
    for name in names {
        let wide_name = wide(&name);
        let status =
            unsafe { WlanDeleteProfile(client.0, &guid, PCWSTR(wide_name.as_ptr()), None) };
        if status != 0 {
            last = win32("WlanDeleteProfile", status);
        }
    }
    last
}

#[cfg(test)]
mod connect_tests {
    use super::*;

    #[test]
    fn owe_networks_are_classified_apart_from_legacy_open_ones() {
        // Sharing Security::Open authored an open/none profile, which does not
        // describe an OWE network and cannot join it.
        assert_eq!(
            classify(true, DOT11_AUTH_ALGO_OWE.0),
            Security::EnhancedOpen
        );
        // Open-system auth on a SECURED network is WEP, not an open one; the
        // genuinely open case carries no security flag.
        assert_eq!(
            classify(false, DOT11_AUTH_ALGO_80211_OPEN.0),
            Security::Open
        );
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn an_unsecured_network_is_open_whatever_the_algorithm_says() {
        assert_eq!(classify(false, DOT11_AUTH_ALGO_RSNA_PSK.0), Security::Open);
    }

    #[test]
    fn wep_is_not_mistaken_for_an_open_network() {
        // Open-system auth WITH security enabled is WEP. The flag is the only
        // thing separating the two, and calling it open skipped the password
        // prompt and then authored an open/none profile that cannot join.
        assert_eq!(
            classify(true, DOT11_AUTH_ALGO_80211_OPEN.0),
            Security::Unsupported
        );
        assert_eq!(
            classify(true, DOT11_AUTH_ALGO_80211_SHARED_KEY.0),
            Security::Unsupported
        );
        // Without the security flag it really is an open network.
        assert_eq!(
            classify(false, DOT11_AUTH_ALGO_80211_OPEN.0),
            Security::Open
        );
    }

    #[test]
    fn psk_and_sae_networks_both_ask_for_a_passphrase() {
        assert_eq!(
            classify(true, DOT11_AUTH_ALGO_RSNA_PSK.0),
            Security::PersonalPsk
        );
        assert_eq!(
            classify(true, DOT11_AUTH_ALGO_WPA3_SAE.0),
            Security::PersonalPsk
        );
    }

    #[test]
    fn enterprise_networks_are_recognised_and_not_offered_a_password_box() {
        assert_eq!(classify(true, DOT11_AUTH_ALGO_RSNA.0), Security::Enterprise);
        assert_eq!(
            classify(true, DOT11_AUTH_ALGO_WPA3_ENT_192.0),
            Security::Enterprise
        );
    }

    #[test]
    fn ssid_decoding_stops_at_the_reported_length() {
        let mut bytes = [0u8; 32];
        bytes[..4].copy_from_slice(b"Cafe");
        bytes[4] = b'X';
        assert_eq!(decode_ssid(&bytes, 4), "Cafe");
    }

    #[test]
    fn an_overlong_ssid_length_cannot_read_past_the_field() {
        let bytes = [b'a'; 32];
        assert_eq!(decode_ssid(&bytes, 999).len(), 32);
    }

    #[test]
    fn a_non_utf8_ssid_still_produces_an_entry() {
        let mut bytes = [0u8; 32];
        bytes[..2].copy_from_slice(&[0xff, 0xfe]);
        assert!(!decode_ssid(&bytes, 2).is_empty());
    }
}
