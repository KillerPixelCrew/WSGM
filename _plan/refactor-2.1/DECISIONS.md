# Maintainer decisions, 2026-10-03

These are the maintainer's answers to the open questions from the refactor review. They are binding and override `refactor-plan-v2.md` section 4 and any finding solution that disagrees.

## Plan v2 decisions

| # | Question | Decision |
| --- | --- | --- |
| D1 | Shutdown design | Safety-first ordered steps under one deadline. The planning-corrections B3 percentage cutoffs and preliminary drain are dropped. |
| D2 | Byte bounds on untrusted input | Accept exactly the plan v2 list (they refuse, never truncate). Every other count or length cap is removed. |
| D3 | Device Lab (MIT) compiles GPL interop files | **Relicense Device Lab as GPL.** Do not relicense or duplicate the interop files. Update Device Lab's license file, csproj metadata, README/AGENTS and the notices; the SDKs stay MIT. |
| D4 | Guidance (AGENTS.md) edits the batches need | Approved. Show each diff with its batch and apply it. |
| D5 | Title resolving to a shortcut another title owns | Offer it as a second game shortcut: its own Add that creates its own Steam shortcut. |
| D6 | PL2 single home when both values exist | **Keep the device-stored PL2** and drop `BoostWatts` for that layer. |
| D7 | Ally motion sensor order | **WinRT first, like HC.** |
| D8 | Ally power write order | **Mirror HC: SPL, then SPPT+FPPT.** Remove the recorded SPL <= SPPT <= FPPT stepping rule. Writes stay write-through, no gating. |
| D9 | Ally controller entry re-arm | **No readback machinery at all for the Ally (or any vendor except the Claw).** The maintainer: "readbacks are bullshit and must go. The Claw is the only vendor that allows readbacks." Remove "unresolved"/"uncertain" entries, re-arm rules and recovery entries that wait on a readback for every non-Claw package and every host path. A write either dispatched (published as written) or failed to dispatch. |
| D10 | Uninstall/upgrade cleanup of Steam-side state | Restore the files (boot-movie override and `.wsgm-original`, `themes_custom` junction) **and also hand back Steam's startup-movie choice** at WSGM exit when no WSGM movie is chosen, plus at uninstall. |
| D11 | IR catalogs larger than the 32 KiB frame | **Add catalog paging now** (chunked `remotes` replies with a protocol version bump on both firmware and host). No build-time size refusal. |
| D12 | tools/DeckSpike and tools/SteamReceiver | Delete both, plus `InternalsVisibleTo("DeckSpike")`. |
| D13 | Library badge before the first publication | **WSGM shows the library's name** there, not "Internal". There can be several internal libraries, especially on desktop. The toolkit holds no product label; the host supplies the text. |
| D14 | Setup single-file native extraction to temp | **Dropped as security theater.** No change. |

## Security hardening: all dropped

The maintainer rejects every security-hardening finding: "It's a launcher, it has to launch. This is for gaming handhelds, not enterprise PCs." Move these to no-change and remove them from the batches:

- the logon service launching the `boot.json` path elevated (INSTALL-001 security part)
- elevated setup running installers from the user's temp (INSTALL-002, B026)
- `%ProgramData%\WSGM` permissions (INSTALL-003, B027 DACL part)
- de-elevation task XML staged in a user-writable folder (B029)
- single-file native extraction (INSTALL-V-001, D14)
- tray relay refusal into higher-integrity targets (U05-LFB-016 and related): keep today's relay
- the bridge `hostId`/nonce identity, sender, ownership or integrity checks, and any similar same-user threat mitigations anywhere in the plan

Keep the functional parts of those batches: clean service stop, the updater download location if it fixes a real functional bug, truthful setup partial-change reporting, answers applied last, exact component matching.

## Other answers

| Topic | Decision |
| --- | --- |
| Tray relay in Game Mode | Keep today's relay unchanged. |
| config.json from a newer WSGM or with unknown recovery enums | Best effort as today: load what is understood. No read-only mode. |
| Overlay display-mode selector | Target the display the overlay sheet is shown on, not `paths[0]`. |
| Add Steam Library / Replace launch action pickers | Keep the native Windows pickers (navigation suspended while open). |
| Application-profile Name/Process fields | Convert to controller-reachable press-to-edit rows. |
| Escape / 3 s timeout during shortcut capture | Keep the existing binding; only an explicit Clear clears it. |
| SD-card library marker watcher | Keep watching every ready drive; fix the comment. |
| Plugin Steam UI modules | Register them dynamically when the plugin becomes ready. |
| Overlay sheet reads of idle timeouts and Windows policies | Move them off the UI thread; values fill in a frame later. |
| RTSS (USER-001) | Starts with WSGM, kept alive at all times (restart on exit, no cooldown), not launched when RTSS integration is switched off, never killed. |
| Modern Standby wake-device line | Remove the 16-device cap (no-arbitrary-limits); the row may wrap. |
| File picker | No paging, as plan v2 already says. |
