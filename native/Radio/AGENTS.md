# Radio native library

This Rust workspace provides `WSGM.Radio.dll` and `WSGM.RadioProbe.exe` for Wi-Fi, Bluetooth,
pairing, and touch-keyboard functionality unavailable to WSGM's NativeAOT process.

- Keep the C ABI flat and blittable; the C# boundary is `src\WSGM\Interop\NativeRadio.cs`.
- Build and stage only through `eng\build-radio.ps1`. Its `-Validate` mode is the required clippy and
  test gate; do not hand-copy DLLs or probe executables.
- Device and consent failures are expected states: report actionable errors and preserve a usable UI,
  never assume Wi-Fi scanning or radio power is available without location consent.
