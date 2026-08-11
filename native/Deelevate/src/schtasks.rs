//! The de-elevation itself: a one-shot scheduled task (`InteractiveToken`, no
//! RunLevel so it runs at the user's medium token) that relaunches this exe as the
//! `--medium-child`. Same mechanism as the retired C# helper; the linked-token
//! route fails (1346), so this is the working path. Task XML is UTF-16.

use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::Command;

const CREATE_NO_WINDOW: u32 = 0x0800_0000;

/// Absolute `%WINDIR%\System32\<exe>` — an elevated caller must never resolve a
/// planted schtasks/taskkill from PATH or the working directory.
pub fn system32(exe: &str) -> PathBuf {
    let windir = std::env::var("SystemRoot").unwrap_or_else(|_| r"C:\Windows".to_string());
    Path::new(&windir).join("System32").join(exe)
}

/// Creates and runs the de-elevation task. Returns the task name to delete once
/// the child has connected.
pub fn start(exe: &Path, pipe_name: &str) -> Result<String, String> {
    let task_name = format!("WSGM.Deelevate.{pipe_name}");
    let xml_path = xml_dir().join(format!("deelevate-task-{}.xml", sanitize(pipe_name)));
    write_utf16(&xml_path, &build_task_xml(exe, pipe_name))?;

    let schtasks = system32("schtasks.exe");
    let created = run(
        &schtasks,
        &[
            "/Create",
            "/TN",
            &task_name,
            "/XML",
            &xml_path.to_string_lossy(),
            "/F",
        ],
    );
    if let Err(error) = created {
        let _ = std::fs::remove_file(&xml_path);
        return Err(error);
    }

    let ran = run(&schtasks, &["/Run", "/TN", &task_name]);
    let _ = std::fs::remove_file(&xml_path);
    match ran {
        Ok(()) => Ok(task_name),
        Err(error) => {
            delete(&task_name);
            Err(error)
        }
    }
}

/// Deletes the task; ignore failures (cleanup only).
pub fn delete(task_name: &str) {
    let schtasks = system32("schtasks.exe");
    let _ = run(&schtasks, &["/Delete", "/TN", task_name, "/F"]);
}

fn run(exe: &Path, args: &[&str]) -> Result<(), String> {
    let status = Command::new(exe)
        .args(args)
        .creation_flags(CREATE_NO_WINDOW)
        .status()
        .map_err(|error| format!("could not run schtasks: {error}"))?;
    if status.success() {
        Ok(())
    } else {
        Err(format!(
            "schtasks {} exited with {:?}",
            args[0],
            status.code()
        ))
    }
}

fn xml_dir() -> PathBuf {
    let local = std::env::var("LOCALAPPDATA")
        .unwrap_or_else(|_| std::env::temp_dir().to_string_lossy().into_owned());
    PathBuf::from(local).join("WSGM")
}

fn build_task_xml(exe: &Path, pipe_name: &str) -> String {
    let command = xml_escape(&exe.to_string_lossy());
    let arguments = xml_escape(&format!("--medium-child {pipe_name}"));
    format!(
        r#"<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>WSGM de-elevation one-shot.</Description>
  </RegistrationInfo>
  <Principals>
    <Principal id="Author">
      <LogonType>InteractiveToken</LogonType>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
    <AllowHardTerminate>true</AllowHardTerminate>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{command}</Command>
      <Arguments>{arguments}</Arguments>
    </Exec>
  </Actions>
</Task>
"#
    )
}

fn write_utf16(path: &Path, xml: &str) -> Result<(), String> {
    let mut bytes: Vec<u8> = vec![0xFF, 0xFE]; // UTF-16LE BOM
    for unit in xml.encode_utf16() {
        bytes.extend_from_slice(&unit.to_le_bytes());
    }
    if let Some(dir) = path.parent() {
        let _ = std::fs::create_dir_all(dir);
    }
    std::fs::write(path, bytes).map_err(|error| format!("could not write task XML: {error}"))
}

fn xml_escape(value: &str) -> String {
    value
        .replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
        .replace('\'', "&apos;")
}

fn sanitize(value: &str) -> String {
    value
        .chars()
        .map(|c| if c.is_ascii_alphanumeric() { c } else { '-' })
        .collect()
}
