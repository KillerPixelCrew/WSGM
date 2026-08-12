//! Reads the privacy consent values that silently gate this subsystem.
//!
//! Two of them decide whether the feature works at all:
//!
//! * `location` — since Windows 11 24H2, without precise-location consent the
//!   WLAN scan and current-connection entry points return
//!   `ERROR_ACCESS_DENIED` for any app, packaged or not.
//! * `radios` — documented as gating `Radio.SetStateAsync`.
//!
//! Treat both as *diagnostics only*, never as a predicate to skip a call. On a
//! Windows 11 25H2 test machine this store read `radios = Deny` for both user
//! and machine while `Radio.RequestAccessAsync` still returned `Allowed`, so
//! for an unpackaged process the value plainly does not decide the outcome.
//! Ask the API what it permits; read these only to explain a refusal afterwards.
//!
//! Strictly read-only. There is no supported way to grant either of these
//! programmatically, and writing them behind the user's back would be exactly
//! the kind of consent bypass the settings exist to prevent.

use windows::Win32::Foundation::{ERROR_FILE_NOT_FOUND, ERROR_SUCCESS};
use windows::Win32::System::Registry::{
    HKEY, HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE, KEY_READ, RRF_RT_REG_SZ, RegCloseKey,
    RegGetValueW, RegOpenKeyExW,
};
use windows_core::PCWSTR;

/// What a consent store says about one capability.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Consent {
    /// Explicitly allowed.
    Allow,
    /// Explicitly denied. The related feature will fail.
    Deny,
    /// No value present, which Windows treats as its own default.
    Unset,
    /// The value could not be read.
    Unknown,
}

fn wide(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(std::iter::once(0)).collect()
}

fn read(root: HKEY, capability: &str) -> Consent {
    let path = wide(&format!(
        "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\CapabilityAccessManager\\ConsentStore\\{capability}"
    ));
    let mut key = HKEY::default();
    let opened =
        unsafe { RegOpenKeyExW(root, PCWSTR(path.as_ptr()), Some(0), KEY_READ, &mut key) };
    if opened != ERROR_SUCCESS {
        // Only an absent key is "no value present". Access denied or any other
        // status means the store could not be read, and reporting that as the
        // Windows default would send a remote diagnosis down the wrong path.
        return if opened == ERROR_FILE_NOT_FOUND {
            Consent::Unset
        } else {
            Consent::Unknown
        };
    }
    let name = wide("Value");
    let mut buffer = [0u16; 64];
    let mut size = (buffer.len() * 2) as u32;
    let status = unsafe {
        RegGetValueW(
            key,
            PCWSTR::null(),
            PCWSTR(name.as_ptr()),
            RRF_RT_REG_SZ,
            None,
            Some(buffer.as_mut_ptr().cast()),
            Some(&mut size),
        )
    };
    unsafe {
        let _ = RegCloseKey(key);
    }
    if status != ERROR_SUCCESS {
        // Same distinction as above: a missing value is the documented default,
        // while ERROR_MORE_DATA (a value longer than the buffer) or a denied
        // read is a state this module cannot describe.
        return if status == ERROR_FILE_NOT_FOUND {
            Consent::Unset
        } else {
            Consent::Unknown
        };
    }
    let end = buffer.iter().position(|&c| c == 0).unwrap_or(buffer.len());
    match String::from_utf16_lossy(&buffer[..end]).trim() {
        v if v.eq_ignore_ascii_case("Allow") => Consent::Allow,
        v if v.eq_ignore_ascii_case("Deny") => Consent::Deny,
        "" => Consent::Unset,
        _ => Consent::Unknown,
    }
}

/// Reads a capability's consent for the current user and for the machine.
///
/// Returned in that order because the per-user value is the one that decides
/// for an interactive process when both are present.
#[must_use]
pub fn capability(name: &str) -> (Consent, Consent) {
    (
        read(HKEY_CURRENT_USER, name),
        read(HKEY_LOCAL_MACHINE, name),
    )
}

/// Consent for precise location, which gates the Wi-Fi scan on 24H2 and later.
#[must_use]
pub fn location() -> (Consent, Consent) {
    capability("location")
}

/// Consent for radio control, which gates turning radios on and off.
#[must_use]
pub fn radios() -> (Consent, Consent) {
    capability("radios")
}
