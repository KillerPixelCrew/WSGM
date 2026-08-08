# LoadingIndicators.Avalonia — vendoring and porting notes

Upstream: <https://github.com/moviegear/LoadingIndicators.Avalonia>
License: Unlicense (public domain). `LICENSE.md` is kept here, and the copy that
ships beside `WSGM.exe` lives in `src/WSGM/Licenses/`.

## Why this is vendored as source

WSGM previously consumed the `LoadingIndicators.Avalonia` NuGet package
(11.0.11.1, last published July 2024). That package has no build for Avalonia 12,
and the failure is not cosmetic: its **precompiled** XAML binds through
`Avalonia.Markup.Xaml.MarkupExtensions.CompiledBindings.CompiledBindingPathBuilder`,
a type Avalonia 12 removed. The NativeAOT compiler reports it plainly:

```
ILC: Method '[LoadingIndicators.Avalonia]CompiledAvaloniaXaml.!AvaloniaResources
     +XamlClosure_17.Build(IServiceProvider)' will always throw because:
     Failed to load type '...CompiledBindingPathBuilder' from assembly
     'Avalonia.Markup.Xaml, Version=12.1.1.0'
```

Those closures are built lazily, when an indicator is first rendered — which is
why the app still *started* fine and only the boot splash would have broken. The
boot splash is the cover that hides the desktop at sign-in, so that is the worst
possible place to discover it.

Building the XAML here compiles it against the Avalonia version WSGM actually
ships, so the styles are correct by construction and a future Avalonia breaking
change fails the build instead of the splash.

## Changes from upstream

Only the project file. No `.axaml` and no `.cs` file has been modified — the
upstream XAML compiles unchanged under Avalonia 12's XAML compiler.

`LoadingIndicators.Avalonia/LoadingIndicators.Avalonia.csproj`:

- `TargetFrameworks` `net8.0;net7.0;net6.0` → `TargetFramework` `net10.0`, to match WSGM.
- `Avalonia` `11.0.*` → pinned `12.1.1`, the exact version WSGM ships. A floating
  range here is what allowed the original mismatch.
- Dropped all packaging metadata (`GeneratePackageOnBuild`, `PackageId`, icon,
  readme, release-notes target). This is consumed as a project reference and is
  never packed, and the release-notes target reads a file outside this directory.
- Dropped the `**\*.xaml.cs` `DependentUpon` item and the `Assets\**` resource
  glob: neither matches anything in this tree.
- `AvaloniaResource` glob `**\*.xaml` → `**\*.axaml`, which is what the files in
  this tree are actually called. (Avalonia's own SDK targets also auto-include
  `**/*.axaml`, so this is belt-and-braces rather than the thing that was
  broken.)
- `GenerateDocumentationFile=false`, `NoWarn=CS1591;CS1573`,
  `EnforceCodeStyleInBuild=false`: WSGM builds with warnings as errors and
  requires XML docs on public APIs. Those are WSGM's rules for WSGM's code, not
  something to impose on third-party source we want to keep diffable.

## Re-syncing

Re-clone upstream, then re-apply the project-file changes above. Because no
source file is patched, a re-sync is a straight copy of everything except the
`.csproj`.
