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
//! with the matching `*_free`, exactly like the WLAN API this wraps. Each one
//! travels as a boxed slice (see [`leak_slice`]) so the layout the caller
//! returns is exactly the one that was allocated.

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

/// Hands a vector's storage to the caller as a bare pointer and a count.
///
/// Via `into_boxed_slice`, NOT `shrink_to_fit` + `into_raw_parts`: shrinking is
/// documented as *allowed* to leave spare capacity, and reconstructing such an
/// allocation later with `count` as both length and capacity would hand the
/// allocator a layout it never issued — undefined behaviour on free. A boxed
/// slice's capacity is its length by construction, so the matching
/// [`reclaim_slice`] always sees the original layout.
fn leak_slice<T>(items: Vec<T>) -> (*mut T, u32) {
    let count = items.len() as u32;
    let boxed = items.into_boxed_slice();
    (Box::into_raw(boxed).cast::<T>(), count)
}

/// Takes back what [`leak_slice`] handed out.
///
/// # Safety
/// `items`/`count` must be exactly one `leak_slice` result, reclaimed once.
unsafe fn reclaim_slice<T>(items: *mut T, count: u32) {
    if items.is_null() {
        return;
    }
    // SAFETY: rebuilds the very boxed slice that leak_slice released.
    unsafe {
        drop(Box::from_raw(std::ptr::slice_from_raw_parts_mut(
            items,
            count as usize,
        )));
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
    /// 0 open, 1 pre-shared key, 2 enterprise, 3 Enhanced Open (OWE),
    /// 4 unsupported protection (WEP).
    pub security: i32,
    /// Non-zero when a saved profile exists.
    pub saved: i32,
    /// Non-zero when Windows believes it can be joined.
    pub connectable: i32,
    /// Non-zero when this is the network currently joined.
    pub connected: i32,
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

/// Reads the interface state, joined SSID and signal in one call.
///
/// One call because the taskbar tile needs all three on every status tick:
/// reading the signal only while the panel was open left the tile with no bars
/// until the panel had been opened once.
///
/// # Safety
/// All out pointers must be writable; `ssid` must have `ssid_capacity` units.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn wsgm_wifi_status(
    out_state: *mut i32,
    out_signal: *mut u32,
    ssid: *mut u16,
    ssid_capacity: u32,
) -> i32 {
    guard(|| {
        if out_state.is_null() || out_signal.is_null() {
            return Err(Error::InvalidArgument("out pointers"));
        }
        let (state, name, signal) = wifi::status()?;
        let code = match state {
            InterfaceState::Connected => 0,
            InterfaceState::Connecting => 1,
            InterfaceState::Disconnected => 2,
            InterfaceState::Unavailable => 3,
        };
        unsafe {
            *out_state = code;
            *out_signal = signal;
            if !ssid.is_null() && ssid_capacity > 0 {
                let field = std::slice::from_raw_parts_mut(ssid, ssid_capacity as usize);
                fill(field, &name);
            }
        }
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
        let flat: Vec<WsgmWifiNetwork> = networks
            .iter()
            .map(|n| {
                let mut entry = WsgmWifiNetwork {
                    ssid: [0; 64],
                    signal: n.signal,
                    security: match n.security {
                        Security::Open => 0,
                        Security::PersonalPsk => 1,
                        Security::Enterprise => 2,
                        Security::EnhancedOpen => 3,
                        Security::Unsupported => 4,
                    },
                    saved: i32::from(n.saved),
                    connectable: i32::from(n.connectable),
                    connected: i32::from(n.connected),
                };
                fill(&mut entry.ssid, &n.ssid);
                entry
            })
            .collect();
        // Ownership moves to the caller until wsgm_wifi_free takes it back.
        let (pointer, count) = leak_slice(flat);
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
    unsafe { reclaim_slice(items, count) };
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
                //
                // Only a code that really IS a WLAN reason code, though.
                // `Error::Win32` also carries plain statuses (WlanOpenHandle,
                // WlanConnect), and the caller renders anything non-zero here as
                // reason text — which would replace the far more specific error
                // message with "Wi-Fi reason code N". Leaving it at zero is what
                // makes the caller fall back to that message.
                if !out_reason.is_null() {
                    let code = error.win32_code();
                    let carries_reason =
                        wifi::reason::verdict(code) != wifi::reason::Verdict::Unknown;
                    unsafe { *out_reason = if carries_reason { code } else { 0 } };
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
    /// Non-zero when the device has a live connection right now.
    pub connected: i32,
    /// NUL-terminated container id (36-char GUID text), or empty. Matches the
    /// container field of [`WsgmBtAudioContainer`].
    pub container: [u16; 40],
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
        let flat: Vec<WsgmBtDevice> = devices
            .iter()
            .map(|d| {
                let mut entry = WsgmBtDevice {
                    id: [0; 256],
                    name: [0; 128],
                    paired: i32::from(d.paired),
                    can_pair: i32::from(d.can_pair),
                    connected: i32::from(d.connected),
                    container: [0; 40],
                };
                fill(&mut entry.id, &d.id);
                fill(&mut entry.name, &d.name);
                fill(&mut entry.container, &d.container);
                entry
            })
            .collect();
        let (pointer, count) = leak_slice(flat);
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
    unsafe { reclaim_slice(items, count) };
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
/// 4 access-denied, 5 other, 6 an earlier pairing operation for the device is
/// still in flight inside Windows, or -1 when the attempt errored before
/// starting.
/// `message` carries the error text for -1 and the raw
/// `DevicePairingResultStatus` number otherwise — the grouped outcome lumps
/// rare statuses together, and a remote diagnosis needs the exact one.
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
// stays valid until the finished callback has run. Sync as well as Send because
// the watcher handlers are shared across the threads WinRT delivers events on.
unsafe impl Send for Context {}
unsafe impl Sync for Context {}

fn outcome_code(outcome: PairOutcome) -> i32 {
    match outcome {
        PairOutcome::Paired => 0,
        PairOutcome::AlreadyPaired => 1,
        PairOutcome::Rejected => 2,
        PairOutcome::Failed => 3,
        PairOutcome::AccessDenied => 4,
        PairOutcome::Other => 5,
        PairOutcome::AlreadyInProgress => 6,
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
                Ok((outcome, raw_status)) => {
                    let message: Vec<u16> = format!("DevicePairingResultStatus {raw_status}")
                        .encode_utf16()
                        .chain(std::iter::once(0))
                        .collect();
                    on_done(done_context.get(), outcome_code(outcome), message.as_ptr());
                }
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

/// Called when a watched Bluetooth device appears, changes or goes away.
///
/// `change` is 0 added, 1 updated, 2 removed, 3 initial-enumeration-complete.
/// For 3 the other fields are meaningless. Strings are valid only for the
/// duration of the call.
pub type WsgmBtWatchFn = extern "system" fn(
    context: *mut c_void,
    change: i32,
    id: *const u16,
    name: *const u16,
    paired: i32,
    can_pair: i32,
    connected: i32,
    container: *const u16,
);

/// Starts streaming Bluetooth devices instead of enumerating them in one call.
///
/// The blocking list takes about half a minute before it shows anything, which
/// is unusable for a picker. This reports known devices almost immediately and
/// the rest as they are discovered. Restarting is safe.
///
/// # Safety
/// `context` must stay valid until [`wsgm_bt_watch_stop`] returns.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn wsgm_bt_watch_start(
    on_change: WsgmBtWatchFn,
    context: *mut c_void,
) -> i32 {
    guard(move || {
        let cookie = Context(context);
        bluetooth::start_watch(move |event| {
            let empty = [0u16];
            // Added and Updated carry the same record and differ only in the
            // code; Removed carries an id; Ready carries nothing.
            let (change, device, removed_id) = match event {
                bluetooth::WatchEvent::Added(device) => (0, Some(device), None),
                bluetooth::WatchEvent::Updated(device) => (1, Some(device), None),
                bluetooth::WatchEvent::Removed(id) => (2, None, Some(id)),
                bluetooth::WatchEvent::Ready => (3, None, None),
            };
            let id_text = device
                .as_ref()
                .map(|d| d.id.clone())
                .or(removed_id)
                .unwrap_or_default();
            let id: Vec<u16> = id_text.encode_utf16().chain(std::iter::once(0)).collect();
            let name: Vec<u16> = device
                .as_ref()
                .map(|d| d.name.as_str())
                .unwrap_or_default()
                .encode_utf16()
                .chain(std::iter::once(0))
                .collect();
            let container: Vec<u16> = device
                .as_ref()
                .map(|d| d.container.as_str())
                .unwrap_or_default()
                .encode_utf16()
                .chain(std::iter::once(0))
                .collect();
            on_change(
                cookie.get(),
                change,
                if id.len() > 1 { id.as_ptr() } else { empty.as_ptr() },
                name.as_ptr(),
                i32::from(device.as_ref().is_some_and(|d| d.paired)),
                i32::from(device.as_ref().is_some_and(|d| d.can_pair)),
                i32::from(device.as_ref().is_some_and(|d| d.connected)),
                container.as_ptr(),
            );
        })
    })
}

/// One device container that has audio endpoints.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct WsgmBtAudioContainer {
    /// NUL-terminated container id (36-char GUID text). Matches the container
    /// field of [`WsgmBtDevice`].
    pub container: [u16; 40],
    /// Non-zero when the audio device is connected right now.
    pub active: i32,
}

/// Lists EVERY device container that exposes audio endpoints, not only the
/// Bluetooth ones: the enumeration has no transport filter, so HDMI outputs,
/// USB DACs and onboard speakers are in the list too. Intersect it with the
/// container of a known Bluetooth device to decide which rows get a
/// Connect/Disconnect action; [`wsgm_bt_audio_set`] sends a Bluetooth-only
/// kernel-streaming property and fails on anything else. Release with
/// [`wsgm_bt_audio_free`]. Fast: local endpoint enumeration, no radio traffic.
///
/// # Safety
/// Both out pointers must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn wsgm_bt_audio_list(
    out_items: *mut *mut WsgmBtAudioContainer,
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
        let containers = radio_core::audio::audio_containers()?;
        let flat: Vec<WsgmBtAudioContainer> = containers
            .iter()
            .map(|c| {
                let mut entry = WsgmBtAudioContainer {
                    container: [0; 40],
                    active: i32::from(c.active),
                };
                fill(&mut entry.container, &c.container);
                entry
            })
            .collect();
        let (pointer, count) = leak_slice(flat);
        unsafe {
            *out_items = pointer;
            *out_count = count;
        }
        Ok(())
    })
}

/// Releases a list returned by [`wsgm_bt_audio_list`].
///
/// # Safety
/// `items`/`count` must be exactly what `wsgm_bt_audio_list` produced.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn wsgm_bt_audio_free(items: *mut WsgmBtAudioContainer, count: u32) {
    unsafe { reclaim_slice(items, count) };
}

/// Connects or disconnects a paired Bluetooth AUDIO device by its container
/// id — the BtAudio one-shot the Settings app's own Connect button uses. Soft:
/// pairing is untouched.
///
/// # Safety
/// `container` must be a NUL-terminated UTF-16 string.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn wsgm_bt_audio_set(container: *const u16, connect: i32) -> i32 {
    guard(|| {
        let Some(container) = (unsafe { read_utf16(container) }) else {
            return Err(Error::InvalidArgument("container id"));
        };
        radio_core::audio::set_audio_connection(&container, connect != 0)
    })
}

/// Counts Bluetooth devices with a live connection.
///
/// Fast — answered from PnP state, no inquiry — so it is safe to poll from a
/// status tick.
///
/// # Safety
/// `out_count` must point to a writable `u32`.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn wsgm_bt_connected_count(out_count: *mut u32) -> i32 {
    guard(|| {
        if out_count.is_null() {
            return Err(Error::InvalidArgument("out_count"));
        }
        let count = bluetooth::connected_count()?;
        unsafe { *out_count = count };
        Ok(())
    })
}

/// Stops the Bluetooth watcher. Idempotent.
#[unsafe(no_mangle)]
pub extern "system" fn wsgm_bt_watch_stop() -> i32 {
    guard(|| {
        bluetooth::stop_watch();
        Ok(())
    })
}

/// Called when the WLAN service reports a change.
///
/// `event` is 0 scan-list-refreshed, 1 connection-changed.
pub type WsgmWifiEventFn = extern "system" fn(context: *mut c_void, event: i32);

/// Starts delivering live WLAN events instead of polling for them.
///
/// # Safety
/// `context` must stay valid until [`wsgm_wifi_watch_stop`] returns.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn wsgm_wifi_watch_start(
    on_event: WsgmWifiEventFn,
    context: *mut c_void,
) -> i32 {
    guard(move || {
        let cookie = Context(context);
        wifi::notify::start(move |event| {
            let code = match event {
                wifi::notify::WifiEvent::ScanListRefreshed => 0,
                wifi::notify::WifiEvent::ConnectionChanged => 1,
            };
            on_event(cookie.get(), code);
        })
    })
}

/// Stops delivering WLAN events. Idempotent.
#[unsafe(no_mangle)]
pub extern "system" fn wsgm_wifi_watch_stop() -> i32 {
    guard(|| {
        wifi::notify::stop();
        Ok(())
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
