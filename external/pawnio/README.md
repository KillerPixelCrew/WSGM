# PawnIO

[PawnIO](https://github.com/namazso/PawnIO.Setup) is a signed kernel driver that runs small signed
modules for low-level hardware access. Device Lab's wizard uses it for the AMD SMU and EC tests, the
same way Handheld Companion does, and installs it when a tester's machine lacks it.

`pawnio.lock.json` pins the installer by URL, SHA-256 and signer. The binary is not checked in:
`eng/acquire-pawnio.ps1` downloads it into `artifacts/pawnio` and refuses a file whose digest or
signer differs. Device Lab embeds the installer when that file is present, and embeds the lock file
itself, so the running tool checks the extracted installer against the same pin before it runs it.

A build without the acquired installer still works; the wizard then reports PawnIO as unavailable
instead of installing it.

The installer is redistributable unmodified. Its licence and the driver's are upstream's.
