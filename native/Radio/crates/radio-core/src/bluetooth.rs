//! Bluetooth discovery and pairing via WinRT `Windows.Devices.Enumeration`.
//!
//! WinRT is not a preference here, it is the only option. The Win32 Bluetooth
//! API is documented not to find Low Energy devices at all — which on a
//! handheld would exclude most gamepads — and 32feet.NET is not usable either:
//! its `RadioMode.PowerOff` does not actually power the radio down, its WinRT
//! path calls `CoreWindow.GetForCurrentThread()` (null under Avalonia), and its
//! numeric-comparison handler auto-accepts without asking anyone.
//!
//! Pairing is a two-way conversation. `PairingRequested` fires, we take a
//! deferral so the ceremony waits, hand the request out to whoever is driving
//! this crate, and complete the deferral when they answer. That is what allows
//! WSGM to draw its own PIN UI instead of depending on a system dialog it
//! cannot summon with no shell running.

use std::collections::HashMap;
use std::sync::{Arc, Mutex, OnceLock};

use windows::Devices::Enumeration::{
    DeviceInformation, DeviceInformationCustomPairing, DeviceInformationKind,
    DeviceInformationUpdate, DevicePairingKinds, DevicePairingProtectionLevel,
    DevicePairingRequestedEventArgs, DevicePairingResultStatus, DeviceUnpairingResultStatus,
    DeviceWatcher,
};
use windows::Foundation::{Deferral, TypedEventHandler};
use windows_core::HSTRING;

use crate::error::{Error, Result, winrt};
use crate::mta::{detached_mta, on_mta};

/// A discovered or paired Bluetooth device.
#[derive(Debug, Clone)]
pub struct Device {
    /// The opaque WinRT device id. Stable, and what every other call takes.
    pub id: String,
    /// The display name. May be empty for a device that has not been queried.
    pub name: String,
    /// Whether the device is already paired.
    pub paired: bool,
    /// Whether Windows thinks pairing is currently possible.
    pub can_pair: bool,
}

/// The ceremony Windows wants for a pairing.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PairingKind {
    /// Just confirm the user meant to do this. No PIN involved.
    ConfirmOnly,
    /// Show this PIN; the user types it on the *device*.
    DisplayPin,
    /// Ask the user for the PIN shown on the device.
    ProvidePin,
    /// Show a number and ask whether the device shows the same one.
    ConfirmPinMatch,
    /// Something this build does not handle.
    Unsupported,
}

impl PairingKind {
    fn from_winrt(kind: DevicePairingKinds) -> Self {
        match kind {
            DevicePairingKinds::ConfirmOnly => Self::ConfirmOnly,
            DevicePairingKinds::DisplayPin => Self::DisplayPin,
            DevicePairingKinds::ProvidePin => Self::ProvidePin,
            DevicePairingKinds::ConfirmPinMatch => Self::ConfirmPinMatch,
            _ => Self::Unsupported,
        }
    }

    /// Whether the caller must supply a PIN when accepting.
    #[must_use]
    pub fn needs_pin_from_user(self) -> bool {
        self == Self::ProvidePin
    }
}

/// What the UI must ask, and the token to answer with.
#[derive(Debug, Clone)]
pub struct PairingRequest {
    /// Identifies this request in the reply. Valid until answered once.
    pub token: u32,
    /// Which ceremony to render.
    pub kind: PairingKind,
    /// The PIN to display, for `DisplayPin` and `ConfirmPinMatch`. Empty
    /// otherwise.
    pub pin: String,
    /// The device being paired, for the prompt text.
    pub device_name: String,
}

/// How a pairing attempt ended.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PairOutcome {
    /// Paired successfully.
    Paired,
    /// Already paired before the attempt.
    AlreadyPaired,
    /// The user, or we, declined.
    Rejected,
    /// The device stopped responding.
    Failed,
    /// Access was denied. On an elevated process this is the case to watch:
    /// the broker runs unelevated and may not be able to inspect us.
    AccessDenied,
    /// Any other status.
    Other,
}

impl PairOutcome {
    fn from_winrt(status: DevicePairingResultStatus) -> Self {
        match status {
            DevicePairingResultStatus::Paired => Self::Paired,
            DevicePairingResultStatus::AlreadyPaired => Self::AlreadyPaired,
            DevicePairingResultStatus::RejectedByHandler
            | DevicePairingResultStatus::PairingCanceled => Self::Rejected,
            DevicePairingResultStatus::AccessDenied => Self::AccessDenied,
            DevicePairingResultStatus::Failed
            | DevicePairingResultStatus::ConnectionRejected
            | DevicePairingResultStatus::TooManyConnections
            | DevicePairingResultStatus::HardwareFailure
            | DevicePairingResultStatus::AuthenticationTimeout
            | DevicePairingResultStatus::AuthenticationNotAllowed
            | DevicePairingResultStatus::AuthenticationFailure
            | DevicePairingResultStatus::NoSupportedProfiles => Self::Failed,
            _ => Self::Other,
        }
    }

    /// Whether the device ended up paired, however it got there.
    #[must_use]
    pub fn is_success(self) -> bool {
        matches!(self, Self::Paired | Self::AlreadyPaired)
    }
}

/// A pairing request that is waiting for an answer.
struct Pending {
    args: DevicePairingRequestedEventArgs,
    deferral: Deferral,
}

// SAFETY: DevicePairingRequestedEventArgs and Deferral are both declared
// MarshalingBehavior(Agile) / ThreadingModel(Both) by WinRT, so holding them
// across threads and answering from a different one is explicitly supported.
// That is the whole reason the deferral pattern exists.
unsafe impl Send for Pending {}

fn pending() -> &'static Mutex<HashMap<u32, Pending>> {
    static PENDING: OnceLock<Mutex<HashMap<u32, Pending>>> = OnceLock::new();
    PENDING.get_or_init(|| Mutex::new(HashMap::new()))
}

/// How long a whole pairing ceremony may take before it is abandoned.
///
/// Generous, because it includes the user reading a PIN off a device and typing
/// it back, but finite: Windows will happily wait forever otherwise.
const PAIRING_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(90);

/// Drops every unanswered pairing request, completing their deferrals so the
/// ceremony can unwind instead of waiting on an answer that will never come.
fn discard_pending() {
    let Ok(mut map) = pending().lock() else {
        return;
    };
    for (_, request) in map.drain() {
        let _ = request.deferral.Complete();
    }
}

fn next_token() -> u32 {
    use std::sync::atomic::{AtomicU32, Ordering};
    static NEXT: AtomicU32 = AtomicU32::new(1);
    NEXT.fetch_add(1, Ordering::Relaxed)
}

/// The AQS filter matching Bluetooth and Bluetooth LE devices in one query.
///
/// Both protocol GUIDs are needed: classic alone would miss most modern
/// gamepads and headsets, and LE alone would miss the rest.
const BLUETOOTH_AQS: &str = concat!(
    "(System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\"",
    " OR System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\")"
);

fn read_devices(filter: &str) -> Result<Vec<Device>> {
    let aqs = HSTRING::from(filter);
    // The kind is load-bearing, not a default worth omitting: these filters
    // query System.Devices.Aep.* properties, which only exist on association
    // endpoints. The plain FindAllAsyncAqsFilter overload searches device
    // interfaces instead and silently returns nothing at all.
    // No extra properties: Id, Name and Pairing are always populated, and every
    // additional property is another cross-process read per device.
    let found = DeviceInformation::FindAllAsyncWithKindAqsFilterAndAdditionalProperties(
        &aqs,
        None,
        DeviceInformationKind::AssociationEndpoint,
    )
    .map_err(|e| winrt("DeviceInformation.FindAllAsync (association endpoints)", e))?
    .join()
    .map_err(|e| winrt("DeviceInformation.FindAllAsync (join)", e))?;

    let mut devices = Vec::new();
    for info in &found {
        let id = info
            .Id()
            .map(|s| s.to_string_lossy())
            .unwrap_or_default();
        if id.is_empty() {
            continue;
        }
        let name = info
            .Name()
            .map(|s| s.to_string_lossy())
            .unwrap_or_default();
        let (paired, can_pair) = match info.Pairing() {
            Ok(pairing) => (
                pairing.IsPaired().unwrap_or(false),
                pairing.CanPair().unwrap_or(false),
            ),
            Err(_) => (false, false),
        };
        devices.push(Device {
            id,
            name,
            paired,
            can_pair,
        });
    }
    // Unnamed devices are near-useless in a picker; sort them last rather than
    // hiding them, because a gamepad can legitimately advertise late.
    devices.sort_by(|a, b| {
        a.name
            .is_empty()
            .cmp(&b.name.is_empty())
            .then_with(|| a.name.cmp(&b.name))
    });
    Ok(devices)
}

/// Lists Bluetooth devices that are currently visible or already known.
///
/// Blocks for the full discovery — measured at ~30 s, and the same with a
/// paired-only filter, because the AQS is evaluated against a live inquiry
/// either way. Use [`start_watch`] for anything a user is waiting on.
pub fn devices() -> Result<Vec<Device>> {
    on_mta(|| read_devices(BLUETOOTH_AQS))?
}

/// Something the device watcher observed.
#[derive(Debug, Clone)]
pub enum WatchEvent {
    /// A device appeared. Known and paired devices arrive first, within
    /// milliseconds; the rest trickle in as the inquiry progresses.
    Added(Device),

    /// A device's properties changed — most usefully, it became paired.
    Updated(Device),

    /// A device went out of range.
    Removed(String),

    /// The initial enumeration finished. Anything after this is a live change.
    Ready,
}

/// The live watcher, kept alive because dropping it stops the enumeration.
static WATCHER: OnceLock<Mutex<Option<DeviceWatcher>>> = OnceLock::new();

fn watcher_slot() -> &'static Mutex<Option<DeviceWatcher>> {
    WATCHER.get_or_init(|| Mutex::new(None))
}

fn read_one(info: &DeviceInformation) -> Option<Device> {
    let id = info.Id().map(|s| s.to_string_lossy()).unwrap_or_default();
    if id.is_empty() {
        return None;
    }
    let name = info.Name().map(|s| s.to_string_lossy()).unwrap_or_default();
    let (paired, can_pair) = match info.Pairing() {
        Ok(pairing) => (
            pairing.IsPaired().unwrap_or(false),
            pairing.CanPair().unwrap_or(false),
        ),
        Err(_) => (false, false),
    };
    Some(Device {
        id,
        name,
        paired,
        can_pair,
    })
}

/// Starts streaming Bluetooth devices to `on_event`.
///
/// This is what makes the picker usable: a blocking enumeration takes about
/// half a minute before showing anything at all, whereas a watcher reports the
/// already-known devices almost immediately and adds the rest as they are
/// found. Restarting is safe — any previous watcher is stopped first.
///
/// Events arrive on WinRT worker threads, so `on_event` must be cheap and must
/// not block; the caller marshals to its own UI thread.
pub fn start_watch<F>(on_event: F) -> Result<()>
where
    F: Fn(WatchEvent) + Send + Sync + 'static,
{
    stop_watch();
    let handler = Arc::new(on_event);
    let added = Arc::clone(&handler);
    let updated = Arc::clone(&handler);
    let removed = Arc::clone(&handler);
    let ready = Arc::clone(&handler);

    on_mta(move || {
        let aqs = HSTRING::from(BLUETOOTH_AQS);
        // The kind is load-bearing: these filters query System.Devices.Aep.*
        // properties, which exist only on association endpoints. Watching device
        // interfaces instead silently finds nothing.
        let watcher = DeviceInformation::CreateWatcherWithKindAqsFilterAndAdditionalProperties(
            &aqs,
            None,
            DeviceInformationKind::AssociationEndpoint,
        )
        .map_err(|e| winrt("DeviceInformation.CreateWatcher", e))?;

        watcher
            .Added(&TypedEventHandler::new(
                move |_, info: windows_core::Ref<'_, DeviceInformation>| {
                    if let Some(info) = info.as_ref()
                        && let Some(device) = read_one(info)
                    {
                        added(WatchEvent::Added(device));
                    }
                    Ok(())
                },
            ))
            .map_err(|e| winrt("DeviceWatcher.Added", e))?;

        watcher
            .Updated(&TypedEventHandler::new(
                move |_, update: windows_core::Ref<'_, DeviceInformationUpdate>| {
                    // An update carries only the changed properties, so the id is
                    // re-resolved to pick up a pairing change.
                    if let Some(update) = update.as_ref()
                        && let Ok(id) = update.Id()
                        && let Ok(operation) = DeviceInformation::CreateFromIdAsync(&id)
                        && let Ok(info) = operation.join()
                        && let Some(device) = read_one(&info)
                    {
                        updated(WatchEvent::Updated(device));
                    }
                    Ok(())
                },
            ))
            .map_err(|e| winrt("DeviceWatcher.Updated", e))?;

        watcher
            .Removed(&TypedEventHandler::new(
                move |_, update: windows_core::Ref<'_, DeviceInformationUpdate>| {
                    if let Some(update) = update.as_ref()
                        && let Ok(id) = update.Id()
                    {
                        removed(WatchEvent::Removed(id.to_string_lossy()));
                    }
                    Ok(())
                },
            ))
            .map_err(|e| winrt("DeviceWatcher.Removed", e))?;

        watcher
            .EnumerationCompleted(&TypedEventHandler::new(move |_, _| {
                ready(WatchEvent::Ready);
                Ok(())
            }))
            .map_err(|e| winrt("DeviceWatcher.EnumerationCompleted", e))?;

        watcher
            .Start()
            .map_err(|e| winrt("DeviceWatcher.Start", e))?;
        if let Ok(mut slot) = watcher_slot().lock() {
            *slot = Some(watcher);
        }
        Ok(())
    })?
}

/// Stops the watcher started by [`start_watch`]. Idempotent.
pub fn stop_watch() {
    let existing = watcher_slot().lock().ok().and_then(|mut slot| slot.take());
    let Some(watcher) = existing else {
        return;
    };
    // Stopping touches the same apartment the watcher was created in.
    let _ = on_mta(move || {
        let _ = watcher.Stop();
    });
}

/// Lists only devices that are already paired.
pub fn paired_devices() -> Result<Vec<Device>> {
    on_mta(|| {
        read_devices(&format!(
            "{BLUETOOTH_AQS} AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True"
        ))
    })?
}

/// Starts pairing a device.
///
/// Returns as soon as the attempt is under way. `on_request` is called when
/// Windows asks a question — possibly on an arbitrary thread — and the caller
/// must eventually answer with [`respond`]. `on_finished` reports the outcome.
///
/// The attempt runs on its own MTA thread: it blocks until the ceremony
/// completes, and answering it from the shared worker would deadlock.
pub fn pair<R, F>(device_id: &str, on_request: R, on_finished: F) -> Result<()>
where
    R: Fn(PairingRequest) + Send + 'static,
    F: FnOnce(std::result::Result<PairOutcome, Error>) + Send + 'static,
{
    if device_id.is_empty() {
        return Err(Error::InvalidArgument("empty device id"));
    }
    let id = device_id.to_owned();
    detached_mta(move || {
        on_finished(pair_blocking(&id, on_request));
    })
}

fn pair_blocking<R>(device_id: &str, on_request: R) -> Result<PairOutcome>
where
    R: Fn(PairingRequest) + Send + 'static,
{
    let id = HSTRING::from(device_id);
    let info = DeviceInformation::CreateFromIdAsync(&id)
        .map_err(|e| winrt("DeviceInformation.CreateFromIdAsync", e))?
        .join()
        .map_err(|e| winrt("DeviceInformation.CreateFromIdAsync (join)", e))?;
    let device_name = info.Name().map(|s| s.to_string_lossy()).unwrap_or_default();

    let pairing = info
        .Pairing()
        .map_err(|e| winrt("DeviceInformation.Pairing", e))?;
    let custom: DeviceInformationCustomPairing = pairing
        .Custom()
        .map_err(|e| winrt("DeviceInformationPairing.Custom", e))?;

    let handler_name = device_name.clone();
    let token = custom
        .PairingRequested(&TypedEventHandler::new(
            move |_sender, args: windows_core::Ref<'_, DevicePairingRequestedEventArgs>| {
                let Some(args) = args.as_ref() else {
                    return Ok(());
                };
                // Take a deferral first: without it the ceremony is answered the
                // moment this handler returns, which leaves no time to ask anyone.
                let deferral = args.GetDeferral()?;
                let kind = PairingKind::from_winrt(args.PairingKind().unwrap_or_default());
                let pin = args.Pin().map(|s| s.to_string_lossy()).unwrap_or_default();
                let token = next_token();
                if let Ok(mut map) = pending().lock() {
                    map.insert(
                        token,
                        Pending {
                            args: args.clone(),
                            deferral,
                        },
                    );
                }
                on_request(PairingRequest {
                    token,
                    kind,
                    pin,
                    device_name: handler_name.clone(),
                });
                Ok(())
            },
        ))
        .map_err(|e| winrt("DeviceInformationCustomPairing.PairingRequested", e))?;

    // Every ceremony we can actually render, and deliberately NOT DisplayPin
    // together with ProvidePin: when both are offered Windows picks DisplayPin
    // and the pairing then fails.
    let kinds = DevicePairingKinds::ConfirmOnly
        | DevicePairingKinds::ProvidePin
        | DevicePairingKinds::ConfirmPinMatch;

    // Bounded, because PairAsync has no timeout of its own and can wait
    // indefinitely on a device that never answers. A row stuck on "Working..."
    // with no way out is worse than a failure the user can retry.
    let (finished_tx, finished_rx) = std::sync::mpsc::channel();
    let operation = custom
        .PairWithProtectionLevelAsync(kinds, DevicePairingProtectionLevel::Default)
        .map_err(|e| winrt("DeviceInformationCustomPairing.PairAsync", e))?;
    operation
        .when(move |outcome| {
            let _ = finished_tx.send(outcome);
        })
        .map_err(|e| winrt("DeviceInformationCustomPairing.PairAsync (when)", e))?;

    let result = match finished_rx.recv_timeout(PAIRING_TIMEOUT) {
        Ok(outcome) => outcome
            .map_err(|e| winrt("DeviceInformationCustomPairing.PairAsync (result)", e)),
        Err(_) => {
            // Drop any question still waiting for an answer, or its deferral
            // would keep the ceremony alive after we have given up on it.
            discard_pending();
            let _ = custom.RemovePairingRequested(token);
            return Err(Error::TimedOut("pairing"));
        }
    };

    let _ = custom.RemovePairingRequested(token);

    let result = result?;
    let status = result
        .Status()
        .map_err(|e| winrt("DevicePairingResult.Status", e))?;
    Ok(PairOutcome::from_winrt(status))
}

/// Answers a pairing request raised through [`pair`].
///
/// `pin` is used only for [`PairingKind::ProvidePin`]. Answering a token twice
/// is a no-op, because the request is removed when it is answered.
pub fn respond(token: u32, accept: bool, pin: &str) -> Result<()> {
    let Some(request) = pending().lock().ok().and_then(|mut m| m.remove(&token)) else {
        return Err(Error::NotFound("pairing request"));
    };
    if accept {
        let outcome = if pin.is_empty() {
            request.args.Accept()
        } else {
            request.args.AcceptWithPin(&HSTRING::from(pin))
        };
        outcome.map_err(|e| winrt("DevicePairingRequestedEventArgs.Accept", e))?;
    }
    // Completing the deferral without accepting is how a rejection is
    // expressed; there is no explicit Reject.
    request
        .deferral
        .Complete()
        .map_err(|e| winrt("Deferral.Complete", e))?;
    Ok(())
}

/// Removes a pairing.
pub fn unpair(device_id: &str) -> Result<bool> {
    let id = device_id.to_owned();
    on_mta(move || {
        let info = DeviceInformation::CreateFromIdAsync(&HSTRING::from(id.as_str()))
            .map_err(|e| winrt("DeviceInformation.CreateFromIdAsync", e))?
            .join()
            .map_err(|e| winrt("DeviceInformation.CreateFromIdAsync (join)", e))?;
        let status = info
            .Pairing()
            .map_err(|e| winrt("DeviceInformation.Pairing", e))?
            .UnpairAsync()
            .map_err(|e| winrt("DeviceInformationPairing.UnpairAsync", e))?
            .join()
            .map_err(|e| winrt("DeviceInformationPairing.UnpairAsync (join)", e))?
            .Status()
            .map_err(|e| winrt("DeviceUnpairingResult.Status", e))?;
        Ok(matches!(
            status,
            DeviceUnpairingResultStatus::Unpaired | DeviceUnpairingResultStatus::AlreadyUnpaired
        ))
    })?
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_provide_pin_asks_the_user_to_type_something() {
        assert!(PairingKind::ProvidePin.needs_pin_from_user());
        // DisplayPin shows a PIN, it does not collect one — treating it as an
        // input prompt would stall the ceremony.
        assert!(!PairingKind::DisplayPin.needs_pin_from_user());
        assert!(!PairingKind::ConfirmPinMatch.needs_pin_from_user());
        assert!(!PairingKind::ConfirmOnly.needs_pin_from_user());
    }

    #[test]
    fn both_paired_outcomes_count_as_success() {
        assert!(PairOutcome::Paired.is_success());
        assert!(PairOutcome::AlreadyPaired.is_success());
        assert!(!PairOutcome::Rejected.is_success());
        assert!(!PairOutcome::AccessDenied.is_success());
    }

    #[test]
    fn answering_an_unknown_token_is_reported_not_ignored() {
        // A silent success here would leave the UI believing it had replied.
        assert!(respond(u32::MAX, true, "").is_err());
    }

    #[test]
    fn the_discovery_filter_covers_classic_and_low_energy() {
        // Classic only would miss most modern gamepads; LE only would miss the rest.
        assert!(BLUETOOTH_AQS.contains("e0cbf06c-cd8b-4647-bb8a-263b43f0f974"));
        assert!(BLUETOOTH_AQS.contains("bb7bb05e-5972-42b5-94fc-76eaa7084d49"));
    }
}
