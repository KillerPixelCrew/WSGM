//! `WSGM.Deelevate.exe` — Steam launch-option wrapper that runs a game at MEDIUM
//! integrity when WSGM has elevated Steam.
//!
//! Native by design: a NativeAOT .NET wrapper dies before `main` when Steam
//! launches it. The de-elevation is unchanged from the previous C# helper — the
//! elevated parent runs a one-shot scheduled task (`InteractiveToken`, no
//! RunLevel) whose medium child launches the game with Steam's environment,
//! handed over a per-launch named pipe that also carries the lifetime (parent
//! death -> kill the game tree; game exit -> report the exit code).

#![windows_subsystem = "windows"]

mod elevation;
mod log;
mod pipe;
mod schtasks;
mod wire;

use std::process::Command;

use pipe::Pipe;
use wire::Payload;

const CHILD_ARG: &str = "--medium-child";

fn main() {
    std::process::exit(run());
}

fn run() -> i32 {
    let args: Vec<String> = std::env::args().collect();

    // Child role: launched by the scheduled task at medium integrity.
    if args.len() == 3 && args[1].eq_ignore_ascii_case(CHILD_ARG) {
        return run_medium_child(&args[2]);
    }

    log::info(&format!(
        "invoked (argc {}, elevated {:?}): {args:?}",
        args.len(),
        elevation::is_elevated()
    ));

    if args.len() < 2 {
        log::error(
            "no target command supplied; expected: WSGM.Deelevate.exe <program> [arguments]",
        );
        return 64;
    }

    // args[1..] is the game command (target + its arguments).
    let payload = Payload::capture(&args[1..]);
    match elevation::is_elevated() {
        Some(false) => launch_and_wait(&payload),
        _ => run_elevated_parent(&payload),
    }
}

/// Wrapper already at medium integrity: launch the game directly and wait.
fn launch_and_wait(payload: &Payload) -> i32 {
    match spawn_game(payload) {
        Ok(mut child) => {
            log::info(&format!("target started directly (pid {})", child.id()));
            child
                .wait()
                .ok()
                .and_then(|status| status.code())
                .unwrap_or(1)
        }
        Err(error) => {
            log::error(&format!("failed to launch target: {error}"));
            1
        }
    }
}

/// Elevated parent: create the pipe, run the de-elevating scheduled task, hand the
/// payload to the medium child, and stay alive for the game's lifetime returning
/// its exit code (Steam tracks THIS process).
fn run_elevated_parent(payload: &Payload) -> i32 {
    let exe = match std::env::current_exe() {
        Ok(path) => path,
        Err(error) => {
            log::error(&format!("cannot resolve own path: {error}"));
            return 1;
        }
    };
    let pipe_name = pipe::unique_name();
    let server = match Pipe::create_server(&pipe_name) {
        Ok(server) => server,
        Err(error) => {
            log::error(&format!("could not create pipe: {error}"));
            return 1;
        }
    };

    let task = match schtasks::start(&exe, &pipe_name) {
        Ok(task) => task,
        Err(error) => {
            log::error(&format!("scheduled task failed: {error}"));
            return 1;
        }
    };

    // The scheduled task's /Run already created the child process; if it never
    // connects (it is the same native exe, so this is unlikely), fail rather than
    // hang forever, or Steam would think the game runs indefinitely.
    if let Err(error) = server.wait_for_client(std::time::Duration::from_secs(20)) {
        log::error(&format!("medium child never connected: {error}"));
        schtasks::delete(&task);
        return 1;
    }
    // /Run keeps its action running; delete the task so they do not accumulate.
    schtasks::delete(&task);

    if let Err(error) = payload.write_to(&server) {
        log::error(&format!("could not send payload: {error}"));
        return 1;
    }

    match server.read_u32() {
        Ok(1) => {}
        Ok(_) => {
            let reason = server.read_string().unwrap_or_else(|_| "unknown".into());
            log::error(&format!("medium launch failed: {reason}"));
            return 1;
        }
        Err(error) => {
            log::error(&format!("medium child handshake failed: {error}"));
            return 1;
        }
    }

    let pid = server.read_u32().unwrap_or(0);
    log::info(&format!(
        "medium target started (pid {pid}); waiting for exit"
    ));
    // No timeout: Steam expects its launch-option wrapper to stay alive for the
    // whole game lifetime. The child writes the code when the game exits.
    match server.read_u32() {
        Ok(code) => {
            log::info(&format!("medium target exited with {code}"));
            code as i32
        }
        Err(error) => {
            log::error(&format!("lost the medium child: {error}"));
            1
        }
    }
}

/// Medium child (run by the scheduled task): read the payload, launch the game,
/// and race the game's exit against the parent's pipe closing.
fn run_medium_child(pipe_name: &str) -> i32 {
    let pipe = match Pipe::connect_client(pipe_name, std::time::Duration::from_secs(20)) {
        Ok(pipe) => pipe,
        Err(error) => {
            log::error(&format!("child could not open pipe: {error}"));
            return 1;
        }
    };

    if elevation::is_elevated() == Some(true) {
        let _ = pipe.write_u32(0);
        let _ = pipe.write_string(
            "Task Scheduler did not provide a medium-integrity token; UAC may be disabled.",
        );
        log::error("child is still elevated — de-elevation failed");
        return 1;
    }

    let payload = match Payload::read_from(&pipe) {
        Ok(payload) => payload,
        Err(error) => {
            let _ = pipe.write_u32(0);
            let _ = pipe.write_string(&format!("payload read failed: {error}"));
            return 1;
        }
    };

    let mut child = match spawn_game(&payload) {
        Ok(child) => child,
        Err(error) => {
            let _ = pipe.write_u32(0);
            let _ = pipe.write_string(&format!("Process launch failed: {error}"));
            return 1;
        }
    };
    let pid = child.id();
    if pipe
        .write_u32(1)
        .and_then(|()| pipe.write_u32(pid))
        .is_err()
    {
        // Parent already gone; take the game down with us.
        kill_tree(pid);
        return 1;
    }
    log::info(&format!(
        "launched medium target (pid {pid}); preserving wrapper lifetime"
    ));

    // Watcher: a broken pipe means Steam killed the elevated parent -> stop the
    // game tree. The parent sends nothing after the payload, so the read only
    // returns once the pipe breaks.
    let watch = pipe.detached();
    std::thread::spawn(move || {
        watch.wait_for_disconnect();
        log::info(&format!(
            "Steam wrapper exited before target pid {pid}; stopping its tree"
        ));
        kill_tree(pid);
        std::process::exit(1);
    });

    let code = child
        .wait()
        .ok()
        .and_then(|status| status.code())
        .unwrap_or(1);
    log::info(&format!("medium target pid {pid} exited with {code}"));
    // The parent (which Steam tracks) reports this to Steam; our own exit code
    // does not matter.
    let _ = pipe.write_u32(code as u32);
    code
}

/// Launches the game exactly as Steam would: same command, arguments, working
/// directory, and — for the medium child — Steam's captured environment rebuilt
/// over the clean one Task Scheduler provides.
fn spawn_game(payload: &Payload) -> std::io::Result<std::process::Child> {
    let target = &payload.arguments[0];
    let working_dir = payload.resolved_working_directory();

    let mut command = Command::new(target);
    command.args(&payload.arguments[1..]);
    command.current_dir(&working_dir);
    command.env_clear();
    for (key, value) in &payload.environment {
        command.env(key, value);
    }
    command.spawn()
}

/// Stops a process and its whole tree via System32\taskkill.
fn kill_tree(pid: u32) {
    let taskkill = schtasks::system32("taskkill.exe");
    let _ = Command::new(taskkill)
        .args(["/PID", &pid.to_string(), "/T", "/F"])
        .spawn()
        .and_then(|mut child| child.wait());
}
