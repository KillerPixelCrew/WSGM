# Steam CEF plugin example

This package exercises a page, a main-menu entry, an independent QAM tab, a native Performance row,
a library tile addition, a game-page addition and a JSON backend with state publications. It is not
installed or enabled automatically.

Build the package from the repository root:

```powershell
New-Item -ItemType Directory -Force publish/SteamCefExample
./eng/package-plugin.ps1 -Project examples/SteamCefPlugin/SteamCefPlugin.csproj -Archive publish/SteamCefExample/example.steam-cef-1.0.0.wsgmpkg
```

Install the resulting archive through the normal Plugins folder. In WSGM Settings > Plugins,
acknowledge the initial warning, enable the instance, allow its Steam CEF access and Save. Open
Steam Big Picture after it is fully ready. The CEF example menu/page and tab use the same counter
backend; incrementing in one surface publishes to the others. Disable CEF access and Save to retract
all frontend registrations without changing WSGM's own surfaces.

For failure recovery, a throw in any module disables the complete package and records its module and
reason in Settings. Reload is explicit. To recover from an endless loop or a renderer crash, start
WSGM with `--shell --desktop-resident --cef-plugins-off`; no package frontend is injected.

The frontend is unrestricted JavaScript. It receives `api`, can access Steam's session directly and
must register cleanup for any side effects it creates outside the toolkit's registrations. Remove
`IPluginSteamFrontend` from the lifecycle class when no backend is needed.
