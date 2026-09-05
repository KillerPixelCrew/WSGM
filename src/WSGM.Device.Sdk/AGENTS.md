# WSGM Device SDK contributor instructions

## Scope and sources of truth

These instructions apply to `src/WSGM.Device.Sdk/**`.

This project is the MIT-licensed, zero-dependency contract shared by WSGM, Device Lab, and community
device plugins. Keep it limited to semantic records, interfaces, validation, deterministic helpers,
and the plugin test kit. Hardware protocols, transport ownership, UI, Steam integration, HidHide,
and virtual-controller policy belong in their consumers.

Before changing a public contract, read this project's `README.md`, `docs/reference.md`, the relevant
tests, and all in-repository consumers. Public XML documentation is part of the contract; keep the
reference guide, samples, API history, and tests synchronized with code.

## Build and package

From the repository root:

```powershell
dotnet build src/WSGM.Device.Sdk/WSGM.Device.Sdk.csproj --configuration Release
dotnet test tests/WSGM.Device.Sdk.Tests/WSGM.Device.Sdk.Tests.csproj --configuration Release
dotnet pack src/WSGM.Device.Sdk/WSGM.Device.Sdk.csproj --configuration Release --no-build
```

The target is .NET 10 for Windows with nullable analysis and warnings-as-errors. Preserve the
absence of `PackageReference` and `ProjectReference`; source-generated code within this assembly is
allowed. Run `pack` only when a package is requested, and do not publish, tag, or release unless
explicitly asked.

## Compatibility and type identity

- `DeviceApi.Version` is the authoritative API level. A manifest's `apiVersion` must match it.
- Preserve existing enum numeric values, record meaning, wire names, JSON shape, and default
  interface behavior. The exact API integer still governs plugin loading; a default interface
  implementation preserves existing implementers when consumers rebuild against the new API, not
  cross-version loading.
- The project is pre-1.0: intentional breaking changes advance the minor version and require
  coordinated consumer, documentation, and API-history updates.
- Consumers must use this exact assembly identity. Do not duplicate contract types or introduce
  consumer-specific abstractions into the SDK.

## Lifecycle and safety contract

- Detection is side-effect free and must not expose mutable device access.
- Start and resume establish ownership and publish complete current sets. Commands revalidate live
  identity/firmware, service availability, range, generation, deadline, and current state before
  acting.
- Results must be truthful. Distinguish rejection, timeout, cancellation, uncertain write, failed
  readback, rollback, and verified success. Never prescribe blind retries for persistent writes
  whose outcome is uncertain.
- Descriptor, physical-device, OEM-control, and settings-manifest publications replace whole sets.
  Capability states, controller samples, and OEM events are individual publications; preserve their
  stable IDs and generation semantics.
- `PluginTrace` is for bounded transitions and decisions, not high-rate samples. Use keyed `Change`
  for polled state and `Debug` for expected diagnostic detail.

## Validation and serialization

- Treat plugin validation as integrity checking, not a sandbox: accepted plugin code runs in the
  host process.
- Keep manifest, settings, capability, command, glyph, path, count, and byte-size validation strict,
  deterministic, bounded, and fail closed. Malformed plugin manifests must produce diagnostics
  rather than escape as parser exceptions.
- Preserve camel-case source-generated JSON and rejection of unknown members where the contract
  requires it.
- Keep the settings declaration and value contracts strict and forward-compatible. Persistent
  storage and transactional commit policy belong to the host, not the SDK.
- Glyph import must retain exact authored SVG/PNG bytes for Steam while producing any separately
  normalized renderable projection. Do not rewrite the evidence bytes.

## Test kit and change discipline

`PluginTestKit` records semantic publications, traces, and keyed changes; it does not dispatch
commands or replace full host validation. Keep that boundary explicit.

Add focused tests for every changed contract, including type identity, dependency freedom, enum/API
compatibility, default interface behavior, unknown JSON members, manifest failure, limits, lifecycle
generations/deadlines, settings validation, glyph byte preservation, and trace behavior. Prefer
small semantic contracts over policy-heavy helpers and preserve the existing file style.
