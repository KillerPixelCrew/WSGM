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

fn first_of(kind: RadioKind) -> Result<Option<Radio>> {
    for radio in all_radios()? {
        let actual = radio.Kind().map_err(|e| winrt("Radio.Kind", e))?;
        if kind.matches(actual) {
            return Ok(Some(radio));
        }
    }
    Ok(None)
}

/// Reads the current power state of a radio.
///
/// Never requires permission. Returns [`RadioPower::Absent`] when the machine
/// has no such radio, which the taskbar renders as a neutral tile.
pub fn power(kind: RadioKind) -> Result<RadioPower> {
    on_mta(move || {
        let Some(radio) = first_of(kind)? else {
            return Ok(RadioPower::Absent);
        };
        let state = radio.State().map_err(|e| winrt("Radio.State", e))?;
        Ok(RadioPower::from_winrt(state))
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
        let Some(radio) = first_of(kind)? else {
            return Err(crate::error::Error::NotFound("radio"));
        };
        let target = if on {
            WinRtRadioState::On
        } else {
            WinRtRadioState::Off
        };
        radio
            .SetStateAsync(target)
            .map_err(|e| winrt("Radio.SetStateAsync", e))?
            .join()
            .map_err(|e| winrt("Radio.SetStateAsync (join)", e))?;
        Ok(access)
    })?
}
