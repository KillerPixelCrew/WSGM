#![cfg(windows)]

//! Modern Standby diagnostics and wake-source control for Windows handhelds.
//!
//! This crate deliberately covers only what HandheldCompanion does **not**:
//! wake-source inventory and disarming, wake statistics, sleep-blocker
//! diagnostics, and the traditional-sleep (S3) escape hatch. It never writes
//! power-scheme policy values and never re-suspends the system on an unwanted
//! wake — HandheldCompanion's `EnhancedSleep` and `GoBackToSleep` own those,
//! and duplicate writers would fight each other over the same state.
//!
//! Everything here goes through documented Win32 entry points rather than
//! parsing `powercfg` output, so behaviour does not change with the display
//! language of the machine.

#![deny(missing_docs)]

pub mod caps;
pub mod census;
pub mod error;
pub mod events;
pub mod requests;
pub mod sleep;
pub mod wake;

pub use caps::{Capabilities, capabilities};
pub use census::{Census, SourceStats, Verdict};
pub use error::{Error, Result};
pub use events::{PowerEvent, PowerEventKind, WakeWatcher};
pub use requests::{PowerRequest, RequestKind, power_requests};
pub use sleep::{SleepOutcome, suspend, try_traditional_sleep};
pub use wake::{WakeDevice, WakeStore, disarm, list_wake_armed, list_wake_programmable, rearm};
