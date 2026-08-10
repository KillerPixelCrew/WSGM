# VolumeControl native helper

This small C++ DLL owns Core Audio COM calls for NativeAOT WSGM and is compiled by `build.ps1` into
`WSGM.VolumeControl.dll`.

- Keep the exported ABI simple, C-compatible, and paired with `Interop\NativeVolumeControl.cs`.
- Do not add managed COM interop to WSGM as a shortcut; this helper exists specifically to keep it
  disabled.
- Use the release build script rather than committing generated DLLs or linker byproducts.
