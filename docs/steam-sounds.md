# Steam UI sound packs

Tools > Sounds installs, previews and selects Audio Loader-compatible sound packs. WSGM stores the
library under its per-user `sounds` folder. Preview uses Windows Media Foundation and the default
Windows audio route; it does not change the selected pack or system volume. Leaving the page or
closing the Overlay stops the preview. Stop does not wait on a repository request and invalidates
queued playback requests from the outgoing page. Sound-library and preview groups retain expansion
through service refresh and Overlay reopen within the resident session.

## Pack format and compatibility

A ZIP must contain exactly one `pack.json`, either at its root or inside a pack folder. WSGM reads
Audio Loader manifest versions up to 3: `name`, `author`, `version`, `description`, `ignore` and
`mappings`. Mappings associate exact Steam sound filenames with arrays of relative asset filenames;
multiple files provide random variations. Without a mapping, the same filename is used. Background
music packs are rejected because they are a separate playback workflow.

Steam's install directory is discovered through the existing Steam owner. The current client's
`steamui/sounds` filenames define the supported resources. Unknown mappings are reported and never
guessed. A missing asset, unsupported file type or undecodable audio keeps the stock event. WAV,
MP3, M4A and Ogg are accepted, subject to the Windows preview and Steam Chromium codecs.

Packs, sounds and manifests have no size or count limit, since the toolkit delivers the overrides to
Steam in parts; an empty sound file is treated as missing. The one bound is against a zip bomb: an
archive that expands past 64 MiB is refused whole. Parent traversal, redirected paths and archive
symlinks are refused. Installation stages and validates a pack before replacing its installed
folder; updates retain the folder identity.

Repository browsing reuses the bounded DeckThemes client used by Themes, with Audio Loader's `AUDIO`
filter. Only requested packs are downloaded. Importing the same named local pack updates its
installed identity. Repository packs are identified by their store identity, independent of their
display names.

## Apply, restore and lifecycle

The session owns `SoundPackService`. It serializes explicit actions, saves selection through
ConfigStore and publishes detached audio data URLs through `SteamSoundOverrideSurface`. The toolkit
uniquely resolves Steam's Gamepad UI store and claims only its audio manager's
`PlayAudioURLWithRepeats` member. It retains the exact original property for removal, validates
decoding before admitting a replacement and intercepts only the current `/sounds/` resources. Voice
and chat managers are untouched. WSGM creates no Steam-side sound folder, symlink or backup and
never replaces a Steam-owned audio file.

Restore publishes an empty map. Each reload builds the new map whole and publishes it once; a reload
that fails publishes the empty map, so no stale sound stays. Removing the selected pack removes the
folder first, then saves defaults and retracts its map, so a removal that fails changes nothing.
Missing or broken selected packs fall back to stock audio. CEF replacement cancels decoding from the
old generation; the shared module runtime republishes into the new generation. Disabling CEF removes
the member claim. Selection stays in the per-user config for a later session.

Steam Stable and Beta use the same discovery and shape gates; neither channel gets a guessed
filename or export. An incompatible client leaves stock audio and reports its gate failure through
the existing Steam integration diagnostics.

## Reference and licensing

The inspected reference is [Audio Loader](https://github.com/DeckThemes/SDH-AudioLoader),
specifically `main.py`, `src/index.tsx`, its audio finder and remote installer. Its license file
contains the MIT license (EMERALD0874, 2022) and BSD 3-Clause license (Steam Deck Homebrew, 2022).
WSGM independently implements the documented manifest and playback behavior. It does not copy
Decky's frontend code, port its SteamOS symlink or redistribute community pack assets. Individual
packs retain their authors' licenses; installing a pack does not grant redistribution rights.

## Acceptance still requiring attended checks

Compilation and offline discovery do not prove live audio. The maintainer's manual pass must cover
Windows Steam Stable and Beta, complete and incomplete packs, repeated switching, defaults, preview
failures, active-pack removal, WSGM and Steam restart, CEF reload, client updates and
controller-only navigation. Automated regression suites and the repository gate run after that
manual pass under the contributor guide's manual-first policy.
