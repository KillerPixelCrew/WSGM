# Game Tools

Game Tools installs, updates, configures and removes third-party per-game modifications in a game's
own folder, from Steam's game menu or from the Overlay. The first delivery ships one tool,
OptiScaler, from a stable and a nightly release channel. This guide covers the owners, how a game's
program is found, the tool catalog and its channels, the game list, the install transaction and its
manifest, conflicts, revalidation and the policies around third-party code. The product decisions
behind it are in [decisions.md](decisions.md#steam).

## Owners

| Part                         | Files                                                                                 | Owns                                                                                                        |
| ---------------------------- | ------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------- |
| PE facts                     | `Core/Infrastructure/PeImage.cs`                                                      | Architecture from the COFF machine and the version-resource identity fields; shared with the emulator owner |
| Folder walk                  | `Core/GameTools/GameFolderLayout.cs`                                                  | One bounded walk per scan, and the guard against folders too broad to be a game folder                      |
| Runtime resolution           | `Core/GameTools/GameRuntimeResolver.cs`                                               | Ranking every executable and choosing the one the game runs, or asking                                      |
| Upscaler and anti-cheat scan | `Core/GameTools/UpscalerScan.cs`, `AntiCheatScan.cs`                                  | Standalone upscaler and frame-generation libraries; anti-cheat markers                                      |
| Catalog                      | `Core/GameTools/game-tools.json`, `GameToolCatalog.cs`                                | The embedded, validated tool definitions                                                                    |
| Releases                     | `Core/GameTools/GameToolReleases.cs`, `GameToolVersions.cs`                           | Latest-release lookup, package cache, payload validation and version families                               |
| Configuration edits          | `Core/GameTools/GameToolIni.cs`                                                       | Editing existing keys of a tool's own INI file                                                              |
| Admission                    | `Core/GameTools/GameToolAdmission.cs`                                                 | Whether a request may start: settings, install preconditions, interrupted changes, the program choice       |
| Transaction                  | `Core/GameTools/GameToolInstaller.cs`                                                 | Planning, journaling, copying, rollback, rename, reconfigure and remove                                     |
| Recovery                     | `Core/GameTools/GameToolRecovery.cs`                                                  | Rolling back interrupted transactions once the game is closed, and rebasing a moved game                    |
| Running check                | `Core/GameTools/GameRunningCheck.cs`                                                  | Whether the game is closed, failing closed, from the `IGameProcessProbe` answers the session supplies       |
| Durable state                | `Core/GameTools/GameToolStore.cs`                                                     | `game-tools.json`: remembered programs, identities and per-tool manifests                                   |
| Service                      | `Shell/GameTools/GameToolsService.cs`                                                 | Steam and process answers, scans, state for both surfaces, one mutation at a time, progress                 |
| Game list                    | `Shell/GameTools/GameToolsLibraryProjection.cs`                                       | Every game with tools and each tool's version, load name and status, from the store and cached releases     |
| Steam page                   | `Shell/GameTools/SteamGameToolsSurface.cs`, `Core/SteamUiAssets/Source/game-tools.ts` | The routed Big Picture page, its publication and commands                                                   |
| Steam entry                  | `Shell/Library/SteamGameContextMenuBackend.cs`                                        | The game menu's "Game Tools…" item                                                                          |
| Overlay page                 | `Overlay/GameToolsView*.cs`, through `OverlaySources.GameTools`                       | The game list, game page, program chooser and confirmations in the Overlay, calling the service directly    |

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
4. State is published as `wsgm.game-tools` whenever the service raises `Changed`, and the game list
   as `wsgm.game-tools.library` (see [The game list](#the-game-list)). Commands are `open`,
   `refresh`, `checkUpdates`, `cancel`, `chooseRuntime`, `install` (with an optional `channel`),
   `update`, `switchChannel` (`appId`, `toolId`, `channel`, optional `replaceConfig`), `repair`,
   `configure` and `remove`, each carrying the AppID it acts on, and `refreshLibrary` without a
   payload.

The Overlay reaches the same service through `OverlaySources.GameTools` (null in overlay-test). It
calls `ReadAsync(appId)` when a page opens and `Peek(appId)` on `Changed`, which re-renders from the
cached scan without walking the folder again, and `PeekLibrary()` for the game list. Both surfaces
get the same `GameToolsState` and `GameToolsLibrary` records, the same actions and the same
refusals.

Mutating commands return as soon as they are admitted, because the Steam bridge must answer
promptly. The work runs in the background and its outcome lands in the game's state as `notice` or
`error` once `busy` returns to null. Only `chooseRuntime` waits for its rescan. One Game Tools
change runs at a time across all games; a command for another game is refused while one runs.
Download and extraction can be cancelled; once game files are being written the transaction
completes or rolls back.

### Progress

`busy` names the game, the tool (`toolId`), the operation and a stable `phase` id. Install, replace,
update, a channel switch and repair pass through the four steps both pages draw:

| `phase`    | Step              | Cancellable |
| ---------- | ----------------- | ----------- |
| `download` | Download          | Yes         |
| `verify`   | Check the package | Yes         |
| `copy`     | Back up and copy  | No          |
| `settings` | Apply settings    | No          |

`starting` comes before the first step, `rescan` after the last while the folder is read again,
`settings` alone is a configure and `remove` a removal. `detail` is the phase as one line, such as
"Downloading OptiScaler 0.9.4 from GitHub"; `bytes` and `total` count the download; `cancellable`
says whether `cancel` still stops the work; `channel` and `version` name what is being installed
once the release is known. Only byte progress is throttled; a new phase is published at once.

A configure's notice names what it saved and where, such as "Menu scale saved to OptiScaler.ini." It
makes no claim about when the game picks the value up, because that is not documented here.

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

## The game list

`GameToolsLibraryProjection` builds `GameToolsLibrary` from `game-tools.json` and the cached release
answers; it never walks a folder or contacts the network. `IGameToolsSource.PeekLibrary()` returns
the cached list and starts the first read; `ReadLibraryAsync()` awaits a read. The service reads it
again after every scan (which includes every operation's closing rescan) and after every release
lookup, one read at a time, and raises `Changed`. Steam receives it as `wsgm.game-tools.library`;
`refreshLibrary` asks for a fresh read.

| Field                             | Meaning                                                                                      |
| --------------------------------- | -------------------------------------------------------------------------------------------- |
| `loading`, `error`                | Before the first read; why the store could not be read                                       |
| `games[]`                         | Games with at least one installed tool, by name: `appId`, `name`, `isShortcut`, `tools[]`    |
| `withTools`, `updates`            | The "With tools" and "Updates" counts; "All" is the Steam library's own count                |
| `tools[].toolId`, `toolName`      | The tool                                                                                     |
| `tools[].version`, `versionLabel` | The installed release's version, and the same with channel and build date for a dated build  |
| `tools[].channel`, `channelLabel` | The channel the installation follows; `defaultChannel` is true when no channel tag is needed |
| `tools[].proxyName`               | The load name                                                                                |
| `tools[].status`, `statusLabel`   | See below; the label is the chip's text, such as "Update to 0.9.5" or "New build 1 Nov"      |
| `tools[].detail`                  | The row's line, such as "OptiScaler 0.9.4 as dxgi.dll"                                       |
| `tools[].latestVersion`           | The channel's latest version when an update is available                                     |
| `tools[].changedFiles`            | How many owned files are missing or changed                                                  |

The status is `interrupted` for a `Pending` or `Failed` manifest, `folderUnavailable` when the
target folder is missing, `modified` when an owned file is missing or its size or write time changed
(the quick revalidation of the manifest's own files, nothing else), `updateAvailable` only when the
channel's latest release is already cached and newer by the update rule, and `upToDate` otherwise.
The folder comes from this session's scan of the game when there was one, else from the root the
manifest recorded at its last write, so a game moved to another library reads as unavailable until
its page is opened and the rebase runs.

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
| `channels`                                   | Release channels, the default first: id, label, description, source and an optional version rule (see below)            |
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

## Release channels

A tool lists one or more channels. Each has a `source` (provider, HTTPS API base, `owner/name`
repository, asset pattern, hosted channel filter and package type) and is validated as strictly as
the tool: unique ids matching `^[a-z][a-z0-9-]*$`, a label, a supported provider, a well-formed
repository, `7z` or `zip`, a compiling asset pattern and an HTTPS base. A channel whose release tags
are not versions sets `versionPattern`, a pattern over the asset name with a `version` group and an
optional eight-digit `build` date group; the catalog load fails when it has no `version` group.

OptiScaler has two:

| Channel   | Source                                                | Version from                                                                         |
| --------- | ----------------------------------------------------- | ------------------------------------------------------------------------------------ |
| `stable`  | `optiscaler/OptiScaler`, prereleases skipped          | The release tag (`v0.9.4` is `0.9.4`)                                                |
| `nightly` | `optiscaler/OptiScaler-nightly`, prereleases accepted | The asset: `OptiScaler_v10.0.0-pre1_20261031.7z` is `10.0.0-pre1`, built 31 Oct 2026 |

The main repository's `nightly` tag carries no assets and only links to the nightly repository. A
nightly release is tagged `nightly-YYYYMMDD` with one `OptiScaler_v<version>_<YYYYMMDD>.7z` asset
and a GitHub-published digest, so the same fail-closed digest check applies. A dated build is shown
as its version, channel and date, such as "10.0.0-pre1 · nightly 31 Oct 2026"; the date comes from
the asset name only.

The channel is chosen per game and tool. Before installation the page's draft carries it, stable by
default, and `install` sends it; the manifest records it (`channel`, empty meaning the default) and
every later update and repair follows it. Update detection is the usual rule, so a new nightly of
the same number but another release identity offers an update. Moving an installed tool to another
channel is always the user's explicit `switchChannel`, never an update: it installs that channel's
latest release through the same transaction even when it is older, such as nightly 10.x back to
stable 0.9.4, and it needs `replaceConfig` when the family changes. `tools[].channels[]` gives each
channel's `latestVersion`, `latestLabel`, `buildDate` (`yyyy-MM-dd`), `latestError`,
`checkingLatest`, `current` and `changesConfig`, so a page can label "Switch to nightly 10.0.0-pre1"
and know whether it needs the confirmation.

## Releases, downloads and versions

- The latest release of each channel is looked up through the shared hosted-source adapter and kept
  in memory for 6 hours per channel, because GitHub's anonymous API limit is small. "Check for
  updates" forces a lookup of every channel and reports the outcome of the channel the game follows.
  Lookups set `tools[].checkingLatest` and the channel's `checkingLatest`, not `busy`.
- Downloads fail closed. The asset's published digest is matched by exact asset name, normalised to
  64 hex characters, and a missing, malformed or mismatched digest refuses the download. Transient
  failures get at most three attempts, one and two seconds apart; a hash mismatch gets one more
  verified download.
- Packages are extracted once into `<data>\GameTools\packages\<tool>\<release>-<hash>`, keyed by
  version, asset, hash and URL, staged beside it and promoted by a move. Stale stage directories are
  swept. Extraction uses SharpCompress with the shared traversal and link checks. A single top-level
  folder becomes the payload root; the nightly package has several and keeps its root.
- The payload is validated before anything touches a game: required files present, the primary file
  a PE of the game's architecture with the tool's identity, and the family's extra directories.
- The family comes from the first numeric token (`0.9.x` is `v09`; `0.10.x` or `10.x` is `v10`).
  Versions compare as integer tuples. An installed copy newer than the latest stable is reported as
  newer and never offered an older stable. An equal number with a different release identity is a
  rebuild and counts as an update. An update or channel switch that crosses families changes the INI
  format, so it needs the user to confirm replacing the configuration (`replaceConfig`).

## The install transaction

`GameToolInstaller` runs install, replace existing, update, repair, configure and remove as a
recoverable transaction in the folder of the chosen program.

1. **Refuse while running.** `GameRunningCheck` refuses when Steam reports the AppID running or when
   any program in the target or install folder, or the chosen program, is running by full image
   path. An unknown answer counts as running. The session supplies Steam's answer and the process
   lookup through `IGameProcessProbe`, so the decision is testable without Shell.
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

Every scan of an existing folder, including the one `open` starts and the one each mutation runs
first, reconciles the store with the folder Steam reports now through `GameToolRecovery`:

- A `Pending` manifest left by a crash is rolled back once the game is known to be closed; until
  then it stays pending and the tool shows the interrupted state. A finished rollback is published
  once as the game's notice. One that can't restore every file becomes `Failed` and waits for
  Remove; one whose drive is disconnected stays pending for the next scan.
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
  `dlssg_to_fsr3_amd_is_better.dll`, `nvngx.dll`, `nvapi64.dll`, and the `D3D12_Optiscaler`,
  `Licenses` and `OptiScaler` folders.
- **v10 layout:** from 10.x (the nightlies) every bundled upscaler, frame-generation and helper file
  sits in the `OptiScaler` subfolder, including `D3D12_OptiScaler` and
  `Streamlined_fetcher_windows.bat`; only OptiScaler itself, `OptiScaler.ini`, the setup scripts and
  `Licenses` stay in the root, and `fakenvapi.dll` is no longer bundled. The `v10` family requires
  the `OptiScaler` folder in the payload. Moving from 0.9.x removes the root files the old version
  owned, restoring the game's originals from their backups, and Remove deletes the subfolder's owned
  files and the directories WSGM created. "Replace existing installation" takes over a manual v10
  install's `OptiScaler` folder. Streamline files a user fetches into `OptiScaler/streamline` are
  not WSGM's and stay.
- **Configuration:** `OptiScaler.ini` is owned configuration: kept on update within a family, always
  removed on uninstall. WSGM edits existing keys only, in the first matching section, and never
  creates a section or key. A missing key means the setting does not exist in that version and is
  shown as unavailable. Encoding, BOM, line endings, indentation and inline comments are preserved,
  nothing is written when no value changed, and a ReadOnly flag is restored afterwards.
- **Settings:** DLSS inputs on AMD and Intel (`[Spoofing] Dxgi`, Automatic (`auto`) or Off
  (`false`), noted as not needed on NVIDIA), menu scale (`[Menu] Scale`, Automatic or 0.8 to 1.6)
  and hide the startup message (`[Menu] DisableSplash`). The keys exist in both the 0.9 and the v10
  files. The GPU vendor comes from LibGPUDriverInteract.
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

## The Overlay page

Steam › Game Tools in the Overlay (`Overlay/GameToolsView*.cs`) opens the game whose Steam page is
on screen, as the cog menu does, and otherwise shows the game list. Back from a game returns to the
list.

- **Game list.** Search, then the All, With tools and Updates filter with their counts. "With tools"
  lists `GameToolsLibrary` rows: capsule art, the tool, version and load name, a channel tag for a
  non-default channel and the status chip. "All games" is a two-column card grid of the Steam
  library, paged with Load more. Capsules come from `SteamArtwork.FindCapsuleFile`: the game's
  custom Wide artwork, else the header Steam cached locally; nothing is downloaded and each image is
  read only once its card is on screen.
- **Game page.** A game card with art, name, install folder and the facts for program, upscalers,
  anti-cheat and GPU, with "Change program…"; it shrinks to one line once a tool is installed or
  working. Anti-cheat shows an error banner; availability (`running`, `folderUnavailable`,
  `notInstalled`, `steamUnavailable`) shows the service's reason and locks the controls. One card
  per catalog tool: icon, name, status and channel tags, the latest version, the description or what
  is installed, one primary action (Install, Update, Switch to another channel, Repair or Replace
  existing installation…), the Release channel, Load as and settings rows (a draft before
  installation, saved at once after it), Check for updates and Repair, "Remove <tool>…" apart and
  marked destructive, and the license, homepage and affiliation notice.
- **Installing.** The card shows the four steps from `busy.phase`, the byte bar and Cancel while
  `cancellable`, and says what cancelling does. The settings stay visible and locked. A download
  tick updates the bar in place; a new phase redraws the card. Back leaves while the work continues.
- **Program chooser.** A nested level lists the resolver's candidates as radio rows with the file,
  its role, architecture, folder and reasons, offers "Browse for the program…" through the in-window
  file picker and explains a truncated scan.
- **Confirmations** use the in-window surface, centred (`OverlaySurfaceStack.ShowConfirm`), with
  Cancel focused: installing into a game with anti-cheat, replacing an existing installation, an
  update or channel switch that replaces the configuration, and Remove, which says how many files
  are deleted and restored.

Drafts (channel, load name, settings) live in the view until the game is left, and keyed cards keep
their editors through publications. `tests/WSGM.UiTests/Overlay/GameToolsViewTests.cs` captures the
list, a game without tools, an installed tool with an update, installing, the program chooser, the
anti-cheat confirmation and the nightly draft at 1280 × 800 and 980 × 640 from explicit fakes.

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
- Stable and nightly are the only channels. A move between 0.9.x and 10.x, by update or by channel
  switch, is a family change the user confirms. Nightlies are untested upstream builds.
- 32-bit and Arm64 games are incompatible.
- The running check covers the program and the executables at the top of its folder and the install
  root; a game started from a deeper launcher path is caught by Steam's running AppID.
- No live Steam, real game folder or hardware pass has been recorded yet, and the transaction has no
  regression fixtures for a disconnected library, a moved game, a removed program, a proxy-name
  collision or rollback.
