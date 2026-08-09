//! Radio power state via WinRT `Windows.Devices.Radios`.
//!
//! This is the only documented way to turn a radio itself on or off, as opposed
//! to disabling the network adapter. It works from an unpackaged Win32 process —
//! Chromium drives Bluetooth exactly this way — with two caveats worth knowing
//! when a call comes back empty or denied:
//!
//! * Enumeration returns radios only when the process architecture matches the
//!   OS architecture, so WSGM must stay per-RID and never build AnyCPU.
//! * `SetStateAsync` is gated by the "Allow apps to control device radios"
//!   privacy setting. Reading state and enumerating are never gated.

use std::sync::{Mutex, OnceLock};
use std::time::{Duration, Instant};

use windows::Devices::Radios::{
    Radio, RadioAccessStatus, RadioKind as WinRtRadioKind, RadioState as WinRtRadioState,
};

use crate::error::{Result, winrt};
use crate::mta::on_mta;

/// Which radio an operation refers to.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RadioKind {
    /// The Wi-Fi radio.
    WiFi,
    /// The Bluetooth radio.
    Bluetooth,
}

impl RadioKind {
    fn matches(self, kind: WinRtRadioKind) -> bool {
        match self {
            Self::WiFi => kind == WinRtRadioKind::WiFi,
            Self::Bluetooth => kind == WinRtRadioKind::Bluetooth,
        }
    }

    /// The name used in log lines.
    #[must_use]
    pub fn label(self) -> &'static str {
        match self {
            Self::WiFi => "Wi-Fi",
            Self::Bluetooth => "Bluetooth",
        }
    }
}

/// The power state of a radio, plus the two "we could not tell" outcomes that
/// the UI must render as neutral rather than as off.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RadioPower {
    /// The radio is on.
    On,
    /// The radio is off, but present and switchable.
    Off,
    /// Present but disabled by policy or hardware switch; not switchable.
    Disabled,
    /// State could not be determined.
    Unknown,
    /// No radio of this kind exists on this machine.
    Absent,
}

impl RadioPower {
    fn from_winrt(state: WinRtRadioState) -> Self {
        match state {
            WinRtRadioState::On => Self::On,
            WinRtRadioState::Off => Self::Off,
            WinRtRadioState::Disabled => Self::Disabled,
            _ => Self::Unknown,
        }
    }
}

/// Whether this process may change radio state.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RadioAccess {
    /// State changes are permitted.
    Allowed,
    /// The user denied radio control in the privacy settings.
    DeniedByUser,
    /// Policy or the system denied radio control.
    DeniedBySystem,
    /// The request produced an unrecognised status.
    Unspecified,
}

impl RadioAccess {
    fn from_winrt(status: RadioAccessStatus) -> Self {
        match status {
            RadioAccessStatus::Allowed => Self::Allowed,
            RadioAccessStatus::DeniedByUser => Self::DeniedByUser,
            RadioAccessStatus::DeniedBySystem => Self::DeniedBySystem,
            _ => Self::Unspecified,
        }
    }

    /// The wording the UI shows when a toggle is refused.
    #[must_use]
    pub fn describe(self) -> &'static str {
        match self {
            Self::Allowed => "allowed",
            Self::DeniedByUser => {
                "denied by the user (Settings > Privacy & security > Radios)"
            }
            Self::DeniedBySystem => "denied by the system or by policy",
            Self::Unspecified => "unspecified",
        }
    }
}

fn all_radios() -> Result<Vec<Radio>> {
    let operation = Radio::GetRadiosAsync().map_err(|e| winrt("Radio.GetRadiosAsync", e))?;
    let list = operation
        .join()
        .map_err(|e| winrt("Radio.GetRadiosAsync (join)", e))?;
    let mut found = Vec::new();
    for radio in &list {
        found.push(radio);
    }
    Ok(found)
}

/// Cached `Radio` objects, keyed by kind.
///
/// `GetRadiosAsync` is an enumeration across every radio driver on the machine
/// and costs seconds on some hardware — far too slow to repeat on every status
/// tick, which is what made the panel take about ten seconds to show anything.
/// The objects themselves are long-lived and agile, and `State()` on a cached
/// one reflects the live value, so only the lookup is cached, never the state.
/// The cached enumeration, with when it was taken. `None` means never
/// enumerated — distinct from "enumerated and this machine has no such radio",
/// which is a real answer worth keeping: without that distinction every status
/// tick re-ran the multi-second `GetRadiosAsync` on a machine simply lacking
/// Wi-Fi or Bluetooth, tying up the shared MTA worker the watchers also use.
/// One enumeration of every radio, with the moment it was taken.
type RadioSnapshot = (Instant, Vec<(RadioKind, Radio)>);

static CACHE: OnceLock<Mutex<Option<RadioSnapshot>>> = OnceLock::new();

/// How long an ABSENT kind is believed before looking again, so a radio
/// plugged in later is still noticed without paying for an enumeration every
/// two seconds.
const ABSENT_RECHECK: Duration = Duration::from_secs(30);

fn cache() -> &'static Mutex<Option<RadioSnapshot>> {
    CACHE.get_or_init(|| Mutex::new(None))
}

/// The cached radios of one kind, and whether the cache had been filled at all.
fn cached(kind: RadioKind) -> (bool, Instant, Vec<Radio>) {
    match cache().lock() {
        Ok(guard) => match guard.as_ref() {
            Some((taken, entries)) => (
                true,
                *taken,
                entries
                    .iter()
                    .filter(|(cached_kind, _)| *cached_kind == kind)
                    .map(|(_, radio)| radio.clone())
                    .collect(),
            ),
            None => (false, Instant::now(), Vec::new()),
        },
        Err(_) => (false, Instant::now(), Vec::new()),
    }
}

/// Every radio of one kind.
///
/// All of them, not the first: a handheld with built-in Bluetooth and a USB
/// adapter has two, and picking one meant the reported state could flip between
/// polls (the cache answered with the first, a refresh with the last) while the
/// single UI switch toggled only whichever the cache happened to return.
fn all_of(kind: RadioKind) -> Result<Vec<Radio>> {
    let (filled, taken, cached_radios) = cached(kind);
    if filled && !cached_radios.is_empty() {
        // Prove the cached objects still answer; a radio can be removed.
        if cached_radios.iter().all(|radio| radio.State().is_ok()) {
            return Ok(cached_radios);
        }
        if let Ok(mut entries) = cache().lock() {
            *entries = None;
        }
    } else if filled && taken.elapsed() < ABSENT_RECHECK {
        // Enumerated already, and this machine genuinely has no radio of this
        // kind. A real answer, not a cache miss.
        return Ok(Vec::new());
    }
    let mut found = Vec::new();
    let mut entries = Vec::new();
    for radio in all_radios()? {
        let actual = radio.Kind().map_err(|e| winrt("Radio.Kind", e))?;
        for candidate in [RadioKind::WiFi, RadioKind::Bluetooth] {
            if candidate.matches(actual) {
                entries.push((candidate, radio.clone()));
                if candidate == kind {
                    found.push(radio.clone());
                }
            }
        }
    }
    if let Ok(mut cache_entries) = cache().lock() {
        *cache_entries = Some((Instant::now(), entries));
    }
    Ok(found)
}

/// The state to report for a kind that has several radios.
///
/// On wins: the question the tile answers is "can this machine use Bluetooth
/// right now", and one live adapter is enough. Off beats the non-answers so a
/// switchable radio still offers its switch.
pub(crate) fn aggregate(states: &[RadioPower]) -> RadioPower {
    for preferred in [
        RadioPower::On,
        RadioPower::Off,
        RadioPower::Disabled,
        RadioPower::Unknown,
    ] {
        if states.contains(&preferred) {
            return preferred;
        }
    }
    RadioPower::Absent
}

/// Reads the current power state of a radio.
///
/// Never requires permission. Returns [`RadioPower::Absent`] when the machine
/// has no such radio, which the taskbar renders as a neutral tile.
pub fn power(kind: RadioKind) -> Result<RadioPower> {
    on_mta(move || {
        let radios = all_of(kind)?;
        if radios.is_empty() {
            return Ok(RadioPower::Absent);
        }
        let mut states = Vec::with_capacity(radios.len());
        for radio in &radios {
            states.push(RadioPower::from_winrt(
                radio.State().map_err(|e| winrt("Radio.State", e))?,
            ));
        }
        Ok(aggregate(&states))
    })?
}

/// Asks whether this process may change radio state.
///
/// Worth calling once per session and caching: repeated calls are wasted work,
/// and the documented behaviour is that this may prompt the user the first time.
pub fn request_access() -> Result<RadioAccess> {
    on_mta(|| {
        let operation =
            Radio::RequestAccessAsync().map_err(|e| winrt("Radio.RequestAccessAsync", e))?;
        let status = operation
            .join()
            .map_err(|e| winrt("Radio.RequestAccessAsync (join)", e))?;
        Ok(RadioAccess::from_winrt(status))
    })?
}

/// Turns a radio on or off.
///
/// Requests access first and reports the refusal rather than attempting a set
/// that would fail, so the caller can explain *why* to the user.
pub fn set_power(kind: RadioKind, on: bool) -> Result<RadioAccess> {
    on_mta(move || {
        let operation =
            Radio::RequestAccessAsync().map_err(|e| winrt("Radio.RequestAccessAsync", e))?;
        let access = RadioAccess::from_winrt(
            operation
                .join()
                .map_err(|e| winrt("Radio.RequestAccessAsync (join)", e))?,
        );
        if access != RadioAccess::Allowed {
            return Ok(access);
        }
        let radios = all_of(kind)?;
        if radios.is_empty() {
            return Err(crate::error::Error::NotFound("radio"));
        }
        let target = if on {
            WinRtRadioState::On
        } else {
            WinRtRadioState::Off
        };
        // EVERY radio of the kind, because the UI has one switch: leaving the
        // second adapter alone means the tile reports a state the machine is
        // not actually in.
        let mut refusal = None;
        let mut last_error = None;
        let mut applied_any = false;
        for radio in &radios {
            match radio
                .SetStateAsync(target)
                .map_err(|e| winrt("Radio.SetStateAsync", e))
                .and_then(|operation| {
                    operation
                        .join()
                        .map_err(|e| winrt("Radio.SetStateAsync (join)", e))
                }) {
                // The SET's own decision, not the earlier request's: permission
                // can be refused between the two calls (policy, a hardware
                // switch), and returning the stale Allowed made the UI report a
                // state change that never happened.
                Ok(status) => {
                    applied_any = true;
                    let decision = RadioAccess::from_winrt(status);
                    if decision != RadioAccess::Allowed && refusal.is_none() {
                        refusal = Some(decision);
                    }
                }
                Err(error) => last_error = Some(error),
            }
        }
        // Any failure is THE result, even alongside a success: reporting
        // Allowed after one adapter changed and another did not claims a
        // machine-wide state the machine is not in — and the aggregate tile
        // then immediately contradicts the switch.
        if let Some(error) = last_error {
            return Err(error);
        }
        if !applied_any {
            return Err(crate::error::Error::NotFound("radio"));
        }
        Ok(refusal.unwrap_or(RadioAccess::Allowed))
    })?
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn one_live_adapter_is_enough_for_the_tile_to_read_on() {
        // "Can this machine use Bluetooth right now" is the question a tile
        // answers, and a second adapter being off does not change it.
        assert_eq!(
            aggregate(&[RadioPower::Off, RadioPower::On]),
            RadioPower::On
        );
    }

    #[test]
    fn a_switchable_radio_outranks_the_states_that_offer_no_switch() {
        assert_eq!(
            aggregate(&[RadioPower::Unknown, RadioPower::Off]),
            RadioPower::Off
        );
        assert_eq!(
            aggregate(&[RadioPower::Unknown, RadioPower::Disabled]),
            RadioPower::Disabled
        );
    }

    #[test]
    fn no_radios_at_all_is_absent() {
        assert_eq!(aggregate(&[]), RadioPower::Absent);
    }
}
