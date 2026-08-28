# wsgm-device (WSGM.DeviceLab.Cli)

The developer CLI. This project is a command router and nothing else: parse, dispatch into
`WSGM.DeviceLab.Core`, render structured output, return an exit code. Behavior that belongs to a
workflow goes in the engine so the GUI gets it too.

- Every command takes an explicit output directory. There is no implicit write to the user's live
  WSGM configuration, and no command reads it either.
- `probe run` is the only command that can mutate hardware, and it must stay hostile to automation:
  no `--yes`, no CI mode, no recipe nesting, no bulk `test all`, and a refusal when stdin is not an
  interactive local console. Authorization expires when the trial hash, module version, device
  generation, target resource, expected original state, or preflight changes.
- `validate` and `pack` must state plainly that they confer no package trust, no privileged
  authorization, no hardware verification, and no retail support. Do not soften that wording.
- Exit codes are part of the contract: `0` success, `64` usage. New codes get documented here when
  they are added, because scripts and CI will depend on them.
- Keep output structured and stable. Diagnostics go to stderr, results to stdout, so a caller can
  pipe one without the other.
