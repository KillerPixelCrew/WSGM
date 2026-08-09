//! Read-only probe for the radio subsystem.
//!
//! Exists to answer, on a real device and in a real game-mode session, the
//! questions that documentation does not settle: whether WinRT radio control
//! works unpackaged and elevated, and whether the native WLAN scan is blocked by
//! the Windows 11 24H2 location-consent gate.
//!
//! Read-only by default. State-changing checks require an explicit flag,
//! because running the default probe must never cut the connection of the
//! machine it is diagnosing.

use std::process::ExitCode;

use radio_core::{Error, RadioKind, bluetooth, consent, power, radios, request_access, wifi};

/// ERROR_ACCESS_DENIED from a scan entry point is the 24H2 location-consent
/// gate rather than a permissions problem the user can fix by elevating, so it
/// gets called out by name in the probe output.
fn consent_hint(error: &Error) -> &'static str {
    if error.win32_code() == 5 {
        "  <-- precise-location consent missing (Windows 11 24H2 gate)"
    } else {
        ""
    }
}

fn main() -> ExitCode {
    let toggle = std::env::args().any(|a| a == "--toggle");

    println!("wsgm-radio-probe");
    println!("================");

    probe_radio(RadioKind::WiFi);
    probe_radio(RadioKind::Bluetooth);

    print!("radio control access ....... ");
    match request_access() {
        Ok(access) => println!("{access:?} ({})", access.describe()),
        Err(e) => println!("FAILED: {e}"),
    }

    println!();
    let (user_location, machine_location) = consent::location();
    let (user_radios, machine_radios) = consent::radios();
    println!("consent location ........... user={user_location:?} machine={machine_location:?}");
    println!("consent radios ............. user={user_radios:?} machine={machine_radios:?}");

    println!();
    print!("wlan interface state ....... ");
    match wifi::state() {
        Ok(state) => println!("{state:?}"),
        Err(e) => println!("FAILED: {e}"),
    }

    print!("wlan scan request .......... ");
    match wifi::request_scan() {
        Ok(()) => println!("accepted"),
        Err(e) => println!("FAILED: {e}{}", consent_hint(&e)),
    }

    print!("wlan network list .......... ");
    match wifi::networks() {
        Ok(found) => {
            println!("{} network(s)", found.len());
            for n in found.iter().take(12) {
                println!(
                    "    {:<34} {:>3}%  {:?}{}",
                    n.ssid,
                    n.signal,
                    n.security,
                    if n.saved { "  [saved]" } else { "" }
                );
            }
        }
        Err(e) => println!("FAILED: {e}{}", consent_hint(&e)),
    }

    println!();
    // Timed separately because the difference decides the UI: a paired-only
    // query reads known devices, while a full list performs a real inquiry.
    // The number that matters for the picker: how long until the FIRST device
    // appears, not how long the whole enumeration takes.
    let started = std::time::Instant::now();
    let first = std::sync::Arc::new(std::sync::Mutex::new(None::<f32>));
    let seen = std::sync::Arc::new(std::sync::atomic::AtomicUsize::new(0));
    {
        let first = std::sync::Arc::clone(&first);
        let seen = std::sync::Arc::clone(&seen);
        if let Err(e) = bluetooth::start_watch(move |event| {
            if let bluetooth::WatchEvent::Added(_) = event {
                seen.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
                let mut slot = first.lock().unwrap();
                if slot.is_none() {
                    *slot = Some(started.elapsed().as_secs_f32());
                }
            }
        }) {
            println!("bluetooth watch ............ FAILED: {e}");
        }
    }
    std::thread::sleep(std::time::Duration::from_secs(3));
    let elapsed = first.lock().unwrap().take();
    println!(
        "bluetooth watch ............ {} device(s) in 3s, first at {}",
        seen.load(std::sync::atomic::Ordering::Relaxed),
        elapsed.map_or("never".to_owned(), |s| format!("{s:.2}s"))
    );
    bluetooth::stop_watch();

    let started = std::time::Instant::now();
    print!("bluetooth device list ...... ");
    match bluetooth::devices() {
        Ok(found) => {
            println!("{} device(s) in {:.1}s", found.len(), started.elapsed().as_secs_f32());
            for d in found.iter().take(12) {
                println!(
                    "    {:<40} {}{}",
                    if d.name.is_empty() {
                        "(unnamed)"
                    } else {
                        &d.name
                    },
                    if d.paired { "paired" } else { "unpaired" },
                    if d.can_pair { ", pairable" } else { "" }
                );
            }
        }
        Err(e) => println!("FAILED: {e}"),
    }

    // Pairing probe: starts a real ceremony against a device and REJECTS every
    // question, so it answers "does PairingRequested fire, and with what
    // ceremony" without actually pairing anything or changing system state.
    if let Some(index) = std::env::args().position(|a| a == "--pair-probe") {
        let target = std::env::args().nth(index + 1).unwrap_or_default();
        probe_pairing(&target);
    }

    // Restores a device this probe paired, so a pairing test can be repeated.
    if let Some(index) = std::env::args().position(|a| a == "--unpair") {
        let needle = std::env::args().nth(index + 1).unwrap_or_default();
        match bluetooth::devices() {
            Ok(devices) => {
                match devices.iter().find(|d| d.paired && d.name.contains(&needle)) {
                    Some(device) => {
                        print!("unpair \"{}\" ... ", device.name);
                        match bluetooth::unpair(&device.id) {
                            Ok(removed) => println!("{removed}"),
                            Err(e) => println!("FAILED: {e}"),
                        }
                    }
                    None => println!("unpair ..................... no paired device matching {needle:?}"),
                }
            }
            Err(e) => println!("unpair ..................... list failed: {e}"),
        }
    }

    if toggle {
        println!();
        println!("--toggle given: switching each radio off and back on");
        for kind in [RadioKind::WiFi, RadioKind::Bluetooth] {
            for on in [false, true] {
                print!("  {} -> {} ... ", kind.label(), if on { "on" } else { "off" });
                match radios::set_power(kind, on) {
                    Ok(access) => println!("{access:?}"),
                    Err(e) => println!("FAILED: {e}"),
                }
            }
        }
    } else {
        println!();
        println!("(read-only; pass --toggle to also exercise SetStateAsync)");
    }

    ExitCode::SUCCESS
}

/// Starts a pairing against the first unpaired device whose name contains
/// `needle`, reports every question Windows asks, and declines all of them.
///
/// Declining is the point: this has to be safe to run on a machine whose
/// Bluetooth state must not change, while still proving whether the ceremony
/// reaches us at all.
fn probe_pairing(needle: &str) {
    // Found through the watcher, never the blocking list: the list takes ~30 s,
    // by which time a controller has already dropped out of pairing mode and
    // the attempt is testing nothing.
    let found = std::sync::Arc::new(std::sync::Mutex::new(None::<bluetooth::Device>));
    {
        let found = std::sync::Arc::clone(&found);
        let needle = needle.to_owned();
        if let Err(e) = bluetooth::start_watch(move |event| {
            if let bluetooth::WatchEvent::Added(device) | bluetooth::WatchEvent::Updated(device) =
                event
                && !device.paired
                && device.can_pair
                && (needle.is_empty() || device.name.contains(&needle))
            {
                let mut slot = found.lock().unwrap();
                if slot.is_none() {
                    *slot = Some(device);
                }
            }
        }) {
            println!("pair probe ................. watch failed: {e}");
            return;
        }
    }
    // Long enough for the watcher's first burst, short enough that the device is
    // still advertising.
    let deadline = std::time::Instant::now() + std::time::Duration::from_secs(12);
    let target = loop {
        if let Some(device) = found.lock().unwrap().clone() {
            break Some(device);
        }
        if std::time::Instant::now() >= deadline {
            break None;
        }
        std::thread::sleep(std::time::Duration::from_millis(100));
    };
    bluetooth::stop_watch();

    let Some(target) = target else {
        println!("pair probe ................. no unpaired, pairable device matching {needle:?}");
        return;
    };
    println!("pair probe ................. target \"{}\"", target.name);

    // --accept completes the pairing for real; without it every question is
    // declined so the probe cannot change Bluetooth state.
    let accept = std::env::args().any(|a| a == "--accept");
    println!(
        "  mode: {}",
        if accept { "ACCEPT (will really pair)" } else { "decline only" }
    );

    // Retried rather than attempted once: a controller only advertises for a
    // short window, so the probe waits for the user instead of the other way
    // round.
    let overall = std::time::Instant::now();
    let mut attempt = 0;
    while overall.elapsed() < std::time::Duration::from_secs(90) {
        attempt += 1;
        let (tx, rx) = std::sync::mpsc::channel();
        let done = tx.clone();
        let started = std::time::Instant::now();
        if let Err(e) = bluetooth::pair(
            &target.id,
            move |request| {
                println!(
                    "  [{:.1}s] PairingRequested: kind={:?} pin={:?}",
                    started.elapsed().as_secs_f32(),
                    request.kind,
                    request.pin
                );
                // ProvidePin is the only ceremony that needs a value; 0000 is
                // the conventional default for a device with no keypad.
                let pin = if request.kind.needs_pin_from_user() { "0000" } else { "" };
                if let Err(e) = bluetooth::respond(request.token, accept, pin) {
                    println!("  respond failed: {e}");
                }
            },
            move |result| {
                let _ = done.send(result);
            },
        ) {
            println!("  pair() failed to start: {e}");
            return;
        }

        match rx.recv_timeout(std::time::Duration::from_secs(40)) {
            Ok(Ok((outcome, raw_status))) => {
                println!(
                    "  attempt {attempt} finished after {:.1}s: {outcome:?} (DevicePairingResultStatus {raw_status})",
                    started.elapsed().as_secs_f32()
                );
                if outcome.is_success() || outcome == bluetooth::PairOutcome::Rejected {
                    return;
                }
            }
            Ok(Err(e)) => println!("  attempt {attempt} errored: {e}"),
            Err(_) => {
                println!("  attempt {attempt} NEVER FINISHED within 40s");
                return;
            }
        }
        println!("  retrying — put the device into pairing mode now");
        std::thread::sleep(std::time::Duration::from_secs(2));
    }
    println!("  gave up after 90s");
}

fn probe_radio(kind: RadioKind) {
    print!("{:.<27} ", format!("{} radio ", kind.label()));
    match power(kind) {
        Ok(state) => println!("{state:?}"),
        Err(e) => println!("FAILED: {e}"),
    }
}
