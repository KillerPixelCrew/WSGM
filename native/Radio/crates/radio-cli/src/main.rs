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

use radio_core::{Error, RadioKind, consent, power, radios, request_access, wifi};

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
