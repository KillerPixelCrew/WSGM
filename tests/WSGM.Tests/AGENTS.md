# WSGM.Tests

This project contains deterministic xUnit coverage for the main application, launcher, and logon
service.

- Test parallelization is disabled because many tests exercise process-wide state, environment
  variables, current directories, native seams, or named resources. Do not re-enable it without
  removing those shared-state hazards.
- Use per-test temporary directories and explicit dependency seams. Never read or write the real
  LocalAppData WSGM tree, installed package, Steam session, shell state, hardware, display
  configuration, service manager, UAC state, or global input hooks.
- Registry tests may use only a unique disposable subtree below HKCU\Software\WSGM.Tests and must
  remove it reliably.
- Do not initialize the production Log singleton. Capture diagnostics through injected sinks or
  test-local abstractions.
- Name tests for the observable contract and cover success, rejection, cancellation, partial
  failure, repetition, and cleanup where relevant.
- Keep test-only helpers in this project. Do not add production branches solely to make a test
  convenient.

During iteration, run the narrowest filter that proves the change:

    dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Area"

Follow the root validation policy for initial delivery and focused follow-up checks.
