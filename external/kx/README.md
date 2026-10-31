# KX

`KX.exe` is the Intel register tool Handheld Companion 1.3.1.6 bundles to read and write processor
power limits (MCHBAR `0x59A0`/`0x59A4`). WSGM uses it only for the KX power-limit path on Intel
handhelds, when the user selects that path. Each KX command loads its HwRwDrv driver and unloads it
on exit, so the driver is present only while a selected KX operation runs.

HwRwDrv loads only with Memory Integrity and the vulnerable-driver blocklist off. Setup turns both
off for a model that declares `DeviceHardwareAccess.IntelKx`.

It is unsigned and carries no version resource, so `kx.lock.json` pins it by SHA-256, and LibHandheld
checks the digest again before running it. Its licence is unknown: neither the file nor Handheld
Companion names an author or terms. It is redistributed unmodified, as Handheld Companion bundles it.
