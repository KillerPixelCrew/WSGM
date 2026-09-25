# KX

`KX.exe` is the Intel register tool Handheld Companion uses to read and write the processor power
limits (MCHBAR `0x59A0`/`0x59A4` and MSR `0x610`). Device Lab's power stage uses it on an Intel
machine that has no curated knowledge record, for one bounded power-limit write that is read back
and put back.

It is unsigned and carries no version resource, so `kx.lock.json` pins it by SHA-256.
`eng/assert-pawnio-pin.ps1` checks the digest, and the running tool checks it again after
extracting the embedded copy into an administrators-only folder.

Its licence is unknown: neither the file nor Handheld Companion names an author or terms. It is
redistributed unmodified, as Handheld Companion 1.3.1.6 bundles it, and the Device Lab MIT licence
does not cover it.
