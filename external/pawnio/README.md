# PawnIO

[PawnIO](https://github.com/namazso/PawnIO.Setup) is a signed kernel driver that runs small signed
modules for low-level hardware access. LibHandheld uses its RyzenSMU, IntelMSR and EC modules for
processor settings, power limits and embedded-controller access, the same way Handheld Companion
does. Setup installs the driver when the selected handheld definition requires it.

`pawnio.lock.json` pins the installer by URL, SHA-256 and signer. The binary is not checked in:
`eng/acquire-pawnio.ps1` downloads it into `artifacts/pawnio` and refuses a file whose digest or
signer differs. Setup embeds the installer when that file is present, and embeds the lock file
itself, so it checks the extracted installer against the same pin before it runs it.

`build.ps1` acquires the installer before it builds setup. Setup also checks the installer's
Authenticode signer against `signerThumbprint`, and `eng/assert-pawnio-pin.ps1`, run by
`eng/verify.ps1`, keeps the lock itself well formed.

The installer is redistributable unmodified. Its licence and the driver's are upstream's.
