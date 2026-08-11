//! `WSGM.Explorerfy.exe` — Steam launch-option wrapper for games that need Windows
//! Explorer running (some games and mod tools require the shell).
//!
//! Used as `"…\WSGM.Explorerfy.exe" %command%`: it asks the running WSGM shell —
//! over the `WSGM.Explorerfy` named pipe — to drop to desktop mode (Explorer up)
//! for the game's lifetime, launches the wrapped game, stays alive until it exits,
//! then releases (closes the pipe) so WSGM returns to game mode.
//!
//! Native by design: a NativeAOT managed wrapper dies before `main` when Steam
//! launches it. This one has no runtime to fail and logs from the first line.

// No console window when Steam (or the user) launches it; diagnostics go to a file.
#![windows_subsystem = "windows"]

use std::fs::OpenOptions;
use std::io::{Read, Write};
use std::path::PathBuf;
use std::process::Command;

/// The shell's listener. A named pipe path resolves to `\\.\pipe\<name>`.
const PIPE_PATH: &str = r"\\.\pipe\WSGM.Explorerfy";

fn main() {
    let args: Vec<String> = std::env::args().collect();
    log(&format!("invoked (argc={}): {args:?}", args.len()));

    if args.len() < 2 {
        log("no target command supplied; expected: WSGM.Explorerfy.exe <program> [arguments]");
        std::process::exit(64);
    }

    // Hold the pipe open for the whole game lifetime — the connection IS the lease.
    // Closing it (clean exit here, or the OS closing our handles if Steam kills the
    // wrapper) is what returns WSGM to game mode.
    let lease = acquire_lease();

    let target = &args[1];
    let mut command = Command::new(target);
    command.args(&args[2..]);
    // Inherit Steam's environment (SteamAppId/GameId …) and working directory — this
    // wrapper is a direct Steam child, so both are already correct.
    let code = match command.spawn() {
        Ok(mut child) => {
            log(&format!("launched '{target}' (pid {})", child.id()));
            let code = child
                .wait()
                .ok()
                .and_then(|status| status.code())
                .unwrap_or(1);
            log(&format!("target exited with {code}"));
            code
        }
        Err(error) => {
            log(&format!("failed to launch '{target}': {error}"));
            1
        }
    };

    // Explicit release before exit (process teardown skips destructors); the OS
    // closing the handle on any abrupt termination is the same release signal.
    drop(lease);
    std::process::exit(code);
}

/// Connects to the shell and requests Explorer. Returns the open pipe (the lease)
/// or `None` when the shell is unreachable — in which case the game still launches,
/// just without Explorer coordination.
fn acquire_lease() -> Option<std::fs::File> {
    let mut pipe = match OpenOptions::new().read(true).write(true).open(PIPE_PATH) {
        Ok(pipe) => pipe,
        Err(error) => {
            log(&format!(
                "WSGM shell not reachable ({error}); launching without Explorer coordination"
            ));
            return None;
        }
    };

    // Acquire request, then wait for the shell's ack (it brings Explorer up first).
    if let Err(error) = pipe.write_all(&[1u8]).and_then(|()| pipe.flush()) {
        log(&format!(
            "pipe write failed ({error}); launching without Explorer coordination"
        ));
        return None;
    }
    let mut ack = [0u8; 1];
    match pipe.read_exact(&mut ack) {
        Ok(()) => {
            log(if ack[0] == 1 {
                "WSGM confirmed desktop mode (Explorer up)"
            } else {
                "WSGM could not enter desktop mode; launching anyway"
            });
            Some(pipe)
        }
        Err(error) => {
            log(&format!(
                "pipe ack failed ({error}); launching without Explorer coordination"
            ));
            None
        }
    }
}

/// Appends one diagnostic line to `%LOCALAPPDATA%\WSGM\explorerfy.log`, falling back
/// to a file next to the exe (and then the temp dir) so a run is never invisible.
fn log(message: &str) {
    let line = format!(
        "{} [explorerfy] [pid {}] {message}\r\n",
        timestamp(),
        std::process::id()
    );
    for path in log_paths() {
        if let Some(dir) = path.parent() {
            let _ = std::fs::create_dir_all(dir);
        }
        if OpenOptions::new()
            .create(true)
            .append(true)
            .open(&path)
            .and_then(|mut file| file.write_all(line.as_bytes()))
            .is_ok()
        {
            return;
        }
    }
}

fn log_paths() -> Vec<PathBuf> {
    let mut paths = Vec::new();
    if let Ok(local) = std::env::var("LOCALAPPDATA") {
        paths.push(PathBuf::from(local).join("WSGM").join("explorerfy.log"));
    }
    if let Ok(exe) = std::env::current_exe() {
        if let Some(dir) = exe.parent() {
            paths.push(dir.join("explorerfy.log"));
        }
    }
    paths.push(std::env::temp_dir().join("explorerfy.log"));
    paths
}

#[repr(C)]
struct SystemTime {
    year: u16,
    month: u16,
    day_of_week: u16,
    day: u16,
    hour: u16,
    minute: u16,
    second: u16,
    milliseconds: u16,
}

#[link(name = "kernel32")]
unsafe extern "system" {
    fn GetLocalTime(system_time: *mut SystemTime);
}

fn timestamp() -> String {
    let mut now = SystemTime {
        year: 0,
        month: 0,
        day_of_week: 0,
        day: 0,
        hour: 0,
        minute: 0,
        second: 0,
        milliseconds: 0,
    };
    // SAFETY: GetLocalTime writes a SYSTEMTIME into the provided buffer.
    unsafe { GetLocalTime(&mut now) };
    format!(
        "{:04}-{:02}-{:02} {:02}:{:02}:{:02}.{:03}",
        now.year, now.month, now.day, now.hour, now.minute, now.second, now.milliseconds
    )
}
