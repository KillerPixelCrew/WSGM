#![cfg(windows)]

//! Wi-Fi and Bluetooth control for the WSGM game-mode shell.
//!
//! WSGM ships as a NativeAOT executable with managed COM interop disabled, so
//! the APIs behind this feature cannot be reached from C#. This crate owns them
//! instead and exposes a flat C ABI through `radio-ffi`, the same shape the
//! Steam Input lease and the volume helper already use.
//!
//! The split between the two Windows APIs in here is not a style choice:
//!
//! * **Radio power** is WinRT `Windows.Devices.Radios`. It is the only
//!   documented way to switch a radio rather than disable the adapter.
//! * **Wi-Fi scan/connect** is the Win32 native WLAN API. WinRT's `WiFiAdapter`
//!   is unusable to us because `RequestAccessAsync` always returns
//!   `DeniedBySystem` without the `wiFiControl` capability, and only a packaged
//!   app can declare one.
//! * **Bluetooth discovery and pairing** is WinRT `Windows.Devices.Enumeration`.
//!   The Win32 Bluetooth API cannot discover Low Energy devices at all.
//!
//! Every WinRT call runs on the worker in [`mta`] — see that module for why.

#![deny(missing_docs)]

pub mod bluetooth;
pub mod consent;
pub mod error;
pub mod keyboard;
pub mod mta;
pub mod radios;
pub mod wifi;

pub use error::{Error, Result};
pub use radios::{RadioAccess, RadioKind, RadioPower, power, request_access, set_power};
pub use wifi::{InterfaceState, Network, Security};
