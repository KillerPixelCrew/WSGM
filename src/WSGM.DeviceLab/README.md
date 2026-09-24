# WSGM Device Lab

The authoring and diagnostic tool for
[WSGM Device Plugins](https://github.com/KillerPixelCrew/WSGM/tree/master/src/WSGM.Device.Sdk). It
inventories the handheld you are targeting, captures what its hardware actually does, scaffolds a
plugin from that capture, validates and packs the package, and runs the attended hardware tests that
only a real machine can answer.

It is a GUI and a CLI over the same code. The executable is `wsgm-device`.

## The tester wizard

Started with no arguments (or `wsgm-device wizard`), Device Lab opens a step-by-step test meant for
someone who is not a developer. It asks for administrator rights once, then:

1. **Get ready.** Holds WSGM's device-owner lock for the session, so WSGM's device integration
   cannot run beside the test. Lists other controller software that would hide or change the device
   and offers to close it (a close request only; services are never stopped). Adds itself to
   HidHide's allowed programs and removes exactly that entry when the test finishes or the window
   closes. Installs the pinned PawnIO driver when it is missing, and asks before replacing an older
   one.
2. **Your device.** Reads the board, BIOS, EC and processor identity, matches it against the known
   devices and asks the tester to confirm, or to type the product name and exact model.
3. The later stages (system details, buttons, motion, rumble, power and fans, sleep) are listed and
   arrive in later builds.
4. **Finish and share.** Shows every file that will be shared, what was replaced (account names,
   user folders, device instance paths, network addresses) and what stays on the computer, then
   writes one `.wsgmlab` file, a ZIP of the redacted test folder.

Each test is a folder under `Documents\WSGM Device Lab`. Selecting a stage in the list shows its
result; "Run again" starts a new attempt, and every attempt is kept, so a wrongly read button does
not mean repeating the whole test. Changes the wizard makes to the machine are also recorded in
`%LOCALAPPDATA%\WSGM Device Lab\wizard`, so a session that was killed is cleaned up the next time
the wizard starts.

`wsgm-device gui` opens the developer tabs described below instead. For a remote tester, publish one
self-contained file:

```powershell
.\eng\publish-device-lab.ps1 -Portable -OutputRoot publish/DeviceLabPortable
```

## Why it is a separate tool

Writing a device plugin means answering questions about a specific machine that no documentation
will tell you: which EC register the fan curve lives behind, what the OEM button reports, whether a
power-limit write actually took. Device Lab exists to answer those before you write the plugin, and
to prove the answers afterwards.

It lives in the WSGM repository but ships separately. WSGM goes to end users and owns a live
session; Device Lab is a developer tool that runs offline, on a machine that may not have WSGM
installed at all.

## The workflow

```powershell
# 1. What is this machine?
wsgm-device doctor    --out-dir diagnostics
wsgm-device inventory --out-dir inventory --shareable
wsgm-device candidates --from inventory/inventory.json # which known device is this?

# 2. Capture it, then read what you captured.
wsgm-device capture    run --recipe recipe.json --out-dir captures # attended; you approve its scope
wsgm-device inspect    capture.wsgmcap
wsgm-device compare    before.wsgmcap after.wsgmcap
wsgm-device correlate  capture.wsgmcap --action <id> --sources <id,id>

# 3. Turn a capture into a buildable plugin.
wsgm-device scaffold --from capture.wsgmcap --out-dir my-plugin `
    --usb-instance <exact-instance-id> # required only when several exact USB endpoints are present

# 4. Prove it, offline first.
wsgm-device validate my-plugin
wsgm-device test sample
wsgm-device test plugin my-plugin --from inventory/inventory.json

# 5. Ship it.
wsgm-device pack my-plugin --out plugin.wsgmpkg
```

`inventory --shareable` is the form meant for a bug report: it keeps the device facts and drops the
identifying ones.

## Known devices

`candidates` compares the inventory against the knowledge base compiled into Device Lab and lists
every record whose identity rules match, with the fields that matched. A tester confirms the match;
Device Lab never assumes it.

The records live in `Knowledge/Devices` and come in two kinds:

- **Extracted** (`hc.*.json`) are generated from the decompiled Handheld Companion source by
  `eng/extract-hc-devices.ps1`. They hold what HC states declaratively: its device switch, power
  ranges, capability flags, OEM key chords, EC fan registers and the IMU axis map HC actually
  applies. Behaviour HC implements inside methods is listed as overridden members, not guessed.
  Nothing in an extracted record is hardware-verified, and HC's own mistakes are recorded as hazards
  rather than corrected.
- **Curated** (`wsgm.*.json`) are written by hand from a WSGM plugin, a lab run or reviewed HC
  source, with the evidence for each fact. Only a curated record carries button mappings, write
  mechanisms with readback, or can supersede an extracted record.

Regenerate the extracted records after updating the reference, and review the diff:

```powershell
.\eng\extract-hc-devices.ps1
```

## Attended versus unattended

The split is enforced, not advisory.

**Read-only or offline:** `validate`, `inspect`, `compare`, `correlate`, `inventory`, `doctor` and
`pack`. `validate` never loads plugin code. It checks the manifest, the package layout and that the
entry assembly is a managed x64 image, all statically.

**Unattended but running your code:** `test sample` and `test plugin` load and run plugin code in a
contained worker with your authority. Only `validate` is fully static.

**Attended:** `test hardware` writes to the device, so it demands an explicit action, a state
directory you named, and your presence. It exists because a capability write is only ever proven on
real hardware. Detection and the whole attended lifecycle run in an authenticated disposable worker
process. Device Lab kills the full process tree at the hard deadline and keeps the production owner
slot reserved if cleanup could not be verified.

## Capture exports and privacy

Before you confirm an export, Device Lab shows a bounded projection of the actual sanitized bundle:
the root documents in full, every stream, analysis and blob represented by exact counts and hashes,
and large content sampled rather than quietly left out of the privacy review.

Redaction uses one token map across the inventory, the recipe and the streams. If redaction would
merge two source identifiers into one token, the export stops with an error before it creates a
bundle or a preview.

Imported markers need valid UTF-8 and a named kind. Explicit restart and resume segments keep their
reason even when the receipt arrives late. Malformed read-probe data comes back as a rejection along
with any earlier samples, and a declared numeric bound requires an in-range numeric value, version
responses included.

A cancelled inventory write, or a failed or cancelled export, reports the leftover temporary path if
cleanup did not manage to remove it. When cleanup succeeds, a cancelled export still reports as
cancelled.

Output paths are checked before anything is written. A broad home directory, a repository root or an
existing reparse point is refused rather than written into.

## Scaffolded plugins are yours

`scaffold` generates a plugin that links only `WSGM.Device.Sdk`, which is MIT. It ships an MIT
`LICENSE.txt` with a placeholder for your name, because that constrains you least. Replace it with
whatever licence you want, including none of these. WSGM itself is GPL-3.0-or-later, but a plugin
does not link WSGM.

## Building

Run these from the WSGM repository root. The SDK is shared source under `src/WSGM.Device.Sdk`, and
device projects are built and reviewed together.

```powershell
dotnet build src/WSGM.DeviceLab/WSGM.DeviceLab.csproj
dotnet test tests/WSGM.DeviceLab.Tests/WSGM.DeviceLab.Tests.csproj
```

Plugins that Device Lab scaffolds reference the same SDK as WSGM: a project reference inside a
checkout, and an explicit reference to the `WSGM.Device.Sdk.dll` shipped beside the tool otherwise.
That is what stops you building a plugin against a contract the host does not have.

## Licence

MIT, see `LICENSE`. Third-party components it redistributes keep their own licences, see
`THIRD_PARTY_NOTICES.md`.

Device Lab compiles in four interop sources from WSGM itself (`Kernel32.cs`,
`NativePackageSource.cs`, `NativePathIdentity.cs` and `NativeHidHide.cs` under `src/WSGM/Interop`),
and parts of the wizard are ported from the AllyXLab tool. Their copyright holder licenses those
copies to Device Lab under its MIT licence; the originals in WSGM stay under WSGM's GPL.

Capability publications can include SDK prominence and companion hints. Device Lab checks them with
`CapabilityLayout.TryValidate` before an attended capability action; these hints describe host
presentation and never permit another hardware write.
