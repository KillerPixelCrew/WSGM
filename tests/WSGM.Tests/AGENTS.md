# WSGM.Tests

This project contains deterministic xUnit coverage for the main application, the launcher, the packaged-game launcher,
the logon service, setup, the IR package host and the Steam UI toolkit.

- Test parallelization is disabled because many tests exercise process-wide state, environment variables, current
  directories, native seams, or named resources. Do not re-enable it without removing those shared-state hazards.
- Use per-test temporary directories and explicit dependency seams. Never read or write the real LocalAppData WSGM tree,
  installed package, Steam session, shell state, hardware, display configuration, service manager, UAC state, or global
  input hooks.
- Registry tests may use only a unique disposable subtree below HKCU\Software\WSGM.Tests and must remove it reliably.
- Do not initialize the production Log singleton. Capture diagnostics through injected sinks or test-local abstractions.
- Name tests for the observable contract and cover success, rejection, cancellation, partial failure, repetition, and
  cleanup where relevant.
- Test files sit in the folder of the production type they cover (Core, Shell, Overlay and so on), and tests for one
  type share one class. Keep test-only helpers in this project: in Fakes and Builders once more than one test class
  needs them. Helpers several test projects need live in tests/Shared, linked as source, with an MIT SPDX header and no
  xUnit API. Do not add production branches solely to make a test convenient.
- A test of an owner's lifecycle awaits the completion the owner exposes (its `StopAsync`, a tracked work task, or a
  `TaskCompletionSource` a fake signals) or advances a fake clock. `AsyncConditions.WaitForAsync` only observes
  eventual state; never use it to wait for order or call counts. Replace an existing polling call when the change that
  touches its owner exposes a completion to await.

During iteration, run the narrowest filter that proves the change:

    dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Area"

Follow the root validation policy for initial delivery and focused follow-up checks.
