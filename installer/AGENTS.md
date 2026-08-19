# Installer

`WSGM.iss` installs the per-user app and the machine-wide SYSTEM logon service. Ordering is a
recovery boundary, not cosmetic setup code.

- Keep `PrivilegesRequired=admin`; the service binary belongs in Program Files while WSGM remains in
  the elevating user's LocalAppData.
- Stop the logon service before stopping/killing WSGM during an update, then restore the previous
  settings/shell mode. On uninstall, uninstall the service before legacy-shell restoration and file
  deletion.
- Ship every generated native helper and required license beside WSGM. Update installer entries with
  any publish-artifact change.
- Preserve the silent-upgrade no-reboot behavior and do not re-register WSGM as the Windows shell.

## Steam Input shim

The shim that goes into **Steam's** install directory is deployed by WSGM at runtime (`--setup`,
and again before every Steam cold start), never by Inno. Inno cannot read `config.json` to honour
the Steam Input Management toggle, and cannot tell WSGM's copy from a same-named file ValvePlug or
Special K owns without a second implementation of that check in Pascal.

Removal is `[UninstallRun]`'s **first** step, `--remove-steam-input-shim`, before the service
uninstall — it needs `{app}\WSGM.exe` to still exist. Deliberately NOT in `[UninstallDelete]`: a
blind delete of `XInput1_4.dll` out of Steam's directory would take another tool's file with it.

`SteamInstalled()` still discards the path on purpose. Nothing here needs it, and a second copy of
the detection logic would drift from `Core\Steam.cs` with no test to catch it.
