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

pub mod profile;
pub mod reason;

use std::ffi::c_void;
use std::ptr::null_mut;

use windows::Win32::Foundation::{ERROR_SUCCESS, HANDLE};
use windows::Win32::NetworkManagement::WiFi::{
    DOT11_AUTH_ALGO_80211_OPEN, DOT11_AUTH_ALGO_OWE, DOT11_AUTH_ALGO_RSNA,
    DOT11_AUTH_ALGO_RSNA_PSK, DOT11_AUTH_ALGO_WPA, DOT11_AUTH_ALGO_WPA3,
    DOT11_AUTH_ALGO_WPA3_ENT, DOT11_AUTH_ALGO_WPA3_ENT_192, DOT11_AUTH_ALGO_WPA3_SAE,
    DOT11_AUTH_ALGO_WPA_PSK, WLAN_AVAILABLE_NETWORK_LIST,
    WLAN_CONNECTION_PARAMETERS, WLAN_INTERFACE_INFO_LIST, WLAN_INTERFACE_STATE,
    WlanCloseHandle, WlanConnect, WlanDeleteProfile, WlanDisconnect, WlanEnumInterfaces,
    WlanFreeMemory, WlanGetAvailableNetworkList, WlanOpenHandle, WlanScan, WlanSetProfile,
    dot11_BSS_type_infrastructure,
    wlan_connection_mode_profile,
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
        let saved = entry.strProfileName[0] != 0;
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

    // Author the profile, preferring WPA3 transition mode so one profile covers
    // both WPA3-Personal and WPA2-PSK, and falling back when it is rejected.
    let mut last: Option<Error> = None;
    let attempts: Vec<String> = match passphrase {
        None => vec![profile::open_profile(ssid)],
        Some(pass) => vec![
            profile::psk_profile(ssid, pass, true),
            profile::psk_profile(ssid, pass, false),
        ],
    };
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
        return Err(last.unwrap_or(Error::InvalidArgument("profile could not be installed")));
    }

    let name = wide(ssid);
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
    let name = wide(ssid);
    let status = unsafe { WlanDeleteProfile(client.0, &guid, PCWSTR(name.as_ptr()), None) };
    win32("WlanDeleteProfile", status)
}

/// Unused placeholder to keep the c_void import honest for future notification
/// work; removed once the notification registration lands.
#[allow(dead_code)]
fn _unused(_: *mut c_void) {}

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
