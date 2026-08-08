//! Live WLAN notifications.
//!
//! Polling a scan list makes the picker feel dead: the driver refreshes its list
//! when it feels like it, so a fixed interval either wastes work or shows a
//! network seconds after Windows already knew about it. Registering for
//! notifications means the list is republished the moment it actually changes,
//! which is what the Windows applet does.
//!
//! The ACM source carries the scan and connection events. The MSM source, which
//! carries the precise disconnect reason, additionally requires the `wiFiControl`
//! capability — so registration asks for both and quietly settles for ACM alone
//! when MSM is refused.

use std::ffi::c_void;
use std::sync::{Mutex, OnceLock};

use windows::Win32::Foundation::HANDLE;
use windows::Win32::NetworkManagement::WiFi::{
    L2_NOTIFICATION_DATA, WLAN_NOTIFICATION_SOURCE_ACM,
    WLAN_NOTIFICATION_SOURCE_MSM, WlanCloseHandle, WlanOpenHandle, WlanRegisterNotification,
};

use crate::error::{Result, win32};

/// What the WLAN service reported.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum WifiEvent {
    /// The list of visible networks changed. Re-read and republish it.
    ScanListRefreshed,

    /// A connection attempt finished, successfully or not.
    ConnectionChanged,
}

// From WLAN_NOTIFICATION_ACM.
const ACM_SCAN_COMPLETE: u32 = 7;
const ACM_SCAN_LIST_REFRESH: u32 = 9;
const ACM_CONNECTION_COMPLETE: u32 = 10;
const ACM_DISCONNECTED: u32 = 12;

type Sink = Box<dyn Fn(WifiEvent) + Send + Sync + 'static>;

static SINK: OnceLock<Mutex<Option<Sink>>> = OnceLock::new();
static CLIENT: OnceLock<Mutex<Option<isize>>> = OnceLock::new();

fn sink() -> &'static Mutex<Option<Sink>> {
    SINK.get_or_init(|| Mutex::new(None))
}

fn client() -> &'static Mutex<Option<isize>> {
    CLIENT.get_or_init(|| Mutex::new(None))
}

/// The callback the WLAN service invokes, on one of its own threads.
///
/// Must not block and must not unwind into the service: the handler only
/// classifies the event and hands it on.
unsafe extern "system" fn on_notification(data: *mut L2_NOTIFICATION_DATA, _context: *mut c_void) {
    if data.is_null() {
        return;
    }
    // SAFETY: the service passes a valid record for the duration of the call.
    let (source, code) = unsafe { ((*data).NotificationSource, (*data).NotificationCode) };
    if source != WLAN_NOTIFICATION_SOURCE_ACM {
        return;
    }
    let event = match code {
        ACM_SCAN_COMPLETE | ACM_SCAN_LIST_REFRESH => WifiEvent::ScanListRefreshed,
        ACM_CONNECTION_COMPLETE | ACM_DISCONNECTED => WifiEvent::ConnectionChanged,
        _ => return,
    };
    if let Ok(guard) = sink().lock()
        && let Some(handler) = guard.as_ref()
    {
        handler(event);
    }
}

/// Starts delivering WLAN events to `on_event`. Restarting is safe.
///
/// The handle stays open for the life of the registration: closing it is what
/// unregisters, so it cannot be a scoped `Client` like the query paths use.
pub fn start<F>(on_event: F) -> Result<()>
where
    F: Fn(WifiEvent) + Send + Sync + 'static,
{
    stop();
    if let Ok(mut guard) = sink().lock() {
        *guard = Some(Box::new(on_event));
    }

    let mut negotiated = 0u32;
    let mut handle = HANDLE::default();
    let status = unsafe { WlanOpenHandle(2, None, &mut negotiated, &mut handle) };
    win32("WlanOpenHandle", status)?;

    // Ask for MSM as well as ACM: MSM carries the precise wrong-password reason.
    // It needs the wiFiControl capability, so fall back to ACM alone rather than
    // ending up with no notifications at all.
    let both = WLAN_NOTIFICATION_SOURCE_ACM | WLAN_NOTIFICATION_SOURCE_MSM;
    let mut status = unsafe {
        WlanRegisterNotification(
            handle,
            both,
            true,
            Some(on_notification),
            None,
            None,
            None,
        )
    };
    if status != 0 {
        status = unsafe {
            WlanRegisterNotification(
                handle,
                WLAN_NOTIFICATION_SOURCE_ACM,
                true,
                Some(on_notification),
                None,
                None,
                None,
            )
        };
    }
    if status != 0 {
        unsafe {
            let _ = WlanCloseHandle(handle, None);
        }
        return win32("WlanRegisterNotification", status);
    }

    if let Ok(mut guard) = client().lock() {
        *guard = Some(handle.0 as isize);
    }
    Ok(())
}

/// Stops delivering WLAN events. Idempotent.
pub fn stop() {
    let existing = client().lock().ok().and_then(|mut guard| guard.take());
    if let Some(raw) = existing {
        // Closing the handle is what unregisters the callback.
        unsafe {
            let _ = WlanCloseHandle(HANDLE(raw as *mut c_void), None);
        }
    }
    if let Ok(mut guard) = sink().lock() {
        *guard = None;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_the_scan_and_connection_codes_are_interesting() {
        // Guards the constants against a careless edit: these four are the ones
        // the picker reacts to, and the numbers come from WLAN_NOTIFICATION_ACM.
        assert_eq!(ACM_SCAN_COMPLETE, 7);
        assert_eq!(ACM_SCAN_LIST_REFRESH, 9);
        assert_eq!(ACM_CONNECTION_COMPLETE, 10);
        assert_eq!(ACM_DISCONNECTED, 12);
    }

    #[test]
    fn stopping_without_starting_is_harmless() {
        stop();
        stop();
    }
}
