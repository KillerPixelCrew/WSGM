//! Error type shared by every module in this crate.

use std::fmt;

/// Result alias used throughout the crate.
pub type Result<T> = std::result::Result<T, Error>;

/// A failure reported by a Windows power entry point or by this crate's own
/// validation of one.
#[derive(Debug, Clone)]
pub enum Error {
    /// A Win32 call failed. Carries the API name and `GetLastError` value.
    Win32 {
        /// Name of the entry point that failed.
        api: &'static str,
        /// Value reported by `GetLastError` at the point of failure.
        code: u32,
    },
    /// An `NTSTATUS`-returning call failed.
    NtStatus {
        /// Name of the entry point that failed.
        api: &'static str,
        /// Raw `NTSTATUS` value.
        status: i32,
    },
    /// A call reported success but the observable state did not change. This is
    /// reported in preference to trusting a return code, because
    /// `DevicePowerSetDeviceState` is verified by re-enumeration.
    NotApplied {
        /// The device description that was targeted.
        device: String,
    },
    /// The requested operation needs an elevated process.
    AccessDenied {
        /// Name of the entry point that reported the denial.
        api: &'static str,
    },
    /// Persisted state on disk could not be read or written.
    Store(String),
    /// The device does not support what was asked of it.
    Unsupported(&'static str),
}

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Error::Win32 { api, code } => write!(f, "{api} failed (GetLastError = {code})"),
            Error::NtStatus { api, status } => {
                write!(f, "{api} failed (NTSTATUS = 0x{status:08X})")
            }
            Error::NotApplied { device } => {
                write!(f, "wake state for '{device}' did not change after the call")
            }
            Error::AccessDenied { api } => {
                write!(f, "{api} requires an elevated process")
            }
            Error::Store(message) => write!(f, "persisted state error: {message}"),
            Error::Unsupported(what) => write!(f, "unsupported on this device: {what}"),
        }
    }
}

impl std::error::Error for Error {}

/// Wraps the last Win32 error for `api`.
pub(crate) fn last_win32(api: &'static str) -> Error {
    // SAFETY: GetLastError has no preconditions and no side effects.
    let code = unsafe { windows_sys::Win32::Foundation::GetLastError() };
    if code == windows_sys::Win32::Foundation::ERROR_ACCESS_DENIED {
        Error::AccessDenied { api }
    } else {
        Error::Win32 { api, code }
    }
}
