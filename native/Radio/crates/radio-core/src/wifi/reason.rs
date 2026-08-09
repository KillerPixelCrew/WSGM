//! WLAN reason codes, and the one question the UI actually needs answered:
//! was the password wrong?
//!
//! Windows never says "wrong password" directly. It reports a reason code, and
//! only a handful of them mean the pre-shared key failed. Everything else —
//! association timeouts, the AP disappearing — must not be blamed on the user's
//! typing, because re-prompting for a password that was correct is worse than
//! saying the network could not be reached.

// Declared here rather than taken from the `windows` crate: its generated
// binding takes the output buffer as a SHARED slice and transmutes the pointer
// for the call. Windows writes through it, which breaks Rust's aliasing
// contract on a `&` reference no matter where the storage lives, and is exactly
// the kind of thing the optimiser is allowed to miscompile. raw-dylib matches
// how the crate links the rest of wlanapi, so this needs no import library.
#[link(name = "wlanapi", kind = "raw-dylib")]
unsafe extern "system" {
    fn WlanReasonCodeToString(
        reason_code: u32,
        buffer_size: u32,
        buffer: *mut u16,
        reserved: *mut core::ffi::c_void,
    ) -> u32;
}

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

// Taken from the crate's own WLAN_REASON_CODE definitions, never computed from
// a base plus an offset. The hand-derived versions were wrong in a way nothing
// could notice: the value used as MSMSEC_BASE was in fact MSM_CONNECT_BASE, so
// every constant below missed its real code, no failure ever classified as a
// wrong password, and the profile rollback that depends on that verdict was
// unreachable. A mistyped password therefore looked like an unexplained failure
// AND left its bad profile saved.
use windows::Win32::NetworkManagement::WiFi::{
    WLAN_REASON_CODE_AC_BASE, WLAN_REASON_CODE_AC_END, WLAN_REASON_CODE_MSM_BASE,
    WLAN_REASON_CODE_MSM_END, WLAN_REASON_CODE_MSMSEC_BASE, WLAN_REASON_CODE_MSMSEC_CONNECT_BASE,
    WLAN_REASON_CODE_MSMSEC_DOWNGRADE_DETECTED, WLAN_REASON_CODE_MSMSEC_END,
    WLAN_REASON_CODE_MSMSEC_KEY_FORMAT, WLAN_REASON_CODE_MSMSEC_KEY_START_TIMEOUT,
    WLAN_REASON_CODE_MSMSEC_KEY_SUCCESS_TIMEOUT, WLAN_REASON_CODE_MSMSEC_M2_MISSING_KEY_DATA,
    WLAN_REASON_CODE_MSMSEC_M3_MISSING_IE, WLAN_REASON_CODE_MSMSEC_M3_MISSING_KEY_DATA,
    WLAN_REASON_CODE_MSMSEC_PROFILE_PASSPHRASE_CHAR, WLAN_REASON_CODE_MSMSEC_PROFILE_PSK_LENGTH,
    WLAN_REASON_CODE_MSMSEC_PROFILE_WRONG_KEYTYPE, WLAN_REASON_CODE_MSMSEC_PSK_MISMATCH_SUSPECTED,
    WLAN_REASON_CODE_NETWORK_NOT_AVAILABLE, WLAN_REASON_CODE_NO_AUTO_CONNECTION,
};

/// The key was tried and rejected. Re-exported under a short name because it is
/// the one code the tests and the ABI care about by name.
pub const MSMSEC_PSK_MISMATCH_SUSPECTED: u32 = WLAN_REASON_CODE_MSMSEC_PSK_MISMATCH_SUSPECTED;

/// Classifies a reason code into the advice the UI should give.
#[must_use]
pub fn verdict(code: u32) -> Verdict {
    match code {
        0 => Verdict::Success,
        // The key exchange failed, which is what a mistyped password looks
        // like from here.
        WLAN_REASON_CODE_MSMSEC_PSK_MISMATCH_SUSPECTED
        | WLAN_REASON_CODE_MSMSEC_KEY_FORMAT
        | WLAN_REASON_CODE_MSMSEC_DOWNGRADE_DETECTED
        | WLAN_REASON_CODE_MSMSEC_M3_MISSING_KEY_DATA
        | WLAN_REASON_CODE_MSMSEC_M3_MISSING_IE
        | WLAN_REASON_CODE_MSMSEC_M2_MISSING_KEY_DATA
        | WLAN_REASON_CODE_MSMSEC_KEY_START_TIMEOUT
        | WLAN_REASON_CODE_MSMSEC_KEY_SUCCESS_TIMEOUT => Verdict::WrongPassword,
        // The profile document itself was rejected.
        WLAN_REASON_CODE_MSMSEC_PROFILE_PSK_LENGTH
        | WLAN_REASON_CODE_MSMSEC_PROFILE_PASSPHRASE_CHAR
        | WLAN_REASON_CODE_MSMSEC_PROFILE_WRONG_KEYTYPE => Verdict::BadProfile,
        WLAN_REASON_CODE_NETWORK_NOT_AVAILABLE | WLAN_REASON_CODE_NO_AUTO_CONNECTION => {
            Verdict::Unreachable
        }
        // Range fallbacks, so a code Windows names but this list does not still
        // lands in the right family instead of "unknown". The MSMSEC block
        // splits at its connect base: below it is profile validation, above it
        // is the security handshake.
        c if (WLAN_REASON_CODE_MSMSEC_BASE..WLAN_REASON_CODE_MSMSEC_CONNECT_BASE).contains(&c) => {
            Verdict::BadProfile
        }
        c if (WLAN_REASON_CODE_MSMSEC_CONNECT_BASE..=WLAN_REASON_CODE_MSMSEC_END).contains(&c) => {
            Verdict::WrongPassword
        }
        // Association and auto-config failures are reachability problems, and
        // must never be blamed on the user's typing.
        c if (WLAN_REASON_CODE_MSM_BASE..=WLAN_REASON_CODE_MSM_END).contains(&c) => {
            Verdict::Unreachable
        }
        c if (WLAN_REASON_CODE_AC_BASE..=WLAN_REASON_CODE_AC_END).contains(&c) => {
            Verdict::Unreachable
        }
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
    let mut buffer = vec![0u16; 1024];
    // SAFETY: an exclusive pointer to storage of exactly the length declared,
    // which is what this API's contract asks for.
    let status = unsafe {
        WlanReasonCodeToString(
            code,
            buffer.len() as u32,
            buffer.as_mut_ptr(),
            std::ptr::null_mut(),
        )
    };
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
        assert_eq!(
            verdict(WLAN_REASON_CODE_MSMSEC_M3_MISSING_KEY_DATA),
            Verdict::WrongPassword
        );
    }

    #[test]
    fn the_constants_are_the_ones_wlanapi_actually_defines() {
        // Pinned against the real values: the previous set was derived from a
        // base that was actually MSM_CONNECT_BASE, so every code missed, no
        // failure ever read as a wrong password, and the profile rollback that
        // hangs off that verdict could never run.
        assert_eq!(MSMSEC_PSK_MISMATCH_SUSPECTED, 294932);
        assert_eq!(WLAN_REASON_CODE_MSMSEC_BASE, 262144);
        assert_eq!(WLAN_REASON_CODE_MSM_BASE, 196608);
        assert_eq!(WLAN_REASON_CODE_AC_BASE, 131072);
    }

    #[test]
    fn an_unreachable_network_never_blames_the_password() {
        // Re-prompting here would be wrong: the key was never tried.
        assert_eq!(
            verdict(WLAN_REASON_CODE_NETWORK_NOT_AVAILABLE),
            Verdict::Unreachable
        );
        assert_eq!(
            verdict(WLAN_REASON_CODE_NO_AUTO_CONNECTION),
            Verdict::Unreachable
        );
        // Anything in the association block is reachability, not typing.
        assert_ne!(
            verdict(WLAN_REASON_CODE_MSM_BASE + 6),
            Verdict::WrongPassword
        );
    }

    #[test]
    fn profile_authoring_faults_are_distinct_from_a_rejected_key() {
        assert_eq!(
            verdict(WLAN_REASON_CODE_MSMSEC_PROFILE_PSK_LENGTH),
            Verdict::BadProfile
        );
        assert_eq!(
            verdict(WLAN_REASON_CODE_MSMSEC_PROFILE_PASSPHRASE_CHAR),
            Verdict::BadProfile
        );
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
