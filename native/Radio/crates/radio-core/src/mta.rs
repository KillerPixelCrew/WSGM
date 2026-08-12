//! The dedicated multi-threaded-apartment worker every WinRT call runs on.
//!
//! Two rules force this design, and both are documented:
//!
//! * `IAsyncOperation::GetResults` "mustn't be called from a single-threaded
//!   apartment", and this library is loaded by an Avalonia UI thread, which is
//!   an STA. Blocking on a WinRT async from there would deadlock.
//! * A DLL can never call `CoInitializeEx`/`RoInitialize` on the host's thread,
//!   because it cannot know the host's threading model — and doing it from
//!   `DllMain` would take the loader lock. So the apartment is initialised
//!   lazily, on first use, on a thread this crate owns outright.
//!
//! The worker outlives every call and is never torn down: a WinRT proxy created
//! in an apartment dies with it, and callers hold device/watcher state across
//! calls.

use std::sync::mpsc::{Sender, SyncSender, channel, sync_channel};
use std::sync::{Mutex, OnceLock};
use std::thread;

use windows::Win32::System::WinRT::{RO_INIT_MULTITHREADED, RoInitialize};

use crate::error::{Error, Result};

type Job = Box<dyn FnOnce() + Send + 'static>;

static WORKER: OnceLock<Mutex<Option<Sender<Job>>>> = OnceLock::new();

fn worker_slot() -> &'static Mutex<Option<Sender<Job>>> {
    WORKER.get_or_init(|| Mutex::new(None))
}

/// A sender for the MTA worker, spawning the thread on first use.
///
/// Deliberately not a `OnceLock<Sender<Job>>` holding a dead channel on
/// failure: a spawn can fail transiently under resource pressure — many
/// processes starting at logon is exactly when WSGM first reaches this crate —
/// and caching that failure would disable radio power, Bluetooth, audio connect
/// and the Wi-Fi indicator for the rest of the session. An empty slot is simply
/// retried by the next call.
fn worker() -> Result<Sender<Job>> {
    let mut slot = worker_slot().lock().map_err(|_| Error::WorkerUnavailable)?;
    if let Some(existing) = slot.as_ref() {
        return Ok(existing.clone());
    }
    let (tx, rx) = channel::<Job>();
    thread::Builder::new()
        .name("wsgm-radio-mta".to_owned())
        .spawn(move || {
            // SAFETY: this thread is created and owned here, so nothing else
            // has initialised an apartment on it. A failure is not fatal —
            // an apartment may already be implicitly present — and the first
            // real WinRT call will surface any genuine problem with context.
            unsafe {
                let _ = RoInitialize(RO_INIT_MULTITHREADED);
            }
            // Deliberately no RoUninitialize: the thread runs for the life of
            // the process, and tearing the apartment down would invalidate
            // every proxy the caller still holds.
            while let Ok(job) = rx.recv() {
                job();
            }
        })
        .map_err(|_| Error::WorkerUnavailable)?;
    *slot = Some(tx.clone());
    Ok(tx)
}

/// Runs `work` on the MTA worker and blocks until it returns.
///
/// The closure must be `Send` because it crosses to another thread, and
/// `'static` because the worker's queue owns it until it runs.
pub fn on_mta<T, F>(work: F) -> Result<T>
where
    T: Send + 'static,
    F: FnOnce() -> T + Send + 'static,
{
    let (tx, rx): (SyncSender<T>, _) = sync_channel(1);
    worker()?
        .send(Box::new(move || {
            // A closed receiver just means the caller went away; dropping the
            // value is correct and must not panic on the worker thread.
            let _ = tx.send(work());
        }))
        .map_err(|_| Error::WorkerUnavailable)?;
    rx.recv().map_err(|_| Error::WorkerUnavailable)
}

/// Queues `work` on the MTA worker without waiting for it.
///
/// Used for the fire-and-forget half of the event paths, where the result is
/// delivered through a callback rather than a return value.
pub fn post_mta<F>(work: F) -> Result<()>
where
    F: FnOnce() + Send + 'static,
{
    worker()?
        .send(Box::new(work))
        .map_err(|_| Error::WorkerUnavailable)
}

/// Runs `work` on a fresh MTA thread of its own and does not wait.
///
/// For operations that block for a long time and would otherwise monopolise the
/// shared worker. Bluetooth pairing is the case that forces this: it blocks
/// until the user answers a PIN prompt, and reading radio state must not be
/// stuck behind that. It also has to be a *different* thread from the one that
/// answers the prompt, or the two would deadlock.
pub fn detached_mta<F>(work: F) -> Result<()>
where
    F: FnOnce() + Send + 'static,
{
    thread::Builder::new()
        .name("wsgm-radio-op".to_owned())
        .spawn(move || {
            // SAFETY: a thread created here and used for nothing else.
            unsafe {
                let _ = RoInitialize(RO_INIT_MULTITHREADED);
            }
            work();
        })
        .map(|_| ())
        .map_err(|_| Error::WorkerUnavailable)
}
