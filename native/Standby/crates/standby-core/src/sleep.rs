//! Entering sleep, and the honest limits of forcing a traditional one.
//!
//! A caveat worth stating plainly, because it is the single most common wrong
//! assumption about "fixing" Modern Standby: on a platform that reports
//! `AoAc`, [`SetSuspendState`] enters S0 low-power idle. It does not enter S3,
//! and no user-mode API makes it. Whether S3 is reachable at all is decided by
//! firmware and by the `PlatformAoAcOverride` policy, which takes effect only
//! after a reboot — and on handhelds whose firmware never implemented S3,
//! setting it leaves a device that cannot sleep properly at all.
//!
//! So this module attempts a traditional sleep where the platform says one
//! exists, reports precisely what it could and could not do, and deliberately
//! does not flip `PlatformAoAcOverride` on the caller's behalf.

use windows_sys::Win32::System::Power::{IsSystemResumeAutomatic, SetSuspendState};

use crate::caps::{Capabilities, capabilities};
use crate::error::{Result, last_win32};

/// What a sleep request actually achieved.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SleepOutcome {
    /// The request was issued and the platform has a traditional sleep state to
    /// honour it with.
    Traditional,
    /// The request was issued, but the platform is Modern Standby only, so it
    /// entered S0 low-power idle regardless of what was asked for.
    ModernStandby,
    /// The platform reports no usable sleep state; nothing was attempted.
    Unavailable,
}

impl SleepOutcome {
    /// Whether a sleep request was actually issued to the system.
    pub fn was_issued(self) -> bool {
        matches!(self, SleepOutcome::Traditional | SleepOutcome::ModernStandby)
    }
}

/// Requests that the system suspend.
///
/// `hibernate` selects S4 over sleep. `force` is passed through to
/// `SetSuspendState`; Windows has ignored it since XP but the parameter is
/// still part of the documented signature.
///
/// Wake events are never disabled: passing `bWakeUpEventsDisabled` would leave
/// a handheld that cannot be woken by its own power button.
pub fn suspend(hibernate: bool, force: bool) -> Result<()> {
    // SAFETY: no pointers are involved and the call has no preconditions.
    let ok = unsafe { SetSuspendState(hibernate, force, false) };
    if ok {
        Ok(())
    } else {
        Err(last_win32("SetSuspendState"))
    }
}

/// Attempts a traditional (S1/S2/S3) sleep, reporting what the platform will
/// actually do rather than assuming the request was honoured.
///
/// On a Modern-Standby-only device this still suspends — there is no better
/// option available — but returns [`SleepOutcome::ModernStandby`] so the caller
/// can be honest with the user instead of claiming an S3 that never happened.
pub fn try_traditional_sleep() -> Result<SleepOutcome> {
    let caps = capabilities()?;
    let outcome = classify(caps);

    if outcome.was_issued() {
        suspend(false, false)?;
    }

    Ok(outcome)
}

/// Decides what a sleep request can achieve on a platform with `caps`.
fn classify(caps: Capabilities) -> SleepOutcome {
    if caps.can_attempt_traditional_sleep() {
        SleepOutcome::Traditional
    } else if caps.modern_standby {
        SleepOutcome::ModernStandby
    } else {
        SleepOutcome::Unavailable
    }
}

/// Whether the most recent resume was unattended rather than user-initiated.
///
/// This is the cleanest available answer to "did the user wake this device, or
/// did something else?" — it comes straight from the power manager and needs
/// no event-log subscription, no localised parsing, and no per-source table of
/// wake reasons to keep up to date.
///
/// It answers a narrower question than a wake-source lookup does: it says
/// whether the resume was automatic, not what caused it. Pair it with
/// [`crate::events`] when the specific source matters.
pub fn was_resume_automatic() -> bool {
    // SAFETY: no preconditions; returns a BOOL.
    unsafe { IsSystemResumeAutomatic() != 0 }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn caps(s3: bool, modern_standby: bool) -> Capabilities {
        Capabilities {
            s3,
            modern_standby,
            ..Default::default()
        }
    }

    #[test]
    fn a_device_with_s3_reports_a_traditional_sleep() {
        assert_eq!(classify(caps(true, false)), SleepOutcome::Traditional);
    }

    #[test]
    fn s3_alongside_modern_standby_still_counts_as_traditional() {
        // Firmware that advertises both is exactly the case worth attempting.
        assert_eq!(classify(caps(true, true)), SleepOutcome::Traditional);
    }

    #[test]
    fn a_modern_standby_only_device_is_reported_as_such_not_as_success() {
        assert_eq!(classify(caps(false, true)), SleepOutcome::ModernStandby);
    }

    #[test]
    fn a_device_with_no_sleep_state_attempts_nothing() {
        let outcome = classify(caps(false, false));
        assert_eq!(outcome, SleepOutcome::Unavailable);
        assert!(!outcome.was_issued());
    }
}
