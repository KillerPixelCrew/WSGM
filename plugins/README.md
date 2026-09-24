# Bundled plugins

Every plugin WSGM ships is inside its setup. Setup installs the one device plugin whose hardware
rules match the machine, offers the common plugins, and installs nothing else. A plugin fix reaches
users with the next WSGM release; the updater always updates WSGM as a whole.

## Curated files

`curated/<id>.json` is the only place a plugin's status is set, and the only file a maintainer edits
to add, change or remove a bundled plugin. The package never grants itself a status.

| Field        | Meaning                                                                         |
| ------------ | ------------------------------------------------------------------------------- |
| `id`         | The plugin id; the file is named after it.                                      |
| `origin`     | `first-party` (built in this repository) or `community` (reviewed third party). |
| `validation` | `hardware-tested` (tested on real hardware) or `blind` (built from references). |
| `bundle`     | `false` keeps a plugin out of the setup, for example a scaffold.                |
| `project`    | First-party: the project directory in this repository.                          |
| `source`     | Community: `repository`, the reviewed `commit` SHA and the `project` directory. |
| `review`     | Community: the review `date` and the submission `issue` link.                   |
| `contact`    | Community: the developer contact WSGM shows when it warns about the plugin.     |

A community entry looks like this:

```json
{
  "id": "community.device.ayaneo-kun",
  "origin": "community",
  "validation": "blind",
  "bundle": true,
  "source": {
    "repository": "https://github.com/example/wsgm-ayaneo-kun",
    "commit": "0123456789abcdef0123456789abcdef01234567",
    "project": "src/AyaneoKun"
  },
  "review": { "date": "2026-09-24", "issue": "https://github.com/KillerPixelCrew/WSGM/issues/0" },
  "contact": "https://github.com/example"
}
```

## Community submissions

Community plugins are submitted as source, never as binaries.

1. Open a "Plugin submission" issue with the repository, the commit to review, the target hardware,
   how you tested it and a contact.
2. A maintainer reviews the code at that commit. Rejection closes the issue.
3. On approval the maintainer adds `curated/<id>.json` pinning that commit. CI builds only the
   pinned commit, never a branch or tag.
4. Every WSGM release builds the pin against that release's SDK. A new plugin version is a new
   submission with a new commit, review and pin.

The build that compiles community code runs in a CI job with a read-only token and no secrets, and
hands the package on as an artifact.

## Building against a WSGM version

Reference the SDK as a package, `WSGM.Device.Sdk` for a device plugin or `WSGM.Plugin.Sdk` for a
common one. CI packs the SDK from the WSGM release into a local feed and builds your commit with the
reference forced to that version; nothing is published to nuget.org. To reproduce it locally, check
out WSGM at the release tag and run:

```powershell
dotnet pack src\WSGM.Device.Sdk\WSGM.Device.Sdk.csproj -o C:\wsgm-feed -p:Version=2.0.0
dotnet build <your project> -p:RestoreAdditionalProjectSources=C:\wsgm-feed
```

A package is built for exactly one WSGM version: packing stamps `wsgmVersion` into its manifest, and
WSGM refuses a package built for another version.

## Outdated plugins

If a pinned commit no longer builds against a new WSGM release, the plugin is not bundled and
`bundle.json` lists it as outdated with the build log and the developer contact. Before that update,
WSGM's updater names the plugin and its contact and recommends staying on the current version. A
user who updates anyway keeps the old file, which WSGM then refuses and reports, until the author
submits a commit that builds.
