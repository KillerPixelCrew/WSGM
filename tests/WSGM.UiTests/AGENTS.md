# Headless UI tests

This suite runs the real Overlay and Settings XAML on Avalonia's headless platform with Skia.

- Use `UiFixture` and explicit dependencies. Never construct production Settings services, run
  `Program.Main` or `App.OnFrameworkInitializationCompleted`, initialize `Log`, or start native input,
  Steam, display, shell, power or device services. `SystemStatus` may be constructed but not started.
- Keep fixtures, stores and diagnostics isolated. Unexpected hardware writes must fail. Do not add
  production test-mode switches or suppress binding errors to get a green test.
- Use keyboard and pointer input for interactions. Assert observable state, requested actions,
  focus and cleanup. Drain the dispatcher or await a signalled completion instead of sleeping.
- Keep the production resource graph, bundled Inter mapping, fixed culture, scale, accent and
  synthetic data. Disable transitions for captures, not the controls or their bindings.
- Baselines are selective regression references. Inspect actual/expected/diff images before using
  `eng/update-ui-baselines.ps1 -Case <case-name>`. Never update them from tests, CI or `verify.ps1`.
- Follow the root validation policy: focused checks on follow-ups, full gate for initial delivery.
  Rendering checks do not establish native activation, input capture, Steam leases or
  physical-device correctness.
