//! Connect/disconnect for paired Bluetooth AUDIO devices.
//!
//! Windows has no public "connect to this paired Bluetooth device" API. What
//! the Settings app's Connect button actually does — and the only supported
//! path — is audio-specific: ask the Bluetooth audio driver to page the
//! device, via a kernel-streaming property on the endpoint's adapter filter
//! (`KSPROPSETID_BtAudio`, `KSPROPERTY_ONESHOT_RECONNECT` /
//! `KSPROPERTY_ONESHOT_DISCONNECT`). The same mechanism ToothTray and every
//! other "connect my headphones" utility uses. Non-audio devices (HID mice,
//! gamepads, BLE peripherals) reconnect on their own initiative when used;
//! there is nothing for the host to do, and Settings shows no Connect button
//! for them either.
//!
//! Endpoints are correlated to a Bluetooth device through
//! `PKEY_Device_ContainerId`: the audio endpoints and the device's
//! association endpoint share one container, so the panel can decide which
//! rows get a Connect action at all.

use std::collections::HashMap;

use windows::Win32::Foundation::PROPERTYKEY;
use windows::Win32::Media::Audio::{
    DEVICE_STATE, DEVICE_STATE_ACTIVE, DEVICE_STATEMASK_ALL, IDeviceTopology, IMMDevice,
    IMMDeviceEnumerator, MMDeviceEnumerator, eCapture, eRender,
};
use windows::Win32::Media::KernelStreaming::{
    IKsControl, KSIDENTIFIER, KSIDENTIFIER_0, KSIDENTIFIER_0_0, KSPROPERTY_TYPE_GET,
};
use windows::Win32::System::Com::{CLSCTX_ALL, CoCreateInstance, CoTaskMemFree, STGM_READ};
use windows::Win32::System::Variant::VT_CLSID;
use windows_core::{GUID, PCWSTR};

use crate::error::{Error, Result, winrt};
use crate::mta::on_mta;

/// `KSPROPSETID_BtAudio` from ksmedia.h. Not exported by the `windows` crate.
const KSPROPSETID_BT_AUDIO: GUID = GUID::from_u128(0x7fa06c40_b8f6_4c7e_8556_e8c33a12e54d);

/// `KSPROPERTY_BTAUDIO` member ordinals from ksmedia.h.
const ONESHOT_RECONNECT: u32 = 0;
const ONESHOT_DISCONNECT: u32 = 1;

/// `PKEY_Device_ContainerId` — the container an audio endpoint belongs to.
const PKEY_DEVICE_CONTAINER_ID: PROPERTYKEY = PROPERTYKEY {
    fmtid: GUID::from_u128(0x8c7ed206_3f8a_4827_b3ab_ae9e1faefc6c),
    pid: 2,
};

/// One device container that has audio endpoints.
///
/// EVERY audio container on the machine, not only the Bluetooth ones: the
/// enumeration has no transport filter, so HDMI outputs, USB DACs and onboard
/// speakers appear here too. Intersecting this list with the containers of the
/// known Bluetooth devices is the caller's job, and it is what decides which
/// rows get a Connect action — [`set_audio_connection`] sends a Bluetooth-only
/// kernel-streaming property and fails on anything else.
#[derive(Debug, Clone)]
pub struct AudioContainer {
    /// The container id, canonically formatted (see [`format_guid`]).
    pub container: String,
    /// Whether any endpoint in the container is currently active, i.e. the
    /// audio device is connected right now.
    pub active: bool,
}

/// Formats a GUID the one way this crate ever does, so container ids compare
/// as plain strings across the ABI.
#[must_use]
pub fn format_guid(guid: &GUID) -> String {
    format!(
        "{:08x}-{:04x}-{:04x}-{:02x}{:02x}-{:02x}{:02x}{:02x}{:02x}{:02x}{:02x}",
        guid.data1,
        guid.data2,
        guid.data3,
        guid.data4[0],
        guid.data4[1],
        guid.data4[2],
        guid.data4[3],
        guid.data4[4],
        guid.data4[5],
        guid.data4[6],
        guid.data4[7],
    )
}

fn enumerator() -> Result<IMMDeviceEnumerator> {
    // SAFETY: plain COM activation on the MTA worker.
    unsafe { CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL) }
        .map_err(|e| winrt("CoCreateInstance(MMDeviceEnumerator)", e))
}

/// Reads the container id off an audio endpoint. `None` rather than an error:
/// an endpoint without one simply cannot be matched to a Bluetooth device.
fn read_container(device: &IMMDevice) -> Option<String> {
    // SAFETY: property store reads on a live endpoint; VT checked before the
    // union is touched.
    unsafe {
        let store = device.OpenPropertyStore(STGM_READ).ok()?;
        let value = store.GetValue(&PKEY_DEVICE_CONTAINER_ID).ok()?;
        let inner = &value.Anonymous.Anonymous;
        if inner.vt != VT_CLSID {
            return None;
        }
        let guid = inner.Anonymous.puuid;
        if guid.is_null() {
            return None;
        }
        Some(format_guid(&*guid))
    }
}

/// All containers that expose audio endpoints, with their live state.
///
/// `DEVICE_STATEMASK_ALL` on purpose: a disconnected Bluetooth headset's
/// endpoints are `DEVICE_STATE_UNPLUGGED`, and those are exactly the ones a
/// Connect button exists for.
pub fn audio_containers() -> Result<Vec<AudioContainer>> {
    on_mta(|| {
        let enumerator = enumerator()?;
        let mut groups: HashMap<String, bool> = HashMap::new();
        for flow in [eRender, eCapture] {
            // SAFETY: enumeration over a COM collection; indexes bounded by
            // GetCount.
            unsafe {
                let devices = enumerator
                    .EnumAudioEndpoints(flow, DEVICE_STATE(DEVICE_STATEMASK_ALL))
                    .map_err(|e| winrt("IMMDeviceEnumerator.EnumAudioEndpoints", e))?;
                let count = devices
                    .GetCount()
                    .map_err(|e| winrt("IMMDeviceCollection.GetCount", e))?;
                for i in 0..count {
                    let Ok(device) = devices.Item(i) else {
                        continue;
                    };
                    let Some(container) = read_container(&device) else {
                        continue;
                    };
                    let active = device
                        .GetState()
                        .map(|state| state == DEVICE_STATE_ACTIVE)
                        .unwrap_or(false);
                    let entry = groups.entry(container).or_insert(false);
                    *entry = *entry || active;
                }
            }
        }
        Ok(groups
            .into_iter()
            .map(|(container, active)| AudioContainer { container, active })
            .collect())
    })?
}

/// Asks the Bluetooth audio driver to connect or disconnect the device whose
/// container is `container_id`. Soft by design: pairing is untouched.
///
/// Every endpoint in the container is tried until one accepts the request —
/// a headset exposes several (render, capture, hands-free) and it only takes
/// one reachable filter to page the device.
pub fn set_audio_connection(container_id: &str, connect: bool) -> Result<()> {
    let target = container_id.to_ascii_lowercase();
    on_mta(move || {
        let enumerator = enumerator()?;
        let mut last_error: Option<Error> = None;
        let mut matched = false;
        for flow in [eRender, eCapture] {
            // SAFETY: as in audio_containers; the one-shot itself is sent in
            // send_oneshot.
            unsafe {
                let devices = enumerator
                    .EnumAudioEndpoints(flow, DEVICE_STATE(DEVICE_STATEMASK_ALL))
                    .map_err(|e| winrt("IMMDeviceEnumerator.EnumAudioEndpoints", e))?;
                let count = devices
                    .GetCount()
                    .map_err(|e| winrt("IMMDeviceCollection.GetCount", e))?;
                for i in 0..count {
                    let Ok(device) = devices.Item(i) else {
                        continue;
                    };
                    if read_container(&device).as_deref() != Some(target.as_str()) {
                        continue;
                    }
                    matched = true;
                    match send_oneshot(&enumerator, &device, connect) {
                        Ok(()) => return Ok(()),
                        Err(error) => last_error = Some(error),
                    }
                }
            }
        }
        Err(last_error.unwrap_or({
            if matched {
                Error::NotFound("a controllable audio endpoint for this device")
            } else {
                Error::NotFound("audio endpoints for this device")
            }
        }))
    })?
}

/// Walks endpoint → connector → adapter device and sends the BtAudio one-shot
/// to the adapter's kernel-streaming filter — the topology documented in
/// "Using the IKsControl Interface to Access Audio Properties".
///
/// # Safety
/// `endpoint` must be a live endpoint from `enumerator`'s collection.
unsafe fn send_oneshot(
    enumerator: &IMMDeviceEnumerator,
    endpoint: &IMMDevice,
    connect: bool,
) -> Result<()> {
    unsafe {
        let topology: IDeviceTopology = endpoint
            .Activate(CLSCTX_ALL, None)
            .map_err(|e| winrt("IMMDevice.Activate(IDeviceTopology)", e))?;
        let connector = topology
            .GetConnector(0)
            .map_err(|e| winrt("IDeviceTopology.GetConnector", e))?;
        let adapter_id = connector
            .GetDeviceIdConnectedTo()
            .map_err(|e| winrt("IConnector.GetDeviceIdConnectedTo", e))?;
        let adapter = enumerator.GetDevice(PCWSTR(adapter_id.0));
        // The id string is CoTaskMemAlloc'd and ours to release.
        CoTaskMemFree(Some(adapter_id.0.cast()));
        let adapter = adapter.map_err(|e| winrt("IMMDeviceEnumerator.GetDevice(adapter)", e))?;
        let ks: IKsControl = adapter
            .Activate(CLSCTX_ALL, None)
            .map_err(|e| winrt("IMMDevice.Activate(IKsControl)", e))?;

        let property = KSIDENTIFIER {
            Anonymous: KSIDENTIFIER_0 {
                Anonymous: KSIDENTIFIER_0_0 {
                    Set: KSPROPSETID_BT_AUDIO,
                    Id: if connect {
                        ONESHOT_RECONNECT
                    } else {
                        ONESHOT_DISCONNECT
                    },
                    Flags: KSPROPERTY_TYPE_GET,
                },
            },
        };
        let mut returned = 0u32;
        ks.KsProperty(
            &property,
            size_of::<KSIDENTIFIER>() as u32,
            std::ptr::null_mut(),
            0,
            &mut returned,
        )
        .map_err(|e| winrt("IKsControl.KsProperty(BtAudio one-shot)", e))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn guids_format_lowercase_and_unbraced() {
        // The exact shape both sides of the ABI compare as plain strings.
        let guid = GUID::from_u128(0x8c7ed206_3f8a_4827_b3ab_ae9e1faefc6c);
        assert_eq!(format_guid(&guid), "8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c");
    }

    #[test]
    fn the_btaudio_oneshot_ordinals_match_ksmedia_h() {
        // typedef enum { KSPROPERTY_ONESHOT_RECONNECT, KSPROPERTY_ONESHOT_DISCONNECT }
        assert_eq!(ONESHOT_RECONNECT, 0);
        assert_eq!(ONESHOT_DISCONNECT, 1);
    }
}
