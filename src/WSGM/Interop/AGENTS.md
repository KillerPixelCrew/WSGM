# Interop

Interop is the narrow managed boundary to Win32 and WSGM's native helper DLLs.

- Keep P/Invoke declarations `LibraryImport`-based, blittable, explicit about ownership, and usable
  by NativeAOT with managed COM interop disabled.
- Native helper buffers must have a matching free call on every success path; do not expose native
  pointers beyond the interop/manager boundary.
- Add logging and graceful unavailable behavior for device-specific APIs. The application must remain
  usable when a helper DLL, radio, audio endpoint, or shell service is unavailable.
