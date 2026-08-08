//! Error type shared by every operation in this crate.

use std::fmt;

/// The result of a radio operation.
pub type Result<T> = std::result::Result<T, Error>;

/// A failure reported by the radio subsystem.
#[derive(Debug, Clone)]
pub enum Error {
    /// A Win32 call failed. Carries the raw `ERROR_*` / `WLAN_*` status so the
    /// managed side can distinguish, in particular, `ERROR_ACCESS_DENIED` (5),
    /// which is what the Windows 11 24H2 location-consent gate returns from the
    /// scan and current-connection entry points.
    Win32 {
        /// The failing entry point, for the log line.
        api: &'static str,
        /// The raw Win32 status code.
        code: u32,
    },

    /// A WinRT call failed.
    WinRt {
        /// The failing operation, for the log line.
        api: &'static str,
        /// The raw HRESULT.
        hresult: i32,
        /// The message WinRT supplied, if any.
        message: String,
    },

    /// The caller asked for a radio or interface that is not present.
    NotFound(&'static str),

    /// The caller passed something unusable across the ABI (null pointer,
    /// non-UTF-16 text, an SSID that cannot fit the 32-byte field).
    InvalidArgument(&'static str),

    /// The MTA worker thread could not be reached. Only happens if it panicked.
    WorkerUnavailable,
}

impl Error {
    /// The raw Win32 status for a [`Error::Win32`], else zero. The managed side
    /// uses this to detect the consent gate without parsing the message.
    #[must_use]
    pub fn win32_code(&self) -> u32 {
        match self {
            Self::Win32 { code, .. } => *code,
            _ => 0,
        }
    }
}

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Win32 { api, code } => write!(f, "{api} failed (Win32 {code})"),
            Self::WinRt {
                api,
                hresult,
                message,
            } => {
                if message.is_empty() {
                    write!(f, "{api} failed (HRESULT 0x{hresult:08X})")
                } else {
                    write!(f, "{api} failed (HRESULT 0x{hresult:08X}): {message}")
                }
            }
            Self::NotFound(what) => write!(f, "{what} not found"),
            Self::InvalidArgument(what) => write!(f, "invalid argument: {what}"),
            Self::WorkerUnavailable => f.write_str("the radio worker thread is unavailable"),
        }
    }
}

impl std::error::Error for Error {}

/// Wraps a `windows` crate error with the operation that produced it.
pub(crate) fn winrt(api: &'static str, error: windows_core::Error) -> Error {
    Error::WinRt {
        api,
        hresult: error.code().0,
        message: error.message(),
    }
}

/// Maps a Win32/WLAN status to [`Error::Win32`], treating zero as success.
pub(crate) fn win32(api: &'static str, code: u32) -> Result<()> {
    if code == 0 {
        Ok(())
    } else {
        Err(Error::Win32 { api, code })
    }
}
