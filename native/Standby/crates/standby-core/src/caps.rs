//! System sleep-capability probe.
//!
//! This is the question every other decision hangs off: does the device do
//! Modern Standby (S0 low-power idle), does it still expose a real S3, and is
//! there a hibernation file to fall back to?

use windows_sys::Win32::System::Power::{GetPwrCapabilities, SYSTEM_POWER_CAPABILITIES};

use crate::error::{Error, Result, last_win32};

/// What the platform reports it can do.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct Capabilities {
    /// Legacy standby state S1 is available.
    pub s1: bool,
    /// Legacy standby state S2 is available.
    pub s2: bool,
    /// Traditional suspend-to-RAM (S3) is available.
    pub s3: bool,
    /// Suspend-to-disk (S4, hibernate) is available.
    pub s4: bool,
    /// A hibernation file exists, so S4 can actually be entered.
    pub hiber_file_present: bool,
    /// The platform is an "always on, always connected" (Modern Standby) system.
    ///
    /// When this is true and [`Capabilities::s3`] is false — the usual state on
    /// a modern handheld — there is no traditional sleep to fall back to and
    /// the only lever left is reducing what wakes the device.
    pub modern_standby: bool,
    /// Modern Standby maintains network connectivity while idle.
    pub modern_standby_connectivity: bool,
    /// A programmable wake alarm is present.
    pub wake_alarm_present: bool,
    /// Fast S4 ("hybrid boot") is available.
    pub fast_s4: bool,
    /// Hiberboot (fast startup) is enabled.
    pub hiberboot: bool,
}

impl Capabilities {
    /// True when [`crate::sleep::try_traditional_sleep`] has a state to target.
    ///
    /// Modern Standby and S3 are not mutually exclusive in the reported
    /// capabilities: some firmware advertises both, which is exactly the case
    /// where forcing S3 is worth attempting.
    pub fn can_attempt_traditional_sleep(self) -> bool {
        self.s3 || self.s2 || self.s1
    }

    /// True when the device is a Modern Standby system with no S3 escape hatch.
    pub fn is_modern_standby_only(self) -> bool {
        self.modern_standby && !self.s3
    }
}

/// Reads the platform's power capabilities.
pub fn capabilities() -> Result<Capabilities> {
    // SAFETY: the struct is fully owned here and GetPwrCapabilities only writes
    // into it. All fields are plain data with no invalid bit patterns for the
    // booleans we read (Windows writes 0 or 1).
    let mut raw: SYSTEM_POWER_CAPABILITIES = unsafe { std::mem::zeroed() };
    let ok = unsafe { GetPwrCapabilities(&mut raw) };
    if !ok {
        return Err(last_win32("GetPwrCapabilities"));
    }

    Ok(Capabilities {
        s1: raw.SystemS1,
        s2: raw.SystemS2,
        s3: raw.SystemS3,
        s4: raw.SystemS4,
        hiber_file_present: raw.HiberFilePresent,
        modern_standby: raw.AoAc,
        modern_standby_connectivity: raw.AoAcConnectivitySupported,
        wake_alarm_present: raw.WakeAlarmPresent,
        fast_s4: raw.FastSystemS4,
        hiberboot: raw.Hiberboot,
    })
}

/// Returns the capability probe, mapping a total failure to a typed error so
/// callers can distinguish "probe failed" from "device reports nothing".
pub fn require_capabilities() -> Result<Capabilities> {
    let caps = capabilities()?;
    if !caps.s1 && !caps.s2 && !caps.s3 && !caps.s4 && !caps.modern_standby {
        return Err(Error::Unsupported("no sleep state reported by the platform"));
    }
    Ok(caps)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn modern_standby_only_requires_absent_s3() {
        let ms_only = Capabilities {
            modern_standby: true,
            s3: false,
            ..Default::default()
        };
        assert!(ms_only.is_modern_standby_only());
        assert!(!ms_only.can_attempt_traditional_sleep());

        let both = Capabilities {
            modern_standby: true,
            s3: true,
            ..Default::default()
        };
        assert!(!both.is_modern_standby_only());
        assert!(both.can_attempt_traditional_sleep());
    }

    #[test]
    fn legacy_states_also_allow_a_traditional_attempt() {
        let s1_only = Capabilities {
            s1: true,
            ..Default::default()
        };
        assert!(s1_only.can_attempt_traditional_sleep());
    }
}
