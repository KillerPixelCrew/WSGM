//! Standalone logger for the wrapper: `%LOCALAPPDATA%\WSGM\deelevate.log`, with a
//! fallback next to the exe (then the temp dir) so a run is never invisible — a
//! launch wrapper must never fail merely because diagnostics cannot be written.

use std::fs::OpenOptions;
use std::io::Write;
use std::path::PathBuf;

use windows_sys::Win32::Foundation::SYSTEMTIME;
use windows_sys::Win32::System::SystemInformation::GetLocalTime;

pub fn info(message: &str) {
    write("info ", message);
}

pub fn error(message: &str) {
    write("error", message);
}

fn write(level: &str, message: &str) {
    let line = format!(
        "{} [{level}] [pid {}] {message}\r\n",
        timestamp(),
        std::process::id()
    );
    for path in paths() {
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

fn paths() -> Vec<PathBuf> {
    let mut paths = Vec::new();
    if let Ok(local) = std::env::var("LOCALAPPDATA") {
        paths.push(PathBuf::from(local).join("WSGM").join("deelevate.log"));
    }
    if let Ok(exe) = std::env::current_exe()
        && let Some(dir) = exe.parent() {
            paths.push(dir.join("deelevate.log"));
        }
    paths.push(std::env::temp_dir().join("deelevate.log"));
    paths
}

fn timestamp() -> String {
    // SAFETY: GetLocalTime fills the provided SYSTEMTIME.
    let now: SYSTEMTIME = unsafe {
        let mut now = std::mem::zeroed();
        GetLocalTime(&mut now);
        now
    };
    format!(
        "{:04}-{:02}-{:02} {:02}:{:02}:{:02}.{:03}",
        now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond, now.wMilliseconds
    )
}
