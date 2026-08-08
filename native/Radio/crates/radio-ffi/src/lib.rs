#![cfg(windows)]

//! Stable C ABI consumed by WSGM's managed binding.
//!
//! No Rust-owned layout crosses the boundary: callers get fixed-width
//! `#[repr(C)]` value structures and NUL-terminated UTF-16 strings. Every
//! fallible export returns `0` on success, `1` for a reported error, or `2`
//! when a panic was caught at the boundary — a panic must never unwind into the
//! .NET host. Error text for the last failure on the calling thread is
//! available from [`wsgm_radio_last_error`].
//!
//! Array-returning calls hand back an allocation that the caller must release
//! with the matching `*_free`, exactly like the WLAN API this wraps.

#![deny(missing_docs)]

use std::cell::RefCell;
use std::ffi::c_void;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::ptr::null_mut;

use radio_core::bluetooth::{self, PairOutcome, PairingKind, PairingRequest};
use radio_core::consent::{self, Consent};
use radio_core::error::Error;
use radio_core::radios::{RadioAccess, RadioKind, RadioPower};
use radio_core::wifi::{self, InterfaceState, Security};

/// The call succeeded.
pub const WSGM_RADIO_OK: i32 = 0;
/// The call failed; see [`wsgm_radio_last_error`].
pub const WSGM_RADIO_ERROR: i32 = 1;
/// A panic was caught at the ABI boundary.
pub const WSGM_RADIO_PANIC: i32 = 2;

thread_local! {
    static LAST_ERROR: RefCell<Vec<u16>> = const { RefCell::new(Vec::new()) };
}

fn set_error(text: &str) {
    let encoded: Vec<u16> = text.encode_utf16().chain(std::iter::once(0)).collect();
    LAST_ERROR.with(|slot| *slot.borrow_mut() = encoded);
}

/// Runs `body`, converting a failure into a status code and a stored message.
fn guard<F>(body: F) -> i32
where
    F: FnOnce() -> Result<(), Error>,
{
    match catch_unwind(AssertUnwindSafe(body)) {
        Ok(Ok(())) => WSGM_RADIO_OK,
        Ok(Err(error)) => {
            set_error(&error.to_string());
            WSGM_RADIO_ERROR
        }
        Err(_) => {
            set_error("the radio helper panicked");
            WSGM_RADIO_PANIC
        }
    }
}

/// Copies UTF-16 text into a fixed-size field, always NUL-terminating.
///
/// Truncates rather than failing: a clipped name is a cosmetic problem, while
/// dropping the whole device would lose the id needed to act on it.
fn fill(field: &mut [u16], value: &str) {
    let mut written = 0;
    for unit in value.encode_utf16() {
        if written + 1 >= field.len() {
            break;
        }
        field[written] = unit;
        written += 1;
    }
    field[written] = 0;
}

/// Reads a NUL-terminated UTF-16 string supplied by the caller.
///
/// # Safety
/// `text` must be null or point to a NUL-terminated UTF-16 buffer.
unsafe fn read_utf16(text: *const u16) -> Option<String> {
    if text.is_null() {
        return None;
    }
    let mut len = 0usize;
    // SAFETY: the contract above guarantees a terminator; the cap stops a
    // malformed buffer from walking the address space.
    unsafe {
        while len < 64 * 1024 && *text.add(len) != 0 {
            len += 1;
        }
        Some(String::from_utf16_lossy(std::slice::from_raw_parts(text, len)))
    }
}

fn radio_kind(value: i32) -> Result<RadioKind, Error> {
    match value {
        0 => Ok(RadioKind::WiFi),
        1 => Ok(RadioKind::Bluetooth),
        _ => Err(Error::InvalidArgument("radio kind")),
    }
}

fn power_code(power: RadioPower) -> i32 {
    match power {
        RadioPower::On => 0,
        RadioPower::Off => 1,
        RadioPower::Disabled => 2,
        RadioPower::Unknown => 3,
        RadioPower::Absent => 4,
    }
}

fn access_code(access: RadioAccess) -> i32 {
    match access {
        RadioAccess::Allowed => 0,
        RadioAccess::DeniedByUser => 1,
        RadioAccess::DeniedBySystem => 2,
        RadioAccess::Unspecified => 3,
    }
}

fn consent_code(value: Consent) -> i32 {
    match value {
        Consent::Allow => 0,
        Consent::Deny => 1,
        Consent::Unset => 2,
        Consent::Unknown => 3,
    }
}

/// Copies the last error on this thread into `buffer`.
///
/// Returns the number of UTF-16 units written, excluding the terminator. A null
/// buffer or one that is too small yields zero.
///
/// # Safety
/// `buffer` must be null or point to writable storage of `capacity` units.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn wsgm_radio_last_error(buffer: *mut u16, capacity: u32) -> u32 {
    if buffer.is_null() || capacity == 0 {
        return 0;
    }
    LAST_ERROR.with(|slot| {
        let stored = slot.borrow();
        if stored.is_empty() {
            return 0;
        }
        let units = stored.len().min(capacity as usize);
        // SAFETY: the caller guarantees `capacity` writable units and `units`
        // never exceeds it.
        unsafe { std::ptr::copy_nonoverlapping(stored.as_ptr(), buffer, units) };
        // Guarantee the terminator even when the message was clipped.
        unsafe { *buffer.add(units - 1) = 0 };
        (units - 1) as u32
    })
}

// ---- radios ----

/// Reads a radio's power state into `out_state`.
///
/// # Safety
/// `out_state` must point to a writable `i32`.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn wsgm_radio_power(kind: i32, out_state: *mut i32) -> i32 {
    guard(|| {
        if out_state.is_null() {
            return Err(Error::InvalidArgument("out_state"));
        }
        let state = radio_core::power(radio_kind(kind)?)?;
        unsafe { *out_state = power_code(state) };
        Ok(())
    })
}

/// Asks whether this process may change radio state.
///
/// # Safety
/// `out_access` must point to a writable `i32`.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn wsgm_radio_access(out_access: *mut i32) -> i32 {
    guard(|| {
        if out_access.is_null() {
            return Err(Error::InvalidArgument("out_access"));
        }
        let access = radio_core::request_access()?;
        unsafe { *out_access = access_code(access) };
        Ok(())
    })
}

/// Turns a radio on or off, reporting the access decision in `out_access`.
///
/// # Safety
/// `out_access` must point to a writable `i32`.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn wsgm_radio_set_power(
    kind: i32,
    on: i32,
    out_access: *mut i32,
) -> i32 {
    guard(|| {
        if out_access.is_null() {
            return Err(Error::InvalidArgument("out_access"));
        }
        let access = radio_core::set_power(radio_kind(kind)?, on != 0)?;
        unsafe { *out_access = access_code(access) };
        Ok(())
    })
}

/// Reads a privacy consent value for diagnostics.
///
/// Never a reason to skip a call — see the note in `radio_core::consent`.
///
/// # Safety
/// `name` must be a NUL-terminated UTF-16 string; the out pointers writable.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn wsgm_radio_consent(
    name: *const u16,
    out_user: *mut i32,
    out_machine: *mut i32,
) -> i32 {
    guard(|| {
        let Some(name) = (unsafe { read_utf16(name) }) else {
            return Err(Error::InvalidArgument("capability name"));
        };
        if out_user.is_null() || out_machine.is_null() {
            return Err(Error::InvalidArgument("out pointers"));
        }
        let (user, machine) = consent::capability(&name);
        unsafe {
            *out_user = consent_code(user);
            *out_machine = consent_code(machine);
        }
        Ok(())
    })
}

// ---- Wi-Fi ----

/// One visible network, flattened for the managed side.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct WsgmWifiNetwork {
    /// NUL-terminated SSID. An SSID is at most 32 bytes, so this cannot clip.
    pub ssid: [u16; 64],
    /// Signal quality, 0-100.
    pub signal: u32,
    /// 0 open, 1 pre-shared key, 2 enterprise.
    pub security: i32,
    /// Non-zero when a saved profile exists.
    pub saved: i32,
    /// Non-zero when Windows believes it can be joined.
    pub connectable: i32,
}

/// Reads the Wi-Fi interface state into `out_state`.
///
/// 0 connected, 1 connecting, 2 disconnected, 3 unavailable.
///
/// # Safety
/// `out_state` must point to a writable `i32`.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn wsgm_wifi_state(out_state: *mut i32) -> i32 {
    guard(|| {
        if out_state.is_null() {
            return Err(Error::InvalidArgument("out_state"));
        }
        let code = match wifi::state()? {
            InterfaceState::Connected => 0,
            InterfaceState::Connecting => 1,
            InterfaceState::Disconnected => 2,
            InterfaceState::Unavailable => 3,
        };
        unsafe { *out_state = code };
        Ok(())
    })
}

/// Asks the driver to start a scan. Results arrive in the list a few seconds later.
#[unsafe(no_mangle)]
pub extern "system" fn wsgm_wifi_scan() -> i32 {
    guard(wifi::request_scan)
}

/// Returns the current network list.
///
/// On success `out_items` receives an allocation of `out_count` entries that
/// must be released with [`wsgm_wifi_free`].
///
/// # Safety
/// Both out pointers must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn wsgm_wifi_list(
    out_items: *mut *mut WsgmWifiNetwork,
    out_count: *mut u32,
) -> i32 {
    guard(|| {
        if out_items.is_null() || out_count.is_null() {
            return Err(Error::InvalidArgument("out pointers"));
        }
        unsafe {
            *out_items = null_mut();
            *out_count = 0;
        }
        let networks = wifi::networks()?;
        let mut flat: Vec<WsgmWifiNetwork> = networks
            .iter()
            .map(|n| {
                let mut entry = WsgmWifiNetwork {
                    ssid: [0; 64],
                    signal: n.signal,
                    security: match n.security {
                        Security::Open => 0,
                        Security::PersonalPsk => 1,
                        Security::Enterprise => 2,
                    },
                    saved: i32::from(n.saved),
                    connectable: i32::from(n.connectable),
                };
                fill(&mut entry.ssid, &n.ssid);
                entry
            })
            .collect();
        flat.shrink_to_fit();
        let count = flat.len() as u32;
        let pointer = flat.as_mut_ptr();
        // Ownership moves to the caller until wsgm_wifi_free takes it back.
        std::mem::forget(flat);
        unsafe {
            *out_items = pointer;
            *out_count = count;
        }
        Ok(())
    })
}

/// Releases a list returned by [`wsgm_wifi_list`].
///
/// # Safety
/// `items`/`count` must be exactly what `wsgm_wifi_list` produced, released once.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn wsgm_wifi_free(items: *mut WsgmWifiNetwork, count: u32) {
    if items.is_null() {
        return;
    }
    // SAFETY: reconstitutes the Vec that `wsgm_wifi_list` forgot, with the same
    // length and capacity (shrink_to_fit made them equal).
    unsafe {
        drop(Vec::from_raw_parts(items, count as usize, count as usize));
    }
}

/// Installs a profile for `ssid` and connects.
///
/// `passphrase` may be null for an open network.
///
/// # Safety
/// Both strings must be null or NUL-terminated UTF-16.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn wsgm_wifi_connect(
    ssid: *const u16,
    passphrase: *const u16,
    out_reason: *mut u32,
) -> i32 {
    guard(|| {
        let Some(ssid) = (unsafe { read_utf16(ssid) }) else {
            return Err(Error::InvalidArgument("ssid"));
        };
        let passphrase = unsafe { read_utf16(passphrase) };
        if !out_reason.is_null() {
            unsafe { *out_reason = 0 };
        }
        match wifi::connect(&ssid, passphrase.as_deref()) {
            Ok(()) => Ok(()),
            Err(error) => {
                // Surface the raw reason code: it is what tells the caller
                // whether to re-prompt for a password or report a dead network.
                if !out_reason.is_null() {
                    unsafe { *out_reason = error.win32_code() };
                }
                Err(error)
            }
        }
    })
}

/// Disconnects the Wi-Fi interface.
#[unsafe(no_mangle)]
pub extern "system" fn wsgm_wifi_disconnect() -> i32 {
    guard(wifi::disconnect)
}

/// Deletes the saved profile for `ssid`.
///
/// # Safety
/// `ssid` must be a NUL-terminated UTF-16 string.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn wsgm_wifi_forget(ssid: *const u16) -> i32 {
    guard(|| {
        let Some(ssid) = (unsafe { read_utf16(ssid) }) else {
            return Err(Error::InvalidArgument("ssid"));
        };
        wifi::forget(&ssid)
    })
}

/// Classifies a WLAN reason code: 0 success, 1 wrong password, 2 bad profile,
/// 3 unreachable, 4 unknown.
#[unsafe(no_mangle)]
pub extern "system" fn wsgm_wifi_reason_verdict(code: u32) -> i32 {
    use radio_core::wifi::reason::Verdict;
    match radio_core::wifi::reason::verdict(code) {
        Verdict::Success => 0,
        Verdict::WrongPassword => 1,
        Verdict::BadProfile => 2,
        Verdict::Unreachable => 3,
        Verdict::Unknown => 4,
    }
}

/// Writes Windows' own localised text for a reason code into `buffer`.
///
/// # Safety
/// `buffer` must point to writable storage of `capacity` units.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn wsgm_wifi_reason_text(
    code: u32,
    buffer: *mut u16,
    capacity: u32,
) -> i32 {
    guard(|| {
        if buffer.is_null() || capacity == 0 {
            return Err(Error::InvalidArgument("buffer"));
        }
        let text = radio_core::wifi::reason::describe(code);
        // SAFETY: the caller guarantees `capacity` writable units.
        let field = unsafe { std::slice::from_raw_parts_mut(buffer, capacity as usize) };
        fill(field, &text);
        Ok(())
    })
}

// ---- Bluetooth ----

/// One Bluetooth device, flattened for the managed side.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct WsgmBtDevice {
    /// NUL-terminated WinRT device id; the handle for every other call.
    pub id: [u16; 256],
    /// NUL-terminated display name, possibly empty.
    pub name: [u16; 128],
    /// Non-zero when already paired.
    pub paired: i32,
    /// Non-zero when Windows thinks pairing is possible.
    pub can_pair: i32,
}

/// Returns Bluetooth devices; `paired_only` limits the list to paired ones.
///
/// Release with [`wsgm_bt_free`].
///
/// # Safety
/// Both out pointers must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn wsgm_bt_list(
    paired_only: i32,
    out_items: *mut *mut WsgmBtDevice,
    out_count: *mut u32,
) -> i32 {
    guard(|| {
        if out_items.is_null() || out_count.is_null() {
            return Err(Error::InvalidArgument("out pointers"));
        }
        unsafe {
            *out_items = null_mut();
            *out_count = 0;
        }
        let devices = if paired_only != 0 {
            bluetooth::paired_devices()?
        } else {
            bluetooth::devices()?
        };
        let mut flat: Vec<WsgmBtDevice> = devices
            .iter()
            .map(|d| {
                let mut entry = WsgmBtDevice {
                    id: [0; 256],
                    name: [0; 128],
                    paired: i32::from(d.paired),
                    can_pair: i32::from(d.can_pair),
                };
                fill(&mut entry.id, &d.id);
                fill(&mut entry.name, &d.name);
                entry
            })
            .collect();
        flat.shrink_to_fit();
        let count = flat.len() as u32;
        let pointer = flat.as_mut_ptr();
        std::mem::forget(flat);
        unsafe {
            *out_items = pointer;
            *out_count = count;
        }
        Ok(())
    })
}

/// Releases a list returned by [`wsgm_bt_list`].
///
/// # Safety
/// `items`/`count` must be exactly what `wsgm_bt_list` produced, released once.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn wsgm_bt_free(items: *mut WsgmBtDevice, count: u32) {
    if items.is_null() {
        return;
    }
    // SAFETY: as in wsgm_wifi_free.
    unsafe {
        drop(Vec::from_raw_parts(items, count as usize, count as usize));
    }
}

/// Called when Windows asks a pairing question.
///
/// `kind` is 0 confirm-only, 1 display-pin, 2 provide-pin, 3 confirm-pin-match,
/// 4 unsupported. `pin` and `device_name` are NUL-terminated UTF-16 valid only
/// for the duration of the call — copy them before returning.
pub type WsgmPairingRequestFn = extern "system" fn(
    context: *mut c_void,
    token: u32,
    kind: i32,
    pin: *const u16,
    device_name: *const u16,
);

/// Called once when a pairing attempt finishes.
///
/// `outcome` is 0 paired, 1 already-paired, 2 rejected, 3 failed,
/// 4 access-denied, 5 other, or -1 when the attempt errored before starting,
/// in which case `message` describes it.
pub type WsgmPairingDoneFn =
    extern "system" fn(context: *mut c_void, outcome: i32, message: *const u16);

/// The `*mut c_void` cookie the caller passes back to its callbacks.
///
/// Rust requires an explicit promise that it may cross threads: the callbacks
/// fire on a worker, not on the thread that started the pairing. The managed
/// side satisfies this by passing a `GCHandle`, which is process-wide.
struct Context(*mut c_void);

impl Context {
    /// Reads the cookie back out.
    ///
    /// A method rather than a field access on purpose: closures capture
    /// disjoint *fields*, so `context.0` inside a `move` closure would capture
    /// the bare `*mut c_void` — which is not `Send` — instead of this wrapper.
    /// Going through `&self` captures the whole struct.
    fn get(&self) -> *mut c_void {
        self.0
    }
}

// SAFETY: the pointer is an opaque token owned by the caller. This crate only
// hands it back, never dereferences it, and the documented contract is that it
// stays valid until the finished callback has run.
unsafe impl Send for Context {}

fn outcome_code(outcome: PairOutcome) -> i32 {
    match outcome {
        PairOutcome::Paired => 0,
        PairOutcome::AlreadyPaired => 1,
        PairOutcome::Rejected => 2,
        PairOutcome::Failed => 3,
        PairOutcome::AccessDenied => 4,
        PairOutcome::Other => 5,
    }
}

fn kind_code(kind: PairingKind) -> i32 {
    match kind {
        PairingKind::ConfirmOnly => 0,
        PairingKind::DisplayPin => 1,
        PairingKind::ProvidePin => 2,
        PairingKind::ConfirmPinMatch => 3,
        PairingKind::Unsupported => 4,
    }
}

/// Starts pairing `device_id`, reporting through the two callbacks.
///
/// Returns as soon as the attempt is under way. Every request delivered to
/// `on_request` must be answered with [`wsgm_bt_respond`], or the ceremony
/// stalls until Windows times it out.
///
/// # Safety
/// `device_id` must be NUL-terminated UTF-16, and `context` must remain valid
/// until `on_done` has been called.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn wsgm_bt_pair(
    device_id: *const u16,
    on_request: WsgmPairingRequestFn,
    on_done: WsgmPairingDoneFn,
    context: *mut c_void,
) -> i32 {
    guard(|| {
        let Some(id) = (unsafe { read_utf16(device_id) }) else {
            return Err(Error::InvalidArgument("device id"));
        };
        let request_context = Context(context);
        let done_context = Context(context);
        bluetooth::pair(
            &id,
            move |request: PairingRequest| {
                let pin: Vec<u16> = request
                    .pin
                    .encode_utf16()
                    .chain(std::iter::once(0))
                    .collect();
                let name: Vec<u16> = request
                    .device_name
                    .encode_utf16()
                    .chain(std::iter::once(0))
                    .collect();
                on_request(
                    request_context.get(),
                    request.token,
                    kind_code(request.kind),
                    pin.as_ptr(),
                    name.as_ptr(),
                );
            },
            move |result| match result {
                Ok(outcome) => on_done(done_context.get(), outcome_code(outcome), std::ptr::null()),
                Err(error) => {
                    let message: Vec<u16> = error
                        .to_string()
                        .encode_utf16()
                        .chain(std::iter::once(0))
                        .collect();
                    on_done(done_context.get(), -1, message.as_ptr());
                }
            },
        )
    })
}

/// Answers a pairing request. `pin` is used only for the provide-pin ceremony.
///
/// # Safety
/// `pin` must be null or NUL-terminated UTF-16.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn wsgm_bt_respond(token: u32, accept: i32, pin: *const u16) -> i32 {
    guard(|| {
        let pin = unsafe { read_utf16(pin) }.unwrap_or_default();
        bluetooth::respond(token, accept != 0, &pin)
    })
}

/// Removes a pairing. `out_removed` receives non-zero when the device is now unpaired.
///
/// # Safety
/// `device_id` must be NUL-terminated UTF-16 and `out_removed` writable.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn wsgm_bt_unpair(device_id: *const u16, out_removed: *mut i32) -> i32 {
    guard(|| {
        let Some(id) = (unsafe { read_utf16(device_id) }) else {
            return Err(Error::InvalidArgument("device id"));
        };
        let removed = bluetooth::unpair(&id)?;
        if !out_removed.is_null() {
            unsafe { *out_removed = i32::from(removed) };
        }
        Ok(())
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fill_always_terminates_and_never_overruns() {
        let mut field = [0xFFFFu16; 8];
        fill(&mut field, "abcdefghijkl");
        assert_eq!(field[7], 0, "the last unit must be the terminator");
        assert_eq!(String::from_utf16_lossy(&field[..7]), "abcdefg");
    }

    #[test]
    fn fill_terminates_an_exactly_fitting_value() {
        let mut field = [0xFFFFu16; 4];
        fill(&mut field, "abc");
        assert_eq!(field[3], 0);
        assert_eq!(String::from_utf16_lossy(&field[..3]), "abc");
    }

    #[test]
    fn fill_handles_an_empty_value() {
        let mut field = [0xFFFFu16; 4];
        fill(&mut field, "");
        assert_eq!(field[0], 0);
    }

    #[test]
    fn a_null_string_is_none_rather_than_a_crash() {
        assert!(unsafe { read_utf16(std::ptr::null()) }.is_none());
    }

    #[test]
    fn a_round_trip_through_read_utf16_preserves_text() {
        let source: Vec<u16> = "Hallo Welt".encode_utf16().chain([0]).collect();
        assert_eq!(
            unsafe { read_utf16(source.as_ptr()) }.as_deref(),
            Some("Hallo Welt")
        );
    }

    #[test]
    fn freeing_a_null_list_is_a_no_op() {
        unsafe { wsgm_wifi_free(null_mut(), 0) };
        unsafe { wsgm_bt_free(null_mut(), 0) };
    }

    #[test]
    fn an_unknown_radio_kind_is_rejected_rather_than_defaulted() {
        let mut state = -99;
        assert_eq!(
            unsafe { wsgm_radio_power(7, &mut state) },
            WSGM_RADIO_ERROR
        );
        assert_eq!(state, -99, "the out value must be left alone on failure");
    }

    #[test]
    fn a_null_out_pointer_is_reported_not_written() {
        assert_eq!(
            unsafe { wsgm_radio_power(0, null_mut()) },
            WSGM_RADIO_ERROR
        );
    }

    #[test]
    fn the_last_error_message_survives_the_round_trip() {
        let mut state = 0;
        assert_eq!(
            unsafe { wsgm_radio_power(42, &mut state) },
            WSGM_RADIO_ERROR
        );
        let mut buffer = [0u16; 256];
        let written = unsafe { wsgm_radio_last_error(buffer.as_mut_ptr(), 256) };
        assert!(written > 0);
        let text = String::from_utf16_lossy(&buffer[..written as usize]);
        assert!(text.contains("radio kind"), "unexpected message: {text}");
    }

    #[test]
    fn reason_verdicts_cross_the_abi_as_stable_codes() {
        use radio_core::wifi::reason::MSMSEC_PSK_MISMATCH_SUSPECTED;
        assert_eq!(wsgm_wifi_reason_verdict(0), 0);
        assert_eq!(wsgm_wifi_reason_verdict(MSMSEC_PSK_MISMATCH_SUSPECTED), 1);
    }
}
