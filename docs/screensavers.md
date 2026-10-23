# Local screensavers

In Big Picture, open **Settings > Customization > Screensaver**. Add an image folder or a pack
folder, then choose it in Steam's usual Screensaver chooser. Steam's game slideshow and bouncing
logo remain available. The WSGM Overlay has the same source controls under **Tools > Screensavers**.
Steam must be running with WSGM integration enabled to change its selection or preview it.

Image folders are read in place. PNG, JPEG, WebP, GIF and BMP are supported. Choose shuffle or
filename order, an interval from 2 seconds to 1 hour, and Fit or Fill. Changes are discovered while
Steam runs; Refresh also rescans and retries a repaired source. A missing or removable folder keeps
its registration and uses the stock fallback until it returns. Removing a source from WSGM never
deletes its files.

Steam owns idle timing. Real Windows session keyboard/mouse activity is merged with native Steam
activity without changing the idle preferences or generating controller input. Native
controller/touch dismissal still wins. Display-off choices continue to respect the screensaver
timeout through the existing display-timeout service.

## Pack format

A local pack is a folder containing `screensaver.json`:

```json
{
  "schemaVersion": 1,
  "id": "example.quiet-orbit",
  "name": "Quiet Orbit",
  "author": "Example author",
  "version": "1.0.0",
  "type": "scene",
  "entry": "index.html",
  "preview": "preview.png",
  "assets": ["index.html", "scene.js", "style.css", "preview.png"],
  "minimumWsgm": "2.4.0"
}
```

`id`, `name`, `author`, `version`, `type` and `minimumWsgm` describe the pack independently of its
location. `type` is `scene` or `images`. Scenes require a relative HTML `entry`; every other
resource must be listed in `assets`. The entry is included automatically. Image packs use
`"type": "images", "folder": "photos"` instead of `entry`; `folder` defaults to the pack root.
`preview` is an optional relative PNG/JPEG/WebP/GIF/BMP thumbnail. It is metadata for consumers;
Steam's current native chooser displays source names. Assets are local HTML, CSS, JS, JSON,
supported raster images or WOFF/WOFF2 fonts. All paths must stay within the pack. Absolute paths,
alternate data streams, traversal, URL-encoded paths, symbolic links and junctions are refused.
Unknown manifest fields or unsupported versions are refused instead of guessed.

Folders may be local or removable, including offline folders that return later. Network shares are
not sources. Limits are 32 registered sources, 4096 directory entries, 2048 images, 16 MiB per
asset, a 64 KiB manifest, and 256 scene assets totaling at most 64 MiB. No photo library is copied
into Steam. The included
[Quiet Orbit example](../examples/screensavers/quiet-orbit/screensaver.json) can be registered
directly as a pack folder.

## Scene boundary and resource lifetime

Pack scripts run in a dedicated, medium-integrity WebView2 process, separate from Steam CEF and
WSGM. The scene receives no host objects, web-message endpoint, SteamClient, WSGM service API, Node
or .NET bridge. Canvas/WebGL and ordinary browser layout are available. The only served files are
explicitly allowed pack assets under a private HTTPS origin. Requests to other origins, navigation,
child frames, workers, downloads, new windows, external protocols and browser permissions are
blocked. Only declared assets can be fetched from the scene's own origin. File pickers, browser
filesystem APIs, WebRTC, script dialogs, browser accelerators, developer tools, password storage and
autofill are disabled. Playback is muted and uses an InPrivate profile.

The trusted parent verifies the renderer's pipe/process identity and integrity, then places it in a
kill-on-close Windows job before authorizing browser creation. Stop, reload, source change, Steam
restart/disconnection, disabling integration, sleep/resume and display change close the controller
and terminate the owned process group. Replacement waits for the old group to exit. A failed
renderer, invalid manifest, missing asset, uncaught script error or undecodable image playlist
leaves Steam's stock bouncing logo underneath; it cannot leave a blank custom frame covering Big
Picture. Unchanged failed scripts are not restarted repeatedly. Refresh, a changed source or the
next activation allows another attempt.

The source controls show discovery, compatibility and playback failures. Scenes default to 30
animation callbacks per second and can be set from 1 to 60. The shared animation queue is bounded to
64 callbacks. This limits normal animation work, not every possible JavaScript loop. Prefer static
backgrounds, modest Canvas/WebGL resolution, cached drawing resources and no unnecessary timers.
Scripts can still use CPU/GPU while active; the entire renderer is destroyed on stop. Treat
downloaded packs as untrusted browser content and inspect their packaged files.
