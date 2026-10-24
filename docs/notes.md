# Notes

Notes is a small game notebook available in WSGM's Overlay (Tools > Notes, pinnable to Quick access)
and in Steam's Big Picture and in-game overlay (Quick Access > Extensions > Notes, route
`/wsgm/notes`). Both surfaces read and write the same WSGM-owned pages through one session service,
`Shell/NotesService`. Notes stay on this PC: nothing is uploaded, synchronized or sent to a search
provider.

The notebook chooser keeps the current game or Global scope visible. Pages appear as preview cards
with their first text, content types, edited date and pin/read-only status. A blank notebook
explains what its pages can contain and offers **New page**.

Opening a card gives the page the main workspace. Navigate, Text, Ink, Add text and Screenshot stay
near the canvas; Ink shows its own pen, eraser, width and color tools. **Page…** in the Overlay and
**Page options** in Steam hold rename, pin, ordering and confirmed deletion. **Page contents** lets
a controller select a text block or image; arrangement tools remain available for the selection.
Returning from these views keeps the canvas, drawing history and position. Steam image requests
belong to the open page, survive its secondary views and cancel on exit; different page instances do
not share a mutable image cache.

The Overlay uses a compact 240-DIP left sidebar throughout, leaving more space for pages and
websites. Navigation and the selected section remain visible.

Over a game, Quick Access is drawn in that game's Steam overlay window. The toolkit's
`steamInteractionWindow` resolves the window an action came from, and `navigateSteamRoute` opens the
page in that overlay window with its own `Navigate`, the way Steam opens its own pages over a game;
Big Picture's main window keeps its history push.

## Pages and storage

A notebook is either **Global** or one game. A game's notebook is keyed by a stable identity, never
by its title:

| Game                                  | Notebook identity        |
| ------------------------------------- | ------------------------ |
| Steam title                           | `steam:<appid>`          |
| Title imported by WSGM's Game Library | `library:<source>:<key>` |
| Other non-Steam shortcut              | `steam:<shortcut appid>` |
| Application Steam does not know       | `process:<executable>`   |

An imported title keeps its notebook when its Steam shortcut is recreated or it moves to another
drive or SD card, because the Library Manager's source key does not change. When the running game
cannot be resolved (Steam unavailable, two games running), both surfaces say so and show Global
explicitly; nothing is written to a game notebook, or to Global, on the strength of a failed
resolution.

A page (`Core/Notes/NoteDocument.cs`) is a structured document, never HTML:

- text blocks and images, each with an id, position, size and back-to-front order;
- a vector ink layer of strokes (tool, `#RRGGBB` color, width, x/y points, optional pen pressure)
  drawn over the elements;
- title, pin, order, created/modified times, a content revision and an explicit page format.

The store (`Core/Notes/NoteStore.cs`) lives under `%LOCALAPPDATA%\WSGM\notes`:

- `pages/<id>.json`, one small file per page, replaced atomically and durably;
- `assets/<sha256>.jpg|png`, one file per image, named by its content, so editing a line of text
  never rewrites a screenshot and the same image is stored once;
- `notes.json`, the screenshot countdown preference and the last known game names.

A page that does not parse is moved aside as `*.bad.<time>.json` and never overwritten. A page
written by a newer format is shown read-only. Images that no page refers to are deleted only when
the store first loads, before any editor exists, so an undo or a conflict copy can never point at an
image that cleanup already removed. Content is never length-limited; validation checks types,
identities, finite geometry and references.

## Revisions and conflicts

Every content edit (title, text, element placement, ink) names the revision it was made against. If
the page changed elsewhere first, the edit is refused and the editor receives the current page.
Nothing is overwritten:

- typed text offers **Replace with my text**, **Add my text as a new block** or **Discard**;
- ink offers **Apply my ink to the newest version** or **Discard** in the Overlay; Steam reports the
  conflict and keeps the newest page;
- a stale page delete is refused.

Pin and page order are organization, written through their own operations, and do not invalidate an
open editor. A screenshot is inserted into the page's current revision, since it only adds an
element.

The Overlay saves every edit as it is made. Text being typed is kept in the service while the editor
is open, so closing the Overlay mid-sentence and reopening it restores the editor with that text.
The Overlay also remembers the open notebook, page, mode, zoom and scroll position.

## Overlay editing

The page has three explicit modes:

- **Navigate**: drag to scroll, tap to select, drag a selection to move it, drag its corner to
  resize.
- **Text**: tap a text block to edit it, or empty space to add one there.
- **Ink**: pen, highlighter and eraser, three widths, six colors, Undo, Redo and Clear ink (with
  confirmation). The canvas (`Controls/NotesCanvas.cs`) captures each contact and stops gesture
  recognition, so a stroke cannot scroll the sheet or start any Overlay gesture; the sheet's own
  edge swipes are already disarmed while it is open. Leaving Ink returns touch to scrolling.

Strokes are captured at the digitizer's rate, including intermediate points, then simplified once
when the contact lifts. The eraser removes whole strokes, so undo restores them whole and in their
original drawing order. Pen pressure is stored when a pen reports it; finger and mouse strokes do
not need it. Releasing a finished contact preserves its ink and eraser result for saving; losing
capture during an unfinished contact cancels it.

A controller reaches everything through rows below the page: each element can be selected, edited,
nudged, resized, brought to front or deleted. Text is edited in a multiline editor with the
Overlay's on-screen keyboard; a physical keyboard types directly, and selection, copy and paste use
the text box's own handling. Images are decoded at display size only while a page that shows them is
open.

## Steam

`Shell/NotesSteamSurface.cs` registers the page with the toolkit's `SteamPagePatch`, a typed
publication and a closed command vocabulary; `Core/SteamUiAssets/Source/notes.ts` draws it with
Steam's own components and the toolkit UI kit. The page:

- determines which Steam window it is drawn in (`SteamUIStore.WindowStore.SteamUIWindows`, matched
  by `BrowserWindow`). In a game's gamepad overlay it opens that game's notebook from the overlay's
  own app id and process; in Big Picture's main window it uses the session's running-game identity;
- publishes no images; it requests each image (`image`) and keeps it in memory only while the page
  is open, so an edit never resends screenshots. Nothing is put in `localStorage`;
- renders text as plain text through React and ink as SVG polylines from the same strokes, so note
  content is never executed as web content;
- edits text in a `<textarea>` attached to Steam's own virtual-keyboard input hook, found by source
  fingerprint (`TakeFocusAndShowKeyboard`, `BIsElementValidForInput`, `CreateVirtualKeyboardRef`).
  Without the hook the textarea still accepts a physical keyboard.

### Old CEF capability test

The installed client's CEF is Chromium 126 (`libcef.dll` 126.0.6478.183, read from disk on
2026-10-23). The page does not rely on that: when it opens it measures Pointer Events, coalesced
events, `touch-action`, SVG and pointer capture in its own window, logs the result once
(`Notes: Steam page capabilities …` in wsgm.log) and offers Ink only when all four are present.
Otherwise Steam shows the shared vector ink and offers text and element editing, and the Ink button
says the WSGM Overlay is needed. Both surfaces always use the one document format. Steam's ink is
finger/pen/mouse through Pointer Events and SVG; whether touch input reaches a game overlay window
as pointer events on a given device is a live acceptance item.

## Add Screenshot

The countdown is part of the feature: the player leaves Notes to unpause, open a map or move the
camera, and the game is captured when the countdown ends.

The WSGM Overlay also takes screenshots from the desktop. When no application window was behind
Notes, it captures the display the Overlay was on. The Overlay and countdown close before the
capture, and the same page reopens with the image. This works without a running game; application
captures still keep their original process and foreground checks.

`Shell/NotesCaptureTransaction.cs` runs **Armed → Countdown → Capturing → Restoring → Idle**:

1. The page and its exact location are already saved. The player chooses 5 to 10 seconds; the choice
   is remembered.
2. The surface closes and hands the game back. The Overlay's own close releases its capture claim
   and Steam Input lease and, in Game Mode, refocuses the window that was in front before it opened;
   Steam uses its `RaiseWindowForGame` for the exact overlay process (the toolkit's
   `SteamGameWindowActivation`).
3. A small top-center, non-activating, click-through countdown shows the seconds over the game
   (`Overlay/NotesCountdownWindow.cs`). Over a confirmed exclusive-fullscreen game it is not shown.
4. At expiry the application's foreground window or the selected desktop display is captured
   (`Shell/NotesGameCapture.cs`).
5. The image is stored as an asset and inserted below the page's content, selected and ready for
   ink.
6. The same Notes surface reopens on the same page: the Overlay's Notes section, or Steam's
   `/wsgm/notes` in that game's overlay window, navigated the way Steam opens its own pages there.

Cancellation and failure leave the page intact and report a reason on it:

- opening the WSGM Overlay during the countdown cancels it; Steam's page offers **Cancel
  screenshot**;
- the game exiting, another window in front at expiry, a minimized game, Steam's overlay still open
  over the game, a blank frame (exclusive fullscreen or protected content) or a capture error insert
  nothing and return to the page;
- shutdown cancels and reopens nothing. Only one screenshot runs at a time.

### Capture path investigation

In the order the issue requires, from the installed client and source on 2026-10-23:

1. **Steam.** The client exposes `SteamClient.Screenshots` for screenshots the user already took
   (listing, `GetLastScreenshotTaken`, upload) and a `TakeScreenshot` that belongs to Game
   Recording's timeline and needs background recording. Neither takes a screenshot of an arbitrary
   running game on request; the overlay hotkey would be synthetic input. Not used.
2. **WSGM.** WSGM had no capture path of its own.
3. **Windows Graphics Capture** of the game's window is used. It records only that window's own
   content, never WSGM's Overlay, the countdown or another application, and the capture is refused
   unless the game process owns the foreground window. Steam's in-game overlay is drawn into the
   game's frames, so capture is refused while Steam reports it open over the game. On Windows 11 the
   yellow capture border is turned off where the system allows it. Screenshots are stored as JPEG at
   quality 0.9.

## Steam's native Notes

Inspected in the installed client bundle on 2026-10-23 (no CEF connection). Steam's Game Notes store
one cloud file per game (`notes_<appid>`, `notes_shortcut_<name>`) through `SteamClient.GameNotes`
(`GetNotes`, `SaveNotes`, `SyncToServer`, `SyncToClient`, `ResolveSyncConflicts`), with note content
as rich-text HTML and images uploaded to Valve's cloud through `GameNotes.UploadImage`.

WSGM does not read or write it:

- writing would upload WSGM's notes to Valve's cloud, while WSGM notes are local by default;
- its content model is HTML with uploaded images and no vector ink, so a projection would either
  lose ink and layout or bake them into images;
- it has its own sync and conflict resolution; merging two stores would have ambiguous conflict
  semantics and could overwrite the user's Steam notes.

Steam's own Notes remain available beside WSGM's in Steam's overlay; WSGM never changes them.

## Browser

Notes and the Browser (#220) are independent. URLs typed into a note are plain text; opening them in
the Browser is a later integration and is not required for Notes to work.

## Validation status

Offline: the document, store, service, transaction and Steam request rules have focused tests
(`NoteStoreTests`, `NotesServiceTests`, `NotesCaptureTransactionTests`); the Steam fragment
type-checks in the combined asset. The redesigned library and editor were reviewed in live Big
Picture, including creation, rename, multiline save and compact layout. Overlay renders cover
980×640, 1024×640 and 1280×800. Steam's virtual keyboard on the textarea, pointer ink in a game
overlay window, the overlay-window return navigation, Windows Graphics Capture against real games
(borderless, exclusive fullscreen, HDR) and touch/pen behavior on the device need attended
acceptance; source and tests do not establish them.
