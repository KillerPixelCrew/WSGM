# Interop

Interop contains minimal managed declarations and wrappers for native operating system APIs. Product
policy belongs in the caller.

- Prefer source-generated LibraryImport declarations with exact entry points, calling conventions,
  character sets, and SetLastError behavior.
- Keep handles and native allocations under explicit ownership. Use SafeHandle where practical and
  release every acquired resource on all paths.
- Keep structures blittable and layouts documented. Do not hide pointer lifetime, buffer sizing, or
  platform assumptions behind convenience APIs.
- Translate expected optional-feature failures at the wrapper boundary. Preserve native error
  details for actionable failures; do not silently convert every error into false.
- Avoid COM, shell, registry, or service policy in this directory. A wrapper exposes a capability;
  Core or Shell decides when it is safe to use.
- Tests must use seams, disposable resources, or pure layout/translation checks. Do not manipulate
  the live shell, display, service manager, or global input state.

Run focused interop tests on every ownership or error-path change, then the repository verification
gate.
