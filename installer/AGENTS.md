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
