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
use std::sync::mpsc::{Receiver, SyncSender, sync_channel};
use std::sync::{Mutex, OnceLock};
use std::time::Duration;

use windows::Win32::Foundation::HANDLE;
use windows_core::GUID;
use windows::Win32::NetworkManagement::WiFi::{
    L2_NOTIFICATION_DATA, WLAN_CONNECTION_NOTIFICATION_DATA, WLAN_NOTIFICATION_SOURCE_ACM,
    WLAN_NOTIFICATION_SOURCE_MSM, WLAN_NOTIFICATION_SOURCE_NONE, WlanCloseHandle, WlanOpenHandle,
    WlanRegisterNotification,
    wlan_notification_acm_connection_attempt_fail, wlan_notification_acm_connection_complete,
    wlan_notification_acm_disconnected, wlan_notification_acm_scan_complete,
    wlan_notification_acm_scan_list_refresh,
};

use crate::error::{Error, Result, win32};

/// What the WLAN service reported.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum WifiEvent {
    /// The list of visible networks changed. Re-read and republish it.
    ScanListRefreshed,

    /// A connection attempt finished, successfully or not.
    ConnectionChanged,
}

// From WLAN_NOTIFICATION_ACM, taken from the crate's own constants rather than
// written out here: the hand-copied numbers were wrong (9 is connection_start,
// not scan_list_refresh; 12 is filter_list_change, not disconnected), so the
// picker was reacting to events it never meant to watch and ignoring the two it
// did.
const ACM_SCAN_COMPLETE: u32 = wlan_notification_acm_scan_complete.0 as u32;
const ACM_SCAN_LIST_REFRESH: u32 = wlan_notification_acm_scan_list_refresh.0 as u32;
const ACM_CONNECTION_COMPLETE: u32 = wlan_notification_acm_connection_complete.0 as u32;
const ACM_CONNECTION_ATTEMPT_FAIL: u32 = wlan_notification_acm_connection_attempt_fail.0 as u32;
const ACM_DISCONNECTED: u32 = wlan_notification_acm_disconnected.0 as u32;

/// `WLAN_REASON_CODE_SUCCESS`. The only reason value that means the attempt
/// actually succeeded.
const WLAN_REASON_CODE_SUCCESS: u32 = 0;

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

/// How one connection attempt ended.
#[derive(Debug, Clone, Copy)]
pub struct ConnectionOutcome {
    /// Whether the interface actually associated.
    pub succeeded: bool,
    /// The WLAN reason code, which is what distinguishes a rejected key from an
    /// unreachable network. Zero when the service reported none.
    pub reason: u32,
}

/// A scoped ACM registration that reports the next connection outcome.
///
/// This exists because `WlanConnect` returning `ERROR_SUCCESS` means only that
/// the request was *accepted*. The verdict — wrong password, network out of
/// range, associated — arrives later as a notification. Reporting success at
/// the call was how a mistyped password looked like a successful join, while
/// the bad profile it had just saved stayed behind and stopped the panel from
/// ever asking for the password again.
///
/// Its own handle, deliberately: the panel's live feed is a process-wide
/// registration with a different job, and one attempt's verdict must not
/// depend on whether the panel happens to be open.
pub struct ConnectionWatch {
    handle: HANDLE,
    receiver: Receiver<ConnectionOutcome>,
    /// The boxed context the callback points at. Owned here and freed only
    /// after the registration is torn down.
    context: *mut WatchContext,
}

/// What the callback needs: where to send the verdict, and which adapter's
/// verdict actually counts.
struct WatchContext {
    sender: SyncSender<ConnectionOutcome>,
    /// The interface `WlanConnect` was called on. Notifications are
    /// process-wide, so a second adapter's automatic reconnect would otherwise
    /// be read as this attempt's outcome — and could roll back the profile
    /// authored for a network that was still connecting.
    interface: GUID,
}

// SAFETY: the raw pointer is an owned Box this type alone frees, and the only
// other reader is the WLAN callback, which the Drop order guarantees has
// finished by then.
unsafe impl Send for ConnectionWatch {}

/// The callback for a [`ConnectionWatch`]. Runs on a WLAN service thread, so it
/// only classifies and forwards; it never blocks.
unsafe extern "system" fn on_connection(data: *mut L2_NOTIFICATION_DATA, context: *mut c_void) {
    if data.is_null() || context.is_null() {
        return;
    }
    // SAFETY: the service passes a valid record for the duration of the call.
    let (source, code, interface, payload, size) = unsafe {
        (
            (*data).NotificationSource,
            (*data).NotificationCode,
            (*data).InterfaceGuid,
            (*data).pData,
            (*data).dwDataSize as usize,
        )
    };
    if source != WLAN_NOTIFICATION_SOURCE_ACM {
        return;
    }
    // SAFETY: the context outlives the registration (see Drop).
    let watch = unsafe { &*context.cast::<WatchContext>() };
    // Another adapter's event is not this attempt's verdict.
    if interface != watch.interface {
        return;
    }
    if !matches!(code, ACM_CONNECTION_COMPLETE | ACM_CONNECTION_ATTEMPT_FAIL) {
        return;
    }
    // The reason code lives in the payload; a short or absent one is reported
    // as zero rather than read past its end.
    let reason = if !payload.is_null() && size >= size_of::<WLAN_CONNECTION_NOTIFICATION_DATA>() {
        // SAFETY: size checked against the record this notification carries.
        unsafe { (*payload.cast::<WLAN_CONNECTION_NOTIFICATION_DATA>()).wlanReasonCode }
    } else {
        0
    };
    // The EVENT alone does not mean the join worked: connection_complete is
    // also how a failed authentication is reported, with the reason carrying
    // the verdict. Trusting the event was how a rejected password still read as
    // a successful connection — and kept the bad profile it had just saved.
    let succeeded = code == ACM_CONNECTION_COMPLETE && reason == WLAN_REASON_CODE_SUCCESS;
    // try_send: a service callback must never block, and one outcome is all
    // the caller waits for.
    let _ = watch
        .sender
        .try_send(ConnectionOutcome { succeeded, reason });
}

impl ConnectionWatch {
    /// Registers for connection outcomes on one interface. Arm this BEFORE
    /// calling `WlanConnect`, or a fast verdict is missed entirely.
    ///
    /// # Parameters
    /// * `interface` — the adapter the attempt runs on; every other adapter's
    ///   notifications are ignored.
    pub fn start(interface: GUID) -> Result<Self> {
        let (sender, receiver) = sync_channel::<ConnectionOutcome>(4);
        let context = Box::into_raw(Box::new(WatchContext { sender, interface }));

        let mut negotiated = 0u32;
        let mut handle = HANDLE::default();
        let status = unsafe { WlanOpenHandle(2, None, &mut negotiated, &mut handle) };
        if let Err(error) = win32("WlanOpenHandle", status) {
            // SAFETY: nothing else ever saw this pointer.
            drop(unsafe { Box::from_raw(context) });
            return Err(error);
        }

        let status = unsafe {
            WlanRegisterNotification(
                handle,
                WLAN_NOTIFICATION_SOURCE_ACM,
                true,
                Some(on_connection),
                Some(context.cast()),
                None,
                None,
            )
        };
        if status != 0 {
            unsafe {
                let _ = WlanCloseHandle(handle, None);
                drop(Box::from_raw(context));
            }
            return win32("WlanRegisterNotification", status).map(|()| unreachable!());
        }
        Ok(Self {
            handle,
            receiver,
            context,
        })
    }

    /// Waits for the first outcome, or `None` when the service stayed silent.
    pub fn wait(&self, timeout: Duration) -> Option<ConnectionOutcome> {
        self.receiver.recv_timeout(timeout).ok()
    }
}

impl Drop for ConnectionWatch {
    fn drop(&mut self) {
        unsafe {
            // Deregister FIRST: this waits for any callback already running, so
            // the boxed sender can never be freed under a live callback.
            let _ = WlanRegisterNotification(
                self.handle,
                WLAN_NOTIFICATION_SOURCE_NONE,
                false,
                None,
                None,
                None,
                None,
            );
            let _ = WlanCloseHandle(self.handle, None);
            drop(Box::from_raw(self.context));
        }
    }
}

/// Maps an unsuccessful outcome onto the error the ABI reports, carrying the
/// reason code the caller classifies with [`super::reason::verdict`].
pub(crate) fn connection_error(outcome: ConnectionOutcome) -> Error {
    Error::Win32 {
        api: "WlanConnect (connection attempt)",
        code: outcome.reason,
    }
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
    fn the_acm_codes_are_the_ones_wlanapi_actually_defines() {
        // Pinned because the previous hand-written numbers were wrong: 9 is
        // connection_start and 12 is filter_list_change, so the picker watched
        // two events it did not care about and missed both it did.
        assert_eq!(ACM_SCAN_COMPLETE, 7);
        assert_eq!(ACM_SCAN_LIST_REFRESH, 26);
        assert_eq!(ACM_CONNECTION_COMPLETE, 10);
        assert_eq!(ACM_CONNECTION_ATTEMPT_FAIL, 11);
        assert_eq!(ACM_DISCONNECTED, 21);
    }

    #[test]
    fn stopping_without_starting_is_harmless() {
        stop();
        stop();
    }
}
