//! Per-launch named pipe between the elevated parent (server) and the medium child
//! (client): carries the payload and the lifetime signal. Thin `windows-sys`
//! wrappers with framed u32/string helpers.

use std::os::windows::ffi::OsStrExt;
use std::time::{Duration, Instant};

use windows_sys::Win32::Foundation::{CloseHandle, GetLastError, HANDLE, INVALID_HANDLE_VALUE};
use windows_sys::Win32::Storage::FileSystem::{CreateFileW, ReadFile, WriteFile};
use windows_sys::Win32::System::Pipes::{ConnectNamedPipe, CreateNamedPipeW};

const PIPE_ACCESS_DUPLEX: u32 = 0x0000_0003;
const PIPE_TYPE_BYTE: u32 = 0x0000_0000;
const PIPE_READMODE_BYTE: u32 = 0x0000_0000;
const PIPE_WAIT: u32 = 0x0000_0000;
const OPEN_EXISTING: u32 = 3;
const GENERIC_READ: u32 = 0x8000_0000;
const GENERIC_WRITE: u32 = 0x4000_0000;
const ERROR_PIPE_CONNECTED: u32 = 535;

const MAX_STRING_BYTES: u32 = 4 * 1024 * 1024;
const MAX_COUNT: u32 = 16_384;

/// An owned pipe handle (closed on drop).
pub struct Pipe {
    handle: HANDLE,
}

// The handle is a plain kernel handle usable from any thread.
unsafe impl Send for Pipe {}

impl Drop for Pipe {
    fn drop(&mut self) {
        // SAFETY: we own the handle.
        unsafe { CloseHandle(self.handle) };
    }
}

impl Pipe {
    /// Creates the server end for `\\.\pipe\<name>`.
    pub fn create_server(name: &str) -> Result<Pipe, String> {
        let wide = wide(&format!(r"\\.\pipe\{name}"));
        // SAFETY: standard CreateNamedPipeW with a null security descriptor.
        let handle = unsafe {
            CreateNamedPipeW(
                wide.as_ptr(),
                PIPE_ACCESS_DUPLEX,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                1,
                64 * 1024,
                64 * 1024,
                0,
                std::ptr::null(),
            )
        };
        if handle == INVALID_HANDLE_VALUE {
            return Err(format!("CreateNamedPipeW error {}", last_error()));
        }
        Ok(Pipe { handle })
    }

    /// Waits for the child to connect, force-exiting if it never does (a hung
    /// wrapper would leave Steam thinking the game runs forever).
    pub fn wait_for_client(&self, timeout: Duration) -> Result<(), String> {
        let done = std::sync::Arc::new(std::sync::atomic::AtomicBool::new(false));
        {
            let done = done.clone();
            std::thread::spawn(move || {
                std::thread::sleep(timeout);
                if !done.load(std::sync::atomic::Ordering::SeqCst) {
                    crate::log::error("timed out waiting for the medium child to connect");
                    std::process::exit(1);
                }
            });
        }
        // SAFETY: blocking connect on our server handle.
        let ok = unsafe { ConnectNamedPipe(self.handle, std::ptr::null_mut()) };
        done.store(true, std::sync::atomic::Ordering::SeqCst);
        if ok != 0 {
            return Ok(());
        }
        let error = last_error();
        if error == ERROR_PIPE_CONNECTED {
            Ok(())
        } else {
            Err(format!("ConnectNamedPipe error {error}"))
        }
    }

    /// Opens the client end, retrying until the server exists or `timeout` passes.
    pub fn connect_client(name: &str, timeout: Duration) -> Result<Pipe, String> {
        let wide = wide(&format!(r"\\.\pipe\{name}"));
        let deadline = Instant::now() + timeout;
        loop {
            // SAFETY: opening an existing named pipe by path.
            let handle = unsafe {
                CreateFileW(
                    wide.as_ptr(),
                    GENERIC_READ | GENERIC_WRITE,
                    0,
                    std::ptr::null(),
                    OPEN_EXISTING,
                    0,
                    std::ptr::null_mut(),
                )
            };
            if handle != INVALID_HANDLE_VALUE {
                return Ok(Pipe { handle });
            }
            if Instant::now() >= deadline {
                return Err(format!("CreateFileW pipe error {}", last_error()));
            }
            std::thread::sleep(Duration::from_millis(50));
        }
    }

    /// A non-owning, `Send` view for the lifetime-watcher thread. The owning
    /// `Pipe` closes the handle; the watcher only reads it.
    pub fn detached(&self) -> RawPipe {
        RawPipe {
            handle: self.handle,
        }
    }

    pub fn write_u32(&self, value: u32) -> Result<(), String> {
        write_all(self.handle, &value.to_le_bytes())
    }

    pub fn read_u32(&self) -> Result<u32, String> {
        let mut bytes = [0u8; 4];
        read_exact(self.handle, &mut bytes)?;
        Ok(u32::from_le_bytes(bytes))
    }

    /// Reads a u32 bounded as a collection count.
    pub fn read_count(&self) -> Result<u32, String> {
        let value = self.read_u32()?;
        if value > MAX_COUNT {
            return Err(format!("count {value} exceeds limit"));
        }
        Ok(value)
    }

    pub fn write_string(&self, value: &str) -> Result<(), String> {
        let bytes = value.as_bytes();
        self.write_u32(bytes.len() as u32)?;
        write_all(self.handle, bytes)
    }

    pub fn read_string(&self) -> Result<String, String> {
        let length = self.read_u32()?;
        if length > MAX_STRING_BYTES {
            return Err(format!("string length {length} exceeds limit"));
        }
        let mut bytes = vec![0u8; length as usize];
        read_exact(self.handle, &mut bytes)?;
        String::from_utf8(bytes).map_err(|error| error.to_string())
    }
}

/// A borrowed handle for the watcher thread — reads only, never closes.
pub struct RawPipe {
    handle: HANDLE,
}

unsafe impl Send for RawPipe {}

impl RawPipe {
    /// Blocks until the parent writes (it never does after the payload) or the
    /// pipe breaks — either way the parent is gone.
    pub fn wait_for_disconnect(&self) {
        let mut byte = [0u8; 1];
        let mut read = 0u32;
        // SAFETY: single blocking read on the shared handle.
        unsafe {
            ReadFile(
                self.handle,
                byte.as_mut_ptr(),
                1,
                &mut read,
                std::ptr::null_mut(),
            );
        }
    }
}

fn write_all(handle: HANDLE, buffer: &[u8]) -> Result<(), String> {
    let mut offset = 0usize;
    while offset < buffer.len() {
        let mut written = 0u32;
        // SAFETY: writing a sub-slice of a valid buffer.
        let ok = unsafe {
            WriteFile(
                handle,
                buffer[offset..].as_ptr(),
                (buffer.len() - offset) as u32,
                &mut written,
                std::ptr::null_mut(),
            )
        };
        if ok == 0 {
            return Err(format!("WriteFile error {}", last_error()));
        }
        if written == 0 {
            return Err("WriteFile wrote nothing".into());
        }
        offset += written as usize;
    }
    Ok(())
}

fn read_exact(handle: HANDLE, buffer: &mut [u8]) -> Result<(), String> {
    let mut offset = 0usize;
    while offset < buffer.len() {
        let mut read = 0u32;
        let remaining = (buffer.len() - offset) as u32;
        // SAFETY: reading into a sub-slice of a valid buffer.
        let ok = unsafe {
            ReadFile(
                handle,
                buffer[offset..].as_mut_ptr(),
                remaining,
                &mut read,
                std::ptr::null_mut(),
            )
        };
        if ok == 0 {
            return Err(format!("ReadFile error {}", last_error()));
        }
        if read == 0 {
            return Err("pipe closed".into());
        }
        offset += read as usize;
    }
    Ok(())
}

pub fn unique_name() -> String {
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|elapsed| elapsed.as_nanos())
        .unwrap_or(0);
    format!("WSGM.Deelevate.{}.{nanos:x}", std::process::id())
}

fn wide(value: &str) -> Vec<u16> {
    std::ffi::OsStr::new(value)
        .encode_wide()
        .chain(std::iter::once(0))
        .collect()
}

fn last_error() -> u32 {
    // SAFETY: reads the calling thread's last error.
    unsafe { GetLastError() }
}
