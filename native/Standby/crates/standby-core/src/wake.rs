//! Wake-source inventory and disarming.
//!
//! This is the preventive half that HandheldCompanion does not implement at
//! all: rather than letting the device wake and re-suspending it afterwards,
//! stop the device from being allowed to wake the system in the first place.
//!
//! Everything here uses the `DevicePower*` family in `powrprof.dll` rather than
//! shelling out to `powercfg /devicequery` and `/devicedisablewake`. That
//! matters on a non-English Windows install, where the CLI's output is
//! localised and any parser built on it silently finds nothing.

use std::path::{Path, PathBuf};
use std::sync::{Mutex, MutexGuard, OnceLock};

use windows_sys::Win32::System::Power::{
    DEVICEPOWER_CLEAR_WAKEENABLED, DEVICEPOWER_FILTER_DEVICES_PRESENT,
    DEVICEPOWER_FILTER_WAKEENABLED, DEVICEPOWER_FILTER_WAKEPROGRAMMABLE,
    DEVICEPOWER_SET_WAKEENABLED, DevicePowerClose, DevicePowerEnumDevices, DevicePowerOpen,
    DevicePowerSetDeviceState,
};

use crate::error::{Error, Result, last_win32};

/// Largest device description we will accept, in UTF-16 code units.
const NAME_CAPACITY: usize = 512;

/// A device that the platform reports as able to wake the system.
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord)]
pub struct WakeDevice {
    /// The device description, as Windows reports it. This same string is what
    /// [`disarm`] and [`rearm`] take, so it round-trips without translation.
    pub description: String,
    /// True when the device is currently armed to wake the system.
    pub wake_enabled: bool,
}

/// `DevicePowerOpen`/`DevicePowerClose` maintain a process-global device list,
/// so every enumeration and state change is serialised through this lock.
fn device_power_lock() -> MutexGuard<'static, ()> {
    static LOCK: OnceLock<Mutex<()>> = OnceLock::new();
    LOCK.get_or_init(|| Mutex::new(()))
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}

/// RAII guard around the process-global device-power list.
struct DevicePowerSession {
    _guard: MutexGuard<'static, ()>,
}

impl DevicePowerSession {
    fn open() -> Result<Self> {
        let guard = device_power_lock();
        // SAFETY: no preconditions; a zero debug mask is the documented default.
        if !unsafe { DevicePowerOpen(0) } {
            return Err(last_win32("DevicePowerOpen"));
        }
        Ok(Self { _guard: guard })
    }
}

impl Drop for DevicePowerSession {
    fn drop(&mut self) {
        // SAFETY: paired with the DevicePowerOpen in `open`.
        unsafe {
            DevicePowerClose();
        }
    }
}

/// Enumerates devices matching `query_flags` within an already-open session.
fn enumerate(query_flags: u32) -> Result<Vec<String>> {
    let mut out = Vec::new();
    let mut index = 0u32;

    loop {
        let mut buffer = [0u16; NAME_CAPACITY];
        let mut size = std::mem::size_of_val(&buffer) as u32;

        // SAFETY: `buffer` is a live, correctly sized allocation and `size`
        // describes it in bytes, which is what the API expects on input.
        let ok = unsafe {
            DevicePowerEnumDevices(
                index,
                0,
                query_flags,
                buffer.as_mut_ptr().cast::<u8>(),
                &mut size,
            )
        };

        // A false return marks the end of the enumeration as well as failure.
        // There is no way to distinguish the two, so an empty list is reported
        // as success rather than as an error.
        if !ok {
            break;
        }

        let units = (size as usize / std::mem::size_of::<u16>()).min(NAME_CAPACITY);
        let name = String::from_utf16_lossy(&buffer[..units]);
        let name = name.trim_end_matches('\0').trim().to_owned();
        if !name.is_empty() {
            out.push(name);
        }

        index += 1;
    }

    Ok(out)
}

/// Lists devices currently armed to wake the system.
///
/// Equivalent to `powercfg /devicequery wake_armed`, without the localised text.
pub fn list_wake_armed() -> Result<Vec<String>> {
    let _session = DevicePowerSession::open()?;
    let mut devices = enumerate(DEVICEPOWER_FILTER_DEVICES_PRESENT | DEVICEPOWER_FILTER_WAKEENABLED)?;
    devices.sort();
    devices.dedup();
    Ok(devices)
}

/// Lists devices that *could* be armed to wake the system, whether or not they
/// currently are. Equivalent to `powercfg /devicequery wake_programmable`.
pub fn list_wake_programmable() -> Result<Vec<String>> {
    let _session = DevicePowerSession::open()?;
    let mut devices =
        enumerate(DEVICEPOWER_FILTER_DEVICES_PRESENT | DEVICEPOWER_FILTER_WAKEPROGRAMMABLE)?;
    devices.sort();
    devices.dedup();
    Ok(devices)
}

/// Lists every wake-programmable device together with its current armed state.
pub fn inventory() -> Result<Vec<WakeDevice>> {
    let _session = DevicePowerSession::open()?;

    let mut armed =
        enumerate(DEVICEPOWER_FILTER_DEVICES_PRESENT | DEVICEPOWER_FILTER_WAKEENABLED)?;
    armed.sort();

    let mut programmable =
        enumerate(DEVICEPOWER_FILTER_DEVICES_PRESENT | DEVICEPOWER_FILTER_WAKEPROGRAMMABLE)?;
    programmable.sort();
    programmable.dedup();

    // Anything armed but not reported as programmable still belongs in the
    // inventory; it simply cannot be changed.
    for name in &armed {
        if programmable.binary_search(name).is_err() {
            programmable.push(name.clone());
        }
    }
    programmable.sort();
    programmable.dedup();

    Ok(programmable
        .into_iter()
        .map(|description| {
            let wake_enabled = armed.binary_search(&description).is_ok();
            WakeDevice {
                description,
                wake_enabled,
            }
        })
        .collect())
}

/// Applies a wake-state change and verifies it by re-enumeration.
///
/// `DevicePowerSetDeviceState`'s documented return value is ambiguous across
/// Windows versions, so the result is confirmed against the armed-device list
/// rather than trusted. A call that "succeeds" without changing the state is
/// reported as [`Error::NotApplied`].
fn set_wake_state(device: &str, enabled: bool) -> Result<()> {
    let mut wide: Vec<u16> = device.encode_utf16().collect();
    wide.push(0);

    let flags = if enabled {
        DEVICEPOWER_SET_WAKEENABLED
    } else {
        DEVICEPOWER_CLEAR_WAKEENABLED
    };

    {
        let _session = DevicePowerSession::open()?;
        // SAFETY: `wide` is NUL-terminated and outlives the call; the API takes
        // no set-data payload for these two flags.
        unsafe {
            DevicePowerSetDeviceState(wide.as_ptr(), flags, std::ptr::null());
        }
    }

    let armed = list_wake_armed()?;
    let is_armed = armed.iter().any(|name| name == device);
    if is_armed == enabled {
        Ok(())
    } else {
        Err(Error::NotApplied {
            device: device.to_owned(),
        })
    }
}

/// Disarms `device` so it can no longer wake the system.
///
/// Requires an elevated process. The change is verified before returning.
pub fn disarm(device: &str) -> Result<()> {
    set_wake_state(device, false)
}

/// Re-arms `device` so it can wake the system again.
pub fn rearm(device: &str) -> Result<()> {
    set_wake_state(device, true)
}

/// Records which devices this library disarmed, so they can be restored even
/// after a crash or an uninstall.
///
/// The format is deliberately a plain versioned line list rather than JSON:
/// it has no dependency, survives partial writes readably, and is trivial to
/// inspect by hand when diagnosing a device remotely from a pasted log.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct WakeStore {
    path: PathBuf,
    disarmed: Vec<String>,
}

const STORE_HEADER: &str = "standby-wake-store v1";

impl WakeStore {
    /// Opens (or creates) the store at `path`.
    pub fn open(path: impl Into<PathBuf>) -> Result<Self> {
        let path = path.into();
        let disarmed = match std::fs::read_to_string(&path) {
            Ok(text) => Self::parse(&text),
            Err(err) if err.kind() == std::io::ErrorKind::NotFound => Vec::new(),
            Err(err) => return Err(Error::Store(err.to_string())),
        };
        Ok(Self { path, disarmed })
    }

    /// Parses the store format, ignoring a missing or foreign header so a
    /// corrupted file degrades to "nothing to restore" instead of failing.
    fn parse(text: &str) -> Vec<String> {
        let mut lines = text.lines();
        match lines.next() {
            Some(first) if first.trim() == STORE_HEADER => {}
            _ => return Vec::new(),
        }
        let mut out: Vec<String> = lines
            .map(str::trim)
            .filter(|line| !line.is_empty())
            .map(str::to_owned)
            .collect();
        out.sort();
        out.dedup();
        out
    }

    fn serialize(&self) -> String {
        let mut text = String::from(STORE_HEADER);
        for device in &self.disarmed {
            text.push('\n');
            text.push_str(device);
        }
        text.push('\n');
        text
    }

    /// The devices this store believes are disarmed.
    pub fn disarmed(&self) -> &[String] {
        &self.disarmed
    }

    /// Path the store persists to.
    pub fn path(&self) -> &Path {
        &self.path
    }

    fn persist(&self) -> Result<()> {
        if let Some(parent) = self.path.parent()
            && !parent.as_os_str().is_empty()
        {
            std::fs::create_dir_all(parent).map_err(|err| Error::Store(err.to_string()))?;
        }
        std::fs::write(&self.path, self.serialize()).map_err(|err| Error::Store(err.to_string()))
    }

    /// Disarms `device` and records it, so [`WakeStore::restore_all`] can undo
    /// it later. Recording happens only after the change is verified.
    pub fn disarm(&mut self, device: &str) -> Result<()> {
        disarm(device)?;
        if let Err(index) = self.disarmed.binary_search(&device.to_owned()) {
            self.disarmed.insert(index, device.to_owned());
            self.persist()?;
        }
        Ok(())
    }

    /// Re-arms every device this store disarmed.
    ///
    /// Devices that no longer exist are dropped from the store rather than
    /// treated as failures — hardware comes and goes on a handheld dock.
    /// Returns the devices that were successfully re-armed.
    pub fn restore_all(&mut self) -> Result<Vec<String>> {
        let present = list_wake_programmable()?;
        let mut restored = Vec::new();
        let mut failed = Vec::new();

        for device in std::mem::take(&mut self.disarmed) {
            if !present.iter().any(|name| name == &device) {
                continue;
            }
            match rearm(&device) {
                Ok(()) => restored.push(device),
                Err(_) => failed.push(device),
            }
        }

        // Anything that could not be restored stays in the store so a later
        // attempt (or the uninstaller) can try again.
        failed.sort();
        self.disarmed = failed;
        self.persist()?;
        Ok(restored)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn store_round_trips_through_its_text_format() {
        let store = WakeStore {
            path: PathBuf::from("unused"),
            disarmed: vec!["HID Keyboard Device".to_owned(), "Realtek PCIe GbE".to_owned()],
        };
        let parsed = WakeStore::parse(&store.serialize());
        assert_eq!(parsed, store.disarmed);
    }

    #[test]
    fn store_ignores_a_file_without_the_expected_header() {
        assert!(WakeStore::parse("something else\nHID Keyboard Device\n").is_empty());
        assert!(WakeStore::parse("").is_empty());
    }

    #[test]
    fn store_parse_drops_blank_lines_and_duplicates() {
        let text = format!("{STORE_HEADER}\n\nMouse\nMouse\n  Keyboard  \n");
        assert_eq!(
            WakeStore::parse(&text),
            vec!["Keyboard".to_owned(), "Mouse".to_owned()]
        );
    }

    #[test]
    fn device_descriptions_with_spaces_survive_serialization() {
        // Device descriptions routinely contain spaces and punctuation; the
        // line format must not need escaping for them.
        let store = WakeStore {
            path: PathBuf::from("unused"),
            disarmed: vec!["Intel(R) Wi-Fi 6E AX211 160MHz".to_owned()],
        };
        assert_eq!(WakeStore::parse(&store.serialize()), store.disarmed);
    }
}
