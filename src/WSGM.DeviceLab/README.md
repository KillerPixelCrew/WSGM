# WSGM Device Lab

The authoring and diagnostic tool for
[WSGM Device Plugins](https://github.com/KillerPixelCrew/WSGM/tree/master/src/WSGM.Device.Sdk). It
inventories the handheld you are targeting, captures what its hardware actually does, scaffolds a
plugin from that capture, validates and packs the package, and runs the attended hardware tests that
only a real machine can answer.

It is a GUI and a CLI over the same code. The executable is `wsgm-device`.

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
