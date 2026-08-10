# Build and verification scripts

`eng` holds the supported build/validation entry points for native helper staging and repository
verification.

- `verify.ps1` is the validation gate: formatting, staged Rust helpers, Release build, tests, and
  coverage. Keep vendored source exclusions intact.
- Both Rust helpers are source-built; update their staging/validation scripts with any ABI, artifact,
  or workspace change.
- Build scripts must fail fast, stage generated artifacts rather than checking them in, and remain
  safe to run from the repository root.
