# Browser and guide lookup

The WSGM Overlay's **Tools > Browser** opens websites without leaving the overlay. Enter an address
or search, or explicitly search for the current game's title. Opening the page alone sends no
search. Use Back/Forward/Reload, zoom and the local keyboard. B returns from the web page to its
controls; closing the overlay returns to the game.

The start screen shows an address-entry prompt until a page is selected. The complete native
viewport hides behind keyboards, menus and other in-window surfaces, preserving its layout space.
Suspension completion honors the current visibility, including when a page reopens while suspension
is pending. Replacing the Overlay's native anchor does not change the selected page's lifetime.

Settings > Quick Access chooses **Keep alive** or **Unload**. Keep alive hides, mutes and blocks
input to the page, and asks Chromium to suspend it. Suspension is best effort. Reopening keeps the
same page. Unload releases the controller, environment and helper process; the next open starts a
fresh page. Cookies and site storage remain in the dedicated user profile under `WSGM/Browser`.
Changing the policy applies immediately, including to a hidden browser.

Arbitrary websites run in the separate, medium-integrity `WSGM.BrowserHost` process. An elevated
resident uses the existing least-privilege launcher and verifies the pipe client, executable,
integrity, HWND ownership and DPI context before embedding it. Failure refuses the browser instead
of running content elevated. Websites have no WSGM/Steam/plugin bridge, host objects or web-message
endpoint. Downloads, file URLs, permissions and custom protocols are refused. Opening HTTP/HTTPS
externally requires an explicit browser-control action.

Steam Quick Access opens **Browser** directly in Valve's native browser for that exact Steam window.
Its native address field accepts HTTP/HTTPS addresses, bare hostnames or Google search terms. The
optional **Game search** action fills the identified game's title into the field; only submitting
the query sends it to the search provider. Native Back returns to the caller, and reopening restores
Steam's saved browser history. An unavailable native contract leaves an explanation and retry
action. Big Picture browsing does not require a running game and never fabricates a game identity.

Steam owns its browser's memory, downloads and external-link prompts; WSGM's Keep alive/Unload
setting controls the WSGM Overlay browser. Address editing and search stay in trusted Steam chrome.
Arbitrary websites continue to render in Steam's separate browser view without a WSGM bridge.

`Shell/BrowserSession` owns the WSGM engine lifetime. `Overlay/BrowserView`, `BrowserViewport` and
`OverlayNativeWebViewHost` own chrome, clipping and input forwarding. `Core/Browser` defines the
bounded wire and URL policy. `SteamBrowserSurface` and the toolkit's `overlay-browser.ts` own the
separate native Steam entry and address editing. Installation and development deployment include the
complete self-contained helper payload under `App/Browser`.

`Browser/WSGM.BrowserProbe` is a local validation harness, excluded from the solution and installed
payload. It is not a reporter requirement or an alternative runtime host.

Build, isolated-renderer and headless layout evidence does not establish controller, elevated
embedding or fullscreen-game acceptance. Those live checks remain tracked in issue #220.
