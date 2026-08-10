# SteamInterop binding mirror

These C# files mirror `native\SteamInput\bindings\SteamInterop.Net` with explicit `using` directives
because WSGM does not enable implicit usings.

- Change the native Rust API, C header, binding mirror, and callers together; bump `sil_abi_version()`
  when the ABI changes.
- Diff against the binding source before copying. Do not hand-edit generated-equivalent API shape
  except for the required explicit usings.
- Keep the FFI NativeAOT-safe and do not turn lease failures into fatal UI failures.
