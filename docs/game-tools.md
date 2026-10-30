# Game Tools

Game Tools installs, updates, configures and removes third-party per-game modifications in a game's
own folder, from Steam's game menu or from the Overlay. The first delivery ships one tool,
OptiScaler. This guide covers the owners, how a game's program is found, the tool catalog, the
install transaction and its manifest, conflicts, revalidation and the policies around third-party
code. The product decisions behind it are in [decisions.md](decisions.md#steam).

## Owners

| Part                         | Files                                                                       | Owns                                                                                                        |
| ---------------------------- | --------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------- |
| PE facts                     | `Core/PeImage.cs`                                                           | Architecture from the COFF machine and the version-resource identity fields; shared with the emulator owner |
| Folder walk                  | `Core/GameTools/GameFolderLayout.cs`                                        | One bounded walk per scan, and the guard against folders too broad to be a game folder                      |
| Runtime resolution           | `Core/GameTools/GameRuntimeResolver.cs`                                     | Ranking every executable and choosing the one the game runs, or asking                                      |
| Upscaler and anti-cheat scan | `Core/GameTools/GameFolderScans.cs`                                         | Standalone upscaler and frame-generation libraries; anti-cheat markers                                      |
| Catalog                      | `Core/GameTools/game-tools.json`, `GameToolCatalog.cs`                      | The embedded, validated tool definitions                                                                    |
| Releases                     | `Core/GameTools/GameToolReleases.cs`, `GameToolVersions.cs`                 | Latest-release lookup, package cache, payload validation and version families                               |
| Configuration edits          | `Core/GameTools/GameToolIni.cs`                                             | Editing existing keys of a tool's own INI file                                                              |
| Transaction                  | `Core/GameTools/GameToolInstaller.cs`                                       | Planning, journaling, copying, rollback, rename, reconfigure and remove                                     |
| Durable state                | `Core/GameTools/GameToolStore.cs`                                           | `game-tools.json`: remembered programs and per-tool manifests                                               |
| Service                      | `Shell/GameToolsService.cs`                                                 | Scans, state for both surfaces, one mutation at a time, progress, running checks                            |
| Steam page                   | `Shell/SteamGameToolsSurface.cs`, `Core/SteamUiAssets/Source/game-tools.ts` | The routed Big Picture page, its publication and commands                                                   |
| Steam entry                  | `Shell/SteamGameContextMenuBackend.cs`                                      | The game menu's "Game Tools…" item                                                                          |
| Overlay page                 | `Overlay/` Game Tools view, through `OverlaySources.GameTools`              | The same page in the Overlay, calling the service directly                                                  |

Release lookup, download and extraction come from the shared `Core/Downloads` utility, which the
emulator owner also uses: `DownloadClient` (HTTPS only), `HostedReleases` (GitHub and Forgejo
releases from a `HostedReleaseSource`), `VerifiedDownload` (bounded, size and SHA-256 checked) and
`PackageArchive` (extraction that refuses paths escaping the staging folder). Game Tools keeps no
second HTTP or archive path.

## From the menu to the service

1. Steam's game cog menu carries "Game Tools…" (`wsgm.game-tools`) for every single-app target,
   including uninstalled games, shortcuts and running games. It is a flat item with no template.
2. Activating it calls `GameToolsService.OpenAsync(appId)`, which makes the game the Steam
   publication's game and starts a scan, then answers with the route `/wsgm/game-tools/<appId>`. The
   toolkit's gate navigates there.
3. The page (`registerSteamPage`, template `game-tools`, gate `gameTools`) also sends `open` on
   mount with the AppID from the route, so direct navigation and Back into the page work the same
   way.
4. State is published as `wsgm.game-tools` whenever the service raises `Changed`. Commands are
   `open`, `refresh`, `checkUpdates`, `cancel`, `chooseRuntime`, `install`, `update`, `repair`,
   `configure` and `remove`, each carrying the AppID it acts on.

The Overlay reaches the same service through `OverlaySources.GameTools` (null in overlay-test). It
calls `ReadAsync(appId)` when a page opens and `Peek(appId)` on `Changed`, which re-renders from the
cached scan without walking the folder again. Both surfaces get the same `GameToolsState` records,
the same actions and the same refusals.

Mutating commands return as soon as they are admitted, because the Steam bridge must answer
promptly. The work runs in the background and its outcome lands in the game's state as `notice` or
`error` once `busy` returns to null. Only `chooseRuntime` waits for its rescan. One Game Tools
change runs at a time across all games; a command for another game is refused while one runs.
Download and extraction can be cancelled; once game files are being written the transaction
completes or rolls back.

## Finding the game's program

Steam never names a store title's executable, so `GameRuntimeResolver` ranks every `*.exe` in the
install folder and reports one of four confidences:

| Confidence    | Meaning                                                                              |
| ------------- | ------------------------------------------------------------------------------------ |
| `confirmed`   | The user chose it earlier and it is still present                                    |
| `structural`  | An engine layout or the shortcut's own target identifies it                          |
| `single`      | It is the only plausible candidate, or decisively ahead of the others                |
| `needsChoice` | Several candidates are plausible; the page lists them with reasons and offers Browse |

- The install folder and shortcut target are read from Steam through `SteamLaunchConfig` on every
  scan and never cached as truth. A shortcut's target is read through `OriginalsFrom`, which sees
  through WSGM's launch wrapper. Its start folder is the root only when it contains the target and
  is not too broad; otherwise the target's folder is.
- The walk is bounded: depth 6, 6,000 directories and 40,000 files. It skips reparse points and
  inaccessible directories. A capped walk sets `scanTruncated`, and the page suggests Browse.
- Unreal: a root `Engine` folder and `<Project>/Binaries/Win64` or `WinGDK` executables, preferring
  `*-Shipping.exe`. A root executable beside them is a launcher. A `.uproject` is not required.
- Unity: an executable beside `<Name>_Data` and `UnityPlayer.dll`, reported as IL2CPP with
  `GameAssembly.dll` and Mono with `MonoBleedingEdge`.
- Redistributables, installers, crash reporters and anti-cheat helpers, and names such as launcher,
  bootstrap, updater, config or settings, lower a candidate's score. They are evidence, not
  exclusions, so an unusual game is still offered.
- The resolver never picks the first executable found. Engine detectors only add evidence, so a new
  detector changes no installer.
- A remembered program that has gone, usually after a game update, is reported as
  `rememberedMissing` and detection runs again.

A picked file must be an `.exe` with a readable PE header. A file outside the install folder is
refused when its folder is a drive root, the Windows folder, the user profile, either Program Files,
ProgramData, AppData, LocalAppData, Desktop, Documents, Downloads or a OneDrive root. The match is
exact, so a game below one of them still works. Anything under `WindowsApps` is refused because
packaged games can't be modified. The tool is installed beside the chosen program, so the choice
cannot change while a tool is installed there.

## Scans that inform the user

`UpscalerScan` lists standalone DLSS, DLSS frame generation and ray reconstruction, FidelityFX and
FSR 2/3, XeSS, XeSS frame generation, XeLL and Streamline libraries, with their versions, from the
same walk. Files WSGM installed are left out. An empty result means no standalone library was found,
never that the game is unsupported: many games link an upscaler statically. A tool that needs an
upscaler input only warns when none is found.

`AntiCheatScan` recognises Easy Anti-Cheat, BattlEye, EA Anti-Cheat and Anti-Cheat Expert from their
usual files. It is a heuristic: a game may ship an anti-cheat it does not enforce offline, or
enforce one whose files live elsewhere. A non-empty result shows the tool's warning prominently and
makes install require an explicit acknowledgement, which the service enforces.

## The tool catalog

Definitions live in the embedded `src/WSGM/Core/GameTools/game-tools.json` (schema version 1),
parsed once and validated: ids match `^[a-z0-9][a-z0-9-]*$` and are unique, the provider is GitHub
or Forgejo, the package is `7z` or `zip`, URLs are HTTPS, file names are plain names, relative paths
have no `..`, every setting has a default among its options. An invalid definition fails the build's
catalog load rather than being skipped.

| Field                                        | Meaning                                                                                                                 |
| -------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------- |
| `name`, `description`, `homepage`, `license` | Shown on the tool card                                                                                                  |
| `notice`                                     | The affiliation statement shown beside the tool                                                                         |
| `antiCheatWarning`                           | Shown, and acknowledged, when anti-cheat was found                                                                      |
| `source`                                     | Provider, API base, repository, asset pattern, channel and package type                                                 |
| `architectures`                              | Supported program architectures; anything else is incompatible with a reason                                            |
| `primaryFile`, `identity`                    | The tool's own binary, its `OriginalFilename`, veto tokens and the other mods recognised by their version fields        |
| `proxy`                                      | Offered install names with hints and a default; detection names and patterns used to find copies installed outside WSGM |
| `payload`                                    | Files the package must contain, extra directories per version family, exclusions, and files that can never be excluded  |
| `configFiles`                                | Configuration the tool owns and edits itself                                                                            |
| `replaceable`                                | Game-shipped files that may be backed up and replaced                                                                   |
| `legacyFiles`                                | Files of an older manual install that "Replace existing installation" backs up and removes                              |
| `runtimeArtifacts`                           | Logs the tool writes at runtime, removed on uninstall                                                                   |
| `requiresUpscaler`                           | Upscaler families the tool needs as input, with the labels its warning names; only a warning                            |
| `families`                                   | Version-family patterns                                                                                                 |
| `settings`                                   | Data-driven options: id, label, section, key, default, options mapped to INI values, per-vendor notes                   |

Patterns use DOS wildcards. A pattern containing `/` matches the relative path only; any other
pattern matches the file name or the relative path. A future tool such as DXVK, dgVoodoo2, ReShade
or ReScaleFrame is a new entry, plus whatever its install mode needs that the current model lacks.

## Releases, downloads and versions

- The latest release is looked up through the shared hosted-source adapter and kept in memory for 6
  hours, because GitHub's anonymous API limit is small. "Check for updates" forces a lookup. Lookups
  set `tools[].checkingLatest`, not `busy`.
- Downloads fail closed. The asset's published digest is matched by exact asset name, normalised to
  64 hex characters, and a missing, malformed or mismatched digest refuses the download. Transient
  failures get at most three attempts, one and two seconds apart; a hash mismatch gets one more
  verified download.
- Packages are extracted once into `<data>\GameTools\packages\<tool>\<release>-<hash>`, keyed by
  version, asset, hash and URL, staged beside it and promoted by a move. Stale stage directories are
  swept. Extraction uses SharpCompress with the shared traversal and link checks. A single top-level
  folder becomes the payload root.
- The payload is validated before anything touches a game: required files present, the primary file
  a PE of the game's architecture with the tool's identity, and the family's extra directories.
- The family comes from the first numeric token (`0.9.x` is `v09`; `0.10.x` or `10.x` is `v10`).
  Versions compare as integer tuples. An installed copy newer than the latest stable is reported as
  newer and never offered an older stable. An equal number with a different release identity is a
  rebuild and counts as an update. An update that crosses families changes the INI format, so it
  needs the user to confirm replacing the configuration (`replaceConfig`).

## The install transaction

`GameToolInstaller` runs install, replace existing, update, repair, configure and remove as a
recoverable transaction in the folder of the chosen program.

1. **Refuse while running.** The service refuses when Steam reports the AppID running or when any
   program in the target or install folder, or the chosen program, is running by full image path. An
   unknown answer counts as running.
2. **Stage.** Download and extract outside the game folder. Cancelling here changes nothing in the
   game.
3. **Plan.** Every target file is classified. Absent files are created. Files WSGM owns are replaced
   after being backed up for the transaction. Game files matching `replaceable` are backed up and
   replaced. Anything else is a conflict and the install is refused. The primary file is installed
   only under the chosen proxy name, never under its own name.
4. **Probe.** A uniquely named `.wsgm-write-test-*.tmp` is created, renamed and deleted in the
   target folder. Failure refuses with "WSGM can't write to the game's folder", which covers
   protected and packaged folders.
5. **Journal.** A `Pending` manifest carrying every planned change and its backup path is written to
   `game-tools.json` before the first game file changes.
6. **Check again, then write.** The running check repeats right before the first write. Each file is
   written to a sibling temporary file and moved into place, clearing ReadOnly first, so a game file
   is always either its old or its new content.
7. **Apply settings and commit.** Settings are written into a freshly copied configuration, or when
   the user changed them. The manifest becomes `Installed` with hashes, sizes and write times.

Any failure rolls back from the journal: created files are deleted and backups restored. An IO or
access error is reported as "a game file is in use or protected" and is never retried. A rollback
undoes an entry only when the file is still the new content, the old content or absent; anything
else was changed by someone else and is left alone. If rollback cannot finish, the manifest becomes
`Failed` and waits for the user's Remove instead of being retried.

Update keeps the proxy name and the existing configuration, replaces binaries and refreshes hashes.
Repair reinstalls the installed release. Configure renames the owned proxy and edits settings; a
rename onto a name another file holds is refused.

Remove deletes owned binaries only while their hash still matches, always deletes owned
configuration, restores backups, deletes the tool's runtime logs and the now-empty directories WSGM
created. Files changed after installation are left and named. A file that is in use stays in the
manifest so Remove can run again; the backups are deleted only once every original is back.

## The manifest

`%LOCALAPPDATA%\WSGM\game-tools.json` is schema-versioned and written atomically under its own named
mutex, like the emulator store. An unknown schema is refused, never overwritten. Per AppID it keeps
the identity (shortcut flag and last name), the confirmed program relative to the install root with
its size and write time, and one manifest per installed tool:

| Group     | Fields                                                                                                                     |
| --------- | -------------------------------------------------------------------------------------------------------------------------- |
| Release   | tool id, install id, product version, family, release id and tag, asset URL, name and SHA-256                              |
| Placement | proxy name, install root at last write, target directory and program relative to it, architecture, engine                  |
| Files     | owned files (path, SHA-256, size, write time, binary or config), replaced originals with backup paths, created directories |
| Settings  | applied setting values by id                                                                                               |
| Lifecycle | install and update times, state (`Pending`, `Installed`, `Failed`), journal, previous manifest, operation                  |

Backups live under `<data>\GameTools\backups\<appId>\<tool>\<installId>`, with per-transaction
folders beside them for owned files an update replaces. `<data>` is the per-user data folder.

## Conflicts

A file is OptiScaler's own build when its trimmed `OriginalFilename` equals `OptiScaler.dll`,
ignoring case, and none of the seven identity fields mentions ReShade. The veto is checked first. A
file without a readable version resource is not the tool. Identity checks run on every detection
name, every top-level `*.dll` and `*.asi`, and `plugins/*.dll`.

| Kind            | When                                              | What the user can do                             |
| --------------- | ------------------------------------------------- | ------------------------------------------------ |
| `unmanagedTool` | A copy of the tool installed outside WSGM         | "Replace existing installation", after a confirm |
| `needsReview`   | Several copies, or versions whose fields disagree | Review the folder                                |

ReShade and Special K are recognised from their version fields. The proxy options name them as the
conflict on that name, and the suggested name moves past them; WSGM never touches their files. Any
other game file or mod holding a name WSGM needs refuses the install with a message naming the file,
and WSGM suggests the first free proxy name.

"Replace existing installation" backs up every identity-matched copy and the legacy files of a
manual install, removes them and installs as usual. A later Remove restores them.

## Revalidation

Every scan, including the one `open` starts, reconciles the store with the folder Steam reports now:

- A `Pending` manifest left by a crash is rolled back.
- A different install root means the game moved library; manifests are rebased because their paths
  are relative.
- A missing folder is `folderUnavailable` (an unplugged drive or card); nothing can be changed.
- An owned file that is missing, or whose size or write time changed, makes the tool `modified` with
  Repair. Hashes are computed only before a mutation.
- A remembered program that is gone asks again.

| Availability        | Meaning                                                    |
| ------------------- | ---------------------------------------------------------- |
| `ready`             | Everything usable                                          |
| `notInstalled`      | No install folder, or a shortcut that doesn't start an exe |
| `folderUnavailable` | The folder is reported but missing                         |
| `steamUnavailable`  | Steam isn't reachable or didn't report the game            |
| `running`           | The game is running; everything is read-only               |

## OptiScaler

OptiScaler replaces a game's DLSS, FSR or XeSS upscaler with another one and can add frame
generation. WSGM installs the stable `.7z` release of
[optiscaler/OptiScaler](https://github.com/optiscaler/OptiScaler) for 64-bit games only.

- **Proxy names:** `dxgi.dll` (default), `winmm.dll`, `version.dll`, `dbghelp.dll`, `d3d12.dll`,
  `wininet.dll`, `winhttp.dll` and `OptiScaler.asi` for a game that already has an ASI loader. Each
  has a hint. The suggested name is the first free one, wrapping round the list.
- **Excluded from the package:** `setup_windows.bat`, `setup_linux.sh` and the `!! *` marker files.
- **Replaceable with backup:** `libxess*.dll`, `libxell.dll`, `amd_fidelityfx_*.dll`, `fakenvapi.*`,
  `dlssg_to_fsr3_amd_is_better.dll`, `nvngx.dll`, `nvapi64.dll`, and the `D3D12_Optiscaler` and
  `Licenses` folders.
- **Configuration:** `OptiScaler.ini` is owned configuration: kept on update within a family, always
  removed on uninstall. WSGM edits existing keys only, in the first matching section, and never
  creates a section or key. A missing key means the setting does not exist in that version and is
  shown as unavailable. Encoding, BOM, line endings, indentation and inline comments are preserved,
  nothing is written when no value changed, and a ReadOnly flag is restored afterwards.
- **Settings:** DLSS inputs on AMD and Intel (`[Spoofing] Dxgi`, `auto` or `false`, noted as not
  needed on NVIDIA), menu scale (`[Menu] Scale`, Auto or 0.8 to 1.6) and hide startup message
  (`[Menu] DisableSplash`). The GPU vendor comes from LibGPUDriverInteract.
- **Runtime logs removed on uninstall:** `OptiScaler.log`, `fakenvapi.log`, `dlssg_to_fsr3.log` and
  `OptiScaler/OptiScaler.log`.

WSGM is not affiliated with OptiScaler. It downloads the release from the upstream project at the
moment the user installs it and does not redistribute it; the GPL-3.0 package arrives with its own
license files. The tool card shows the homepage, the license and the notice that problems should be
reported to WSGM. The OptiClick study informed behaviour only; that program is not open source and
none of its code is used.

## Anti-cheat policy

Third-party DLLs in a game folder can get an online account banned. Every tool card carries the
warning "Do not use with online games. Anti-cheat may ban accounts." when anti-cheat was found, and
install is refused until the user acknowledges it. The scan is a heuristic, so the absence of a
warning is not a promise that a game is safe.

## Logging

Only transitions are logged, with the prefix `Game Tools: `:

| Level | Line                                                                                 |
| ----- | ------------------------------------------------------------------------------------ |
| Info  | `Game Tools: <operation> <tool> <version> for <appId> as <proxy>: <n> file changes.` |
| Info  | `Game Tools: removed <tool> for <appId>: complete or partial, <n> left, <n> locked.` |
| Info  | `Game Tools: runtime for <appId> set to <path>.`                                     |
| Info  | `Game Tools: game <appId> moved to <root>; rebasing its manifests.`                  |
| Warn  | `Game Tools: <operation> <tool> for <appId> failed and was rolled back: <reason>`    |
| Warn  | `Game Tools: rolling back an interrupted <operation> of <tool> for <appId>.`         |
| Warn  | `Game Tools: download failed verification; fetching it once more.`                   |

No line is written per file scanned or copied.

## Known limits

- OptiScaler is the only tool. DXVK, dgVoodoo2, ReShade and ReScaleFrame are deferred; the catalog,
  manifest and conflict model are meant to take them without changing the transaction.
- Deferred OptiScaler options: FPS overlay and limit (WSGM covers both), FSR4 INT8, frame-generation
  input and output, interpolation count and ASI plugins.
- Only the stable channel is installed. A move from 0.9.x to 10.x is a family change the user
  confirms.
- 32-bit and Arm64 games are incompatible.
- The running check covers the program and the executables at the top of its folder and the install
  root; a game started from a deeper launcher path is caught by Steam's running AppID.
- No live Steam, real game folder or hardware pass has been recorded yet, and the transaction has no
  regression fixtures for a disconnected library, a moved game, a removed program, a proxy-name
  collision or rollback.
