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
    DOT11_AUTH_ALGO_80211_OPEN, DOT11_AUTH_ALGO_OWE, DOT11_AUTH_ALGO_RSNA,
    DOT11_AUTH_ALGO_RSNA_PSK, DOT11_AUTH_ALGO_WPA, DOT11_AUTH_ALGO_WPA3,
    DOT11_AUTH_ALGO_WPA3_ENT, DOT11_AUTH_ALGO_WPA3_ENT_192, DOT11_AUTH_ALGO_WPA3_SAE,
    DOT11_AUTH_ALGO_WPA_PSK, WLAN_AVAILABLE_NETWORK_LIST, WLAN_CONNECTION_ATTRIBUTES,
    WLAN_CONNECTION_PARAMETERS, WLAN_INTERFACE_INFO_LIST, WLAN_INTERFACE_STATE,
    WLAN_PROFILE_INFO_LIST, WlanCloseHandle, WlanConnect, WlanDeleteProfile, WlanDisconnect,
    WlanEnumInterfaces, WlanFreeMemory, WlanGetAvailableNetworkList, WlanGetProfileList,
    WlanOpenHandle, WlanQueryInterface, WlanScan, WlanSetProfile,
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
    /// The SSID as text. Empty for a hidden network.
    pub ssid: String,
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

/// The name of the saved profile that joins `ssid`, if there is one.
///
/// Not the same as the SSID, and assuming it was is a real failure mode: when a
/// profile of that name already exists Windows creates the new one as
/// "<SSID> 2", so a connect keyed on the SSID matches nothing and the network
/// sits there looking saved but unjoinable. The scan list's own profile name is
/// authoritative — it is the profile Windows would use — with an exact
/// name match as the fallback for a network that is saved but not in range.
fn profile_for_ssid(client: &Client, guid: &GUID, ssid: &str) -> Option<String> {
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
            let name = decode_ssid(&entry.dot11Ssid.ucSSID, entry.dot11Ssid.uSSIDLength as usize);
            if name != ssid {
                continue;
            }
            let profile = decode_fixed(&entry.strProfileName);
            if !profile.is_empty() {
                return Some(profile);
            }
        }
    }
    profile_names(client, guid)
        .ok()?
        .into_iter()
        .find(|name| name == ssid)
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

/// Asks the driver to start a scan.
///
/// Returns as soon as the request is accepted; results appear in the available
/// network list a few seconds later, which is why the caller polls rather than
/// expecting fresh results immediately.
pub fn request_scan() -> Result<()> {
    let client = Client::open()?;
    let (guid, _) = first_interface(&client)?;
    let status = unsafe { WlanScan(client.0, &guid, None, None, None) };
    win32("WlanScan", status)
}

/// Lists the networks the driver currently knows about.
///
/// Fails with [`Error::Win32`] code 5 when precise-location consent is missing;
/// that is the 24H2 gate, not a missing adapter.
pub fn networks() -> Result<Vec<Network>> {
    let client = Client::open()?;
    let (guid, _) = first_interface(&client)?;
    let mut list: *mut WLAN_AVAILABLE_NETWORK_LIST = null_mut();
    // Flag 0: only networks the driver can actually see, and one entry per
    // visible SSID rather than one per profile.
    let status = unsafe { WlanGetAvailableNetworkList(client.0, &guid, 0, None, &mut list) };
    win32("WlanGetAvailableNetworkList", status)?;
    let _owned = WlanBuffer(list);
    if list.is_null() {
        return Ok(Vec::new());
    }
    // SAFETY: as in first_interface — dwNumberOfItems describes the real length.
    let entries = unsafe {
        let count = (*list).dwNumberOfItems as usize;
        std::slice::from_raw_parts((*list).Network.as_ptr(), count)
    };

    // Both read from the service rather than being inferred from the scan list:
    // getting "saved" wrong is what makes the UI ask for a password it already
    // has, and then overwrite the stored one.
    let saved_profiles = profile_names(&client, &guid).unwrap_or_default();
    let connected = current_ssid(&client, &guid);

    let mut networks: Vec<Network> = Vec::with_capacity(entries.len());
    for entry in entries {
        let ssid = decode_ssid(&entry.dot11Ssid.ucSSID, entry.dot11Ssid.uSSIDLength as usize);
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
        let saved = entry.strProfileName[0] != 0 || saved_profiles.contains(&ssid);
        let is_connected = connected.as_deref() == Some(ssid.as_str());
        // The list can carry the same SSID more than once (different PHY types);
        // keep the strongest, and let any saved entry win the saved flag.
        if let Some(existing) = networks.iter_mut().find(|n| n.ssid == ssid) {
            existing.signal = existing.signal.max(entry.wlanSignalQuality);
            existing.saved |= saved;
            existing.connectable |= entry.bNetworkConnectable.as_bool();
            continue;
        }
        networks.push(Network {
            ssid,
            signal: entry.wlanSignalQuality,
            security,
            saved,
            connectable: entry.bNetworkConnectable.as_bool(),
            connected: is_connected,
        });
    }
    networks.sort_by(|a, b| b.signal.cmp(&a.signal).then_with(|| a.ssid.cmp(&b.ssid)));
    Ok(networks)
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
        a if a == DOT11_AUTH_ALGO_80211_OPEN.0 || a == DOT11_AUTH_ALGO_OWE.0 => Security::Open,
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
    let (guid, _) = first_interface(&client)?;

    // The profile Windows would use for this network, which is NOT always named
    // after the SSID — see profile_for_ssid.
    let existing = profile_for_ssid(&client, &guid, ssid);

    // A profile is authored ONLY when the caller supplied a passphrase. Writing
    // one for a network that already has a saved profile would overwrite it —
    // and with no passphrase to put in it, an open-network profile would replace
    // the user's stored credentials and destroy them. Joining a saved network
    // means connecting to the profile that is already there, untouched.
    match passphrase {
        Some(pass) => {
            // Prefer WPA3 transition mode so one profile covers WPA3-Personal
            // and WPA2-PSK alike, falling back when the adapter rejects it.
            let attempts = [
                profile::psk_profile(ssid, pass, true),
                profile::psk_profile(ssid, pass, false),
            ];
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
        }
        None => {
            // No passphrase: only ever create a profile when the network has
            // none at all AND needs no key. Anything else is either already
            // joinable or genuinely missing a password.
            if existing.is_none() {
                set_profile(&client, &guid, &profile::open_profile(ssid))?;
            }
        }
    }

    // Connect by the profile's real name. Using the SSID here is what left a
    // network stuck on "saved": the profile was called "<SSID> 2" and nothing
    // matched. After authoring a profile ourselves the name IS the SSID,
    // because that is what we wrote into it.
    let profile_name = match passphrase {
        Some(_) => ssid.to_owned(),
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
    let status = unsafe { WlanConnect(client.0, &guid, &parameters, None) };
    win32("WlanConnect", status)
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

/// Disconnects the Wi-Fi interface.
pub fn disconnect() -> Result<()> {
    let client = Client::open()?;
    let (guid, _) = first_interface(&client)?;
    let status = unsafe { WlanDisconnect(client.0, &guid, None) };
    win32("WlanDisconnect", status)
}

/// Removes a saved profile, so the network stops joining automatically.
pub fn forget(ssid: &str) -> Result<()> {
    let client = Client::open()?;
    let (guid, _) = first_interface(&client)?;
    // Deletes every profile that joins this network, not just one named after
    // it: a duplicate like "<SSID> 2" would otherwise survive a Forget and keep
    // rejoining, which looks exactly like Forget having done nothing.
    let mut names: Vec<String> = profile_names(&client, &guid)?
        .into_iter()
        .filter(|name| name == ssid)
        .collect();
    if let Some(bound) = profile_for_ssid(&client, &guid, ssid)
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
mod tests {
    use super::*;

    #[test]
    fn an_unsecured_network_is_open_whatever_the_algorithm_says() {
        assert_eq!(classify(false, DOT11_AUTH_ALGO_RSNA_PSK.0), Security::Open);
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
