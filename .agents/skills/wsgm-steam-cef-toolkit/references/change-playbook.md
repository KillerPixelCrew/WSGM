# Steam CEF change playbook

## Choose the owner first

Ask these questions in order:

1. Is this generic CDP lifecycle, bridge, ownership, Valve contract, or a reusable Valve-backed
   surface? Change `external/steam-ui-toolkit`.
2. Is this WSGM readiness, feature policy, module wiring, state projection, command routing, or a
   service adapter? Change `src/WSGM/Shell`.
3. Is this a WSGM-only tab, badge, artwork, download, library, launch-option, or glyph feature?
   Change `src/WSGM/Core`.
4. Is this a reusable Windows audio, radio, brightness, or device primitive below product policy?
   Change `external/windows-device-control` and adapt it in WSGM.
5. Is the proposed edit inside `NativeQamBootstrap.js`? Stop and edit its source fragment instead.

Do not introduce a WSGM-local substitute for a missing toolkit primitive. Do not push WSGM policy
down into the toolkit.

## Add or restore a Valve-backed surface

1. Find the nearest existing toolkit surface, gate, publication, and tests. Extend them when they
   already own the backend; add a new gate only for a genuinely separate ownership boundary.
2. Identify the Valve contract using a literal current module id or a unique source/prototype token.
   Never enumerate and execute unknown webpack modules.
3. Define a narrow render gate separately from the data-availability gate. Account for stores that
   cache availability and need an explicit state invalidation.
4. Add or extend the toolkit TypeScript gate as required, then add the typed C# surface or row,
   state and command vocabulary, JSON context, validators, and lifecycle tests.
5. Add the WSGM adapter, backend integration, projection, command route, policy, module
   registration, and focused WSGM tests.
6. Make `null` mean no publication. Refuse invalid or uncertain writes instead of fabricating a
   default or retrying blindly.
7. Regenerate the runtime asset and verify that its vocabulary comes from the real built asset, not
   from a duplicate hand-maintained list.
8. Update `docs/steam-cef-system.md` for the current design and `docs/steam-cef.md` only when new
   dated live evidence was actually collected.

Known contract traps include enum fields removed by a closed validator, numeric values outside the
bookended vocabulary (for example a 12 FPS value below a 30 FPS minimum), a missing `deferred`
member, the no-game app id `769` rather than `0`, and PascalCase controller target ids.

## Change transport, readiness, patching, or the bridge

- Start with the existing truth-table, generation-cancellation, phase-budget, ownership, vocabulary,
  and replay tests. Add the failing case before changing the implementation when practical.
- Keep one persistent connection. Do not solve a race by creating an auxiliary attachment.
- Ensure target replacement cancels every stale probe, apply, verify, publication, and command.
- Keep teardown ordered and bounded. An applied-but-unverified patch must be removed.
- Confirm the bridge vocabulary remains the union of registered module contracts and cannot become a
  generic evaluation channel.
- Reconcile both Steam CEF documents and any affected source README with the new behavior.

## Offline validation

From the WSGM repository root:

```powershell
npm run steam-assets:build
npm run steam-assets:check
npm run steam-assets:claims
dotnet test .\external\steam-ui-toolkit\tests\SteamUiToolkit.Tests\SteamUiToolkit.Tests.csproj
dotnet test .\tests\WSGM.Tests\WSGM.Tests.csproj --filter 'FullyQualifiedName~SteamUi|FullyQualifiedName~NativeQam'
.\eng\verify.ps1 -Fix
```

Run the asset build when toolkit TypeScript or a WSGM source fragment changes. Use the check and
claims commands for any Steam UI change. Narrow the test filter while iterating, then run the full
repository gate before delivery and review every formatter-written change.

For standalone toolkit work, run `npm ci` when dependencies are not installed and
`npm run prelude:claims` from `external/steam-ui-toolkit`.

## Submodule delivery

Before changing a submodule, inspect its status and applicable guidance separately. Commit and push
every affected child first, including `windows-device-control` when a reusable OS primitive changes.
Only then stage the new gitlinks in WSGM alongside the adapter and docs. Never hide uncommitted
child work behind a parent commit, and never update a gitlink over someone else's submodule work.

## Evidence boundary

Offline tests can establish lifecycle policy, serialization, validation, refusal, parsing, and asset
ownership. They cannot establish that the current Steam build still exposes the same token, that a
row visibly renders, or that the device accepts a write. State those live gates explicitly instead
of converting an unrun scenario into a pass.
