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

fn probe_radio(kind: RadioKind) {
    print!("{:.<27} ", format!("{} radio ", kind.label()));
    match power(kind) {
        Ok(state) => println!("{state:?}"),
        Err(e) => println!("FAILED: {e}"),
    }
}
