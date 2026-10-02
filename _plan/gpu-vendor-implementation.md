# AMD and NVIDIA GPU implementation

Approved 2026-10-02. Implements [NVIDIA #177](https://github.com/KillerPixelCrew/WSGM/issues/177)
and [AMD #179](https://github.com/KillerPixelCrew/WSGM/issues/179). Both are ordinary `wsgm.gpu`
packages. Vendor APIs stay inside their packages; the existing GPU coordinator supplies game
identity, profiles, Overlay and QAM.

## Checklist

- [x] Read both issue bodies and the current SDK, GPU host and Intel package.
- [x] Locate the maintainer's `D:/Coding/NoVidiaApp` reference and its curated settings and DRS code.
- [x] Finish serialized native calls, observation, loss/reconnect and safe shutdown.
- [x] Implement NVIDIA native global/application settings, inheritance and owned-setting recovery.
- [x] Implement NVIDIA connected-output discovery, supported color combinations, G-SYNC control,
      dithering and HDR10+ output mode.
- [x] Implement AMD ADLX enumeration and HC's Radeon behavior, including RSR/AFMF, supported
      FidelityFX upgrades, FreeSync/scaling/color and mapped ADL2 dithering.
- [x] Use host-switched AMD game overrides; keep display-wide controls global and leave unrelated
      Radeon profiles intact.
- [x] Add both packages to solution/build/bundle, licenses, guidance and provenance.
- [x] Add focused regression sources and compile the Release solution without warnings.
- [x] Format and validate both plugin packages, their manifests, licenses and payloads.
- [ ] Commit and push the complete implementation on master, then rebuild the requested Z: setup.

## API and ownership decisions

NVIDIA uses the installed `nvapi64.dll`. NoVidiaApp is the behavioral reference for curated driver
settings and individual DRS profile mutations. Official NVAPI headers decide native layouts and
documented values. Undocumented settings from NoVidiaApp remain isolated and are offered only when
the driver enumerates them and their values. DRS is authoritative; cleanup touches only recorded
settings and preserves prior explicit values and external changes.

AMD uses the installed `amdadlx64.dll` and documented C interfaces. ADLX's 3D interfaces control
GPU-wide state, so WSGM's existing `Switched` scope supplies game activation/exit behavior. The ADL
application-profile interfaces do not supply a documented public schema for these Radeon settings;
the package must not invent native per-application storage. FreeSync, scaling, output color and
Vari-Bright target the display's actual GPU, with support discovered per output.

WSGM/RTSS retain frame-limit ownership. Neither package adds a competing driver limiter. Compilation,
formatting and package checks are in scope. The maintainer did not request test execution or live
driver changes; those remain deferred. AMD hardware acceptance required by #179 remains separate.

## Build evidence

- Release solution compilation: zero warnings and zero errors, including regression sources.
- Scoped Rider cleanup, Prettier, Steam asset drift and agent-guidance checks passed.
- `eng/build-bundle.ps1` produced both first-party GPU packages and a bundle manifest with matching
  PCI vendor IDs and `blind` validation status. The archives contain the correct managed entry
  assembly, manifest, license, README and provenance; host SDK copies were removed.
- The packaging path did not load plugin code. No app test suite, full test-bearing gate, GPU
  probe, live driver read/write scenario, installation or deployment was executed.
