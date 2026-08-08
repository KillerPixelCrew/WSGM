//! WLAN reason codes, and the one question the UI actually needs answered:
//! was the password wrong?
//!
//! Windows never says "wrong password" directly. It reports a reason code, and
//! only a handful of them mean the pre-shared key failed. Everything else —
//! association timeouts, the AP disappearing — must not be blamed on the user's
//! typing, because re-prompting for a password that was correct is worse than
//! saying the network could not be reached.

use windows::Win32::NetworkManagement::WiFi::WlanReasonCodeToString;

/// What a connection failure should tell the user to do.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Verdict {
    /// The key was rejected. Ask for the password again.
    WrongPassword,

    /// The profile itself was malformed — wrong key length, or an
    /// authentication type this adapter cannot do.
    BadProfile,

    /// The network could not be reached. Retrying is reasonable; re-prompting
    /// for the password is not.
    Unreachable,

    /// Nothing went wrong.
    Success,

    /// A failure with no specific guidance.
    Unknown,
}

// From the documented WLAN_REASON_CODE ranges. The MSMSEC block is the one that
// carries pre-shared-key failures.
const MSMSEC_BASE: u32 = 229376; // WLAN_REASON_CODE_MSMSEC_BASE
const MSM_BASE: u32 = 163840; // WLAN_REASON_CODE_MSM_BASE
const AC_BASE: u32 = 131072; // WLAN_REASON_CODE_AC_BASE

/// The key was tried and rejected.
pub const MSMSEC_PSK_MISMATCH_SUSPECTED: u32 = MSMSEC_BASE + 82;
/// The security handshake produced mismatched keys.
pub const MSMSEC_KEY_MISMATCH: u32 = MSMSEC_BASE + 71;
/// The 4-way handshake never finished; overwhelmingly a wrong key.
pub const MSMSEC_M3_MISSING_KEY_DATA: u32 = MSMSEC_BASE + 63;
/// The handshake timed out waiting for the key exchange to start.
pub const MSMSEC_KEY_START_TIMEOUT: u32 = MSMSEC_BASE + 54;
/// The handshake timed out partway through the key exchange.
pub const MSMSEC_KEY_SUCCESS_TIMEOUT: u32 = MSMSEC_BASE + 55;

/// The profile's pre-shared key was not a legal length.
pub const MSMSEC_PROFILE_PSK_LENGTH: u32 = MSMSEC_BASE + 16;
/// The profile's passphrase contained an illegal character.
pub const MSMSEC_PROFILE_PASSPHRASE_CHAR: u32 = MSMSEC_BASE + 18;
/// The profile named the wrong key type for its authentication mode.
pub const MSMSEC_PROFILE_WRONG_KEYTYPE: u32 = MSMSEC_BASE + 12;

/// Association failed outright.
pub const MSM_ASSOCIATION_FAILURE: u32 = MSM_BASE + 6;
/// Association timed out.
pub const MSM_ASSOCIATION_TIMEOUT: u32 = MSM_BASE + 7;
/// The network was not there to join.
pub const AC_NETWORK_NOT_AVAILABLE: u32 = AC_BASE + 9;
/// The network is on the automatic-connect blocklist.
pub const AC_PROFILE_NOT_ALLOWED: u32 = AC_BASE + 6;

/// Classifies a reason code into the advice the UI should give.
#[must_use]
pub fn verdict(code: u32) -> Verdict {
    match code {
        0 => Verdict::Success,
        MSMSEC_PSK_MISMATCH_SUSPECTED
        | MSMSEC_KEY_MISMATCH
        | MSMSEC_M3_MISSING_KEY_DATA
        | MSMSEC_KEY_START_TIMEOUT
        | MSMSEC_KEY_SUCCESS_TIMEOUT => Verdict::WrongPassword,
        MSMSEC_PROFILE_PSK_LENGTH | MSMSEC_PROFILE_PASSPHRASE_CHAR | MSMSEC_PROFILE_WRONG_KEYTYPE => {
            Verdict::BadProfile
        }
        MSM_ASSOCIATION_FAILURE | MSM_ASSOCIATION_TIMEOUT | AC_NETWORK_NOT_AVAILABLE
        | AC_PROFILE_NOT_ALLOWED => Verdict::Unreachable,
        _ => Verdict::Unknown,
    }
}

/// Asks Windows for the localised text of a reason code.
///
/// Deliberately not a hard-coded table: the strings are translated, and this
/// panel is the only place the user can read them.
#[must_use]
pub fn describe(code: u32) -> String {
    // The documented buffer size for this call is 1024 characters.
    //
    // Heap-allocated rather than a stack array on purpose: the generated binding
    // takes a shared slice and casts it to a mutable pointer for the FFI call, so
    // a local array could in principle be assumed unchanged across it. A `Vec`'s
    // buffer escapes through the pointer, which it cannot.
    let buffer = vec![0u16; 1024];
    let status = unsafe { WlanReasonCodeToString(code, &buffer, None) };
    if status != 0 {
        return format!("Wi-Fi reason code {code}");
    }
    let end = buffer.iter().position(|&c| c == 0).unwrap_or(buffer.len());
    let text = String::from_utf16_lossy(&buffer[..end]);
    let text = text.trim();
    if text.is_empty() {
        format!("Wi-Fi reason code {code}")
    } else {
        text.to_owned()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_psk_mismatch_code_is_the_one_that_re_prompts_for_a_password() {
        assert_eq!(verdict(MSMSEC_PSK_MISMATCH_SUSPECTED), Verdict::WrongPassword);
        assert_eq!(verdict(MSMSEC_KEY_MISMATCH), Verdict::WrongPassword);
    }

    #[test]
    fn an_unreachable_network_never_blames_the_password() {
        // Re-prompting here would be wrong: the key was never tried.
        assert_eq!(verdict(MSM_ASSOCIATION_TIMEOUT), Verdict::Unreachable);
        assert_eq!(verdict(AC_NETWORK_NOT_AVAILABLE), Verdict::Unreachable);
        assert_ne!(verdict(MSM_ASSOCIATION_FAILURE), Verdict::WrongPassword);
    }

    #[test]
    fn profile_authoring_faults_are_distinct_from_a_rejected_key() {
        assert_eq!(verdict(MSMSEC_PROFILE_PSK_LENGTH), Verdict::BadProfile);
        assert_eq!(verdict(MSMSEC_PROFILE_PASSPHRASE_CHAR), Verdict::BadProfile);
    }

    #[test]
    fn zero_is_success_and_anything_unmapped_is_unknown() {
        assert_eq!(verdict(0), Verdict::Success);
        assert_eq!(verdict(1), Verdict::Unknown);
    }

    #[test]
    fn describe_always_produces_something_printable() {
        // Whatever Windows returns, the UI must never show an empty line.
        assert!(!describe(MSMSEC_PSK_MISMATCH_SUSPECTED).is_empty());
        assert!(!describe(0xDEAD_BEEF).is_empty());
    }
}
