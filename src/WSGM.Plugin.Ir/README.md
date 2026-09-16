# WSGM IR plugin

An independent `wsgm.infrared` package that owns its command library, the endpoint protocol and the XIAO IR Mate
firmware. It uses the common Plugin SDK only, with no Device SDK dependency, so a Device plugin can run alongside it.
Hardware acceptance for #52 passed on the reference XIAO on 2026-09-11.

What works today: endpoint identity and version checks over USB serial or the local network, bounded raw learn and send,
cancellation, command and scene storage, backup and restore, named actions, the endpoint's built-in remotes exposed as
host actions, USB-only Wi-Fi pairing with a per-endpoint token, the common-host lifecycle, and the management forms in
Overlay Tools.

The package loads collectibly alongside a Device-category fixture, and on a live network it has paired, identified
itself and refused unpaired clients. On 2026-09-11 it learned a real HDMI switch remote button over Wi-Fi as a 71-timing
NEC frame (address 128, command 1) and replayed it twice, with the switch changing to input 1 each time.

One thing to keep in mind: a COM port list is discovery information only. Only a successful protocol identity reply
tells you the endpoint is compatible.

## Build and package

```powershell
dotnet test tests/WSGM.Plugin.Ir.Tests/WSGM.Plugin.Ir.Tests.csproj
dotnet publish src/WSGM.Plugin.Ir/WSGM.Plugin.Ir.csproj -c Release -r win-x64 -o publish/plugins/wsgm.ir
python -m venv .codex/ir-tools
.codex/ir-tools/Scripts/python.exe -m pip install platformio==6.1.18
.codex/ir-tools/Scripts/python.exe -m platformio run -d src/WSGM.Plugin.Ir/Firmware
```

The package holds `plugin.wsgm.json`, its entry assembly and its dependencies. Install and enable it through the normal
common plugin package workflow.

Plugin preferences hold the USB serial port, the endpoint connection (`usb` or `wifi`) and an optional Wi-Fi host name
or IP that overrides the paired one. Connect in Overlay Tools verifies the endpoint identity explicitly, but learn, send
and scenes identify the endpoint themselves whenever there is no verified connection, so route automation keeps working
after a restart, a resume or a dropped link without anyone pressing Connect. Identification is read-only, and loading,
changing modes, connecting and pairing never emit IR.

## Wi-Fi pairing

Pairing always runs over the USB cable, whatever connection is selected, because holding the cable is the proof of
possession.

Pair Wi-Fi over USB takes the network name and password, mints a random 48-character token if the plugin does not have
one, and stores the credentials and token on the endpoint. The plugin keeps only the token, the endpoint's mDNS host
name (`wsgm-ir-<last three MAC octets>.local`) and its last address in `endpoint.json`, separate from the command
library, so a backup never carries the secret. The password is never written to plugin state and never published.

Pairing waits up to fifteen seconds for the endpoint to join. If it has not joined by then, that is reported as
Unconfirmed rather than a failure, and Connect shows the join state afterwards. Forget Wi-Fi over USB clears both sides.

With the `wifi` connection the plugin opens plain TCP to port 7521 on the paired host name, or on the preference
override, which may carry an explicit `host:port`. Every request except identify carries the token, and the endpoint
answers `unauthorized` otherwise, so other LAN clients cannot blast commands. The token does travel unencrypted on the
local network.

The endpoint serves one host at a time and the newest connection replaces an older one, so a reconnecting WSGM never
waits on a dead socket, and the firmware closes idle clients after two minutes. Each explicit Wi-Fi action discards the
previous connection and identifies a fresh one before doing any endpoint work, so returning to the desktop later does
not send through an idle socket. An uncertain transmission is never retried.

Firmware 0.1.0 has no network support at all, and Wi-Fi setup against it says so.

## Library and actions

Tools action forms use the existing controller and touch keyboard. Learn takes a device and command name, and Select
accepts the displayed `Device / command` name. The selection survives a plugin restart. Rename and Relearn keep the
command identity, so existing scenes keep pointing at it, and Relearn also preserves an explicit carrier override.
Repeat timing applies to the selected command.

Create or replace scene takes a scene name and a semicolon-separated sequence of displayed command names, plus a delay
after each command. Run and delete take a scene name, and Core callers can use stable IDs instead. A command a scene
references cannot be deleted until those references are gone. Action editors keep drafts separate from published state,
and only the explicit action button dispatches anything.

The host stores `library.json` in its assigned private state directory. Learned signals are complete payloads, not
firmware slot numbers. `library.backup.json` is an explicit backup in the same directory, so copy it somewhere else if
you care about disk loss. An invalid replacement library never overwrites the current file, and import requires that
backup to exist. The command and scene limits are host validation bounds rather than firmware slots.

## Built-in remotes, from the host

Read built-in remotes asks the endpoint which remotes its firmware carries and publishes their ids, so the other three
actions can name them: Press a built-in remote button, Run a built-in remote sequence, and Set a built-in air
conditioner.

Ids are free text validated against that catalog rather than a dropdown, because the SDK captures an action's choices
before the plugin starts and cannot learn them from hardware. An id the catalog does not know triggers one fresh read
before it is refused, so a reflashed endpoint does not need a restart.

Firmware below 0.4.0, an unknown id, a climate state outside what the remote declares, and a busy endpoint are all
refusals, and none of them emit anything. A sequence reports that it started, and the wait argument polls the endpoint's
own `sequenceRunning` flag. Cancelling that wait sends
`cancel`, which is its own operation and never a retry.

Core automation can call `send` with a `command` ID or `scene` with a `scene` ID. WSGM authors those calls as ordered
steps in Settings > Display, run at Game Mode entry and leave and at desktop startup and wake. Entry stops at the first
step that did not succeed, nothing is retried, and a step the plugin rejected earns no leave-side compensation, so a
refusal never emits.

Transmission returns `Dispatched`. An endpoint acknowledgement proves the IR went out, not that a TV or HDMI switch
changed state. Uncertain operations are not retried, and a failed exchange drops the connection so the next operation
identifies the endpoint again before doing anything else. Endpoint refusals like a learn timeout or an overflowing
capture show up as plain instructions in Tools. Plugin suspend closes the connection, and replacing the device does not
erase the host library.

The built-in button action takes an optional `delay-ms` from 0 to 5000, default 0. After the endpoint acknowledges the
press, the plugin waits that long before completing the action and admitting the next one. For a three-second HDMI
switch power cycle, configure separate button actions for Port 3, then Power with `delay-ms: 3000`, then Power with no
delay. Leave actions run after the desktop layout and Explorer restoration. Power is a toggle when that is what the
remote declares, and an IR acknowledgement tells you nothing about the switch's actual power state. Cancellation
interrupts the pause without repeating the press.

## Firmware remotes

Firmware 0.4.0 can carry complete remotes. Each one is a folder under `Firmware/remotes/` for tracked examples, or the
untracked `Firmware/remotes.local/` for your own devices, which wins on the same id. The folder name is the remote's id,
and the folder holds `remote.json` plus an optional
`index.html`.

The build step `embed_remotes.py` checks every definition against the pinned IRremoteESP8266 sources, fails the build on
any error, compresses the pages and embeds everything. The tracked
`remotes/hisense-tv` maps Hisense's published discrete code table onto a remote-shaped page.

`remote.json` has a `name` and at least one of `buttons` or `climate`:

- A button with `protocol` and a hex `value` sends a known code. NEC can take `address` and
  `command` instead, and a protocol that carries a byte state takes `state`. Optional `bits` and
  `repeats` work as in `sendCode`, and `defaults` shares a `protocol`, `address`, `bits` or
  `repeats`.
- A button with `ac` sends one fixed air-conditioner state using the `sendAc` fields.
- A button with `raw` sends learned `carrierHz` and `timingsUs`, with optional `repeats` and
  `gapMs`.
- `climate` declares an air conditioner: `protocol`, optional `model` and `celsius`, the supported
  `modes` and `fans`, `minDegrees`, `maxDegrees`, and `swing` as `none` or `toggle`.
- `sequences` list steps of `{ "button": id }` and `{ "delayMs": n }`, at most 32 steps and ten minutes of delay in
  total.

Ids are lowercase letters, digits and dashes, and a custom page may only reference ids that exist.

### The web interface

Once the endpoint is on Wi-Fi and web credentials are set over USB with the `web` operation, a browser opens
`http://<endpoint>/` using HTTP Basic authentication, which browsers can remember.
`GET /` lists the remotes and `GET /remotes/<id>/` serves a remote's page. Without an `index.html`
the firmware serves a generated page with the remote's buttons, sequences and climate controls.

Pages act through relative requests carrying the header `X-WSGM-IR: 1`:

- `POST buttons/<button>` answers `200` with status `transmitted`.
- `POST sequences/<sequence>` answers `202` with status `started`.
- `POST climate` takes a JSON body with the fields of the `climate` operation.
- `GET remote.json` returns the remote's catalog entry.

A busy endpoint answers `409`, an unknown id `404`, and a refused request `422`, each with a JSON
`status`. A missing `X-WSGM-IR` header or unset web credentials answer `403` in plain text, and an absent or wrong
password answers `401`. The required header is what stops another website pressing buttons through credentials the
browser saved.

Basic authentication over plain HTTP sends the password readable on the local network, same as the pairing token, so do
not reuse a password from anywhere else. A generated climate page remembers the last state it sent in the browser,
because IR cannot report what the unit is actually doing.

## The reference hardware

The [Seeed wiki](https://wiki.seeedstudio.com/XIAO_IR_Mate_Smart_IR_Remote/) links the
[hardware archive](https://files.seeedstudio.com/wiki/XIAO_IR_MATE/XIAO_IR_REMOTE_hardware.zip) and
[ESPHome source](https://github.com/Seeed-Studio/xiao-esphome-projects/tree/main/projects/xiao_smart_ir_mate). I looked
at both on 2026-09-09 and kept them locally under `_ref/XiaoIrMate`. The supplied software specifies transmitter GPIO3,
active-low receiver GPIO4, touch GPIO5 with pull-down, motor GPIO6 and one WS2812 GRB LED on GPIO7. Our firmware uses
the IR library's standard active-low demodulating receiver handling and a NeoPixel driver rather than a plain GPIO LED.

Firmware 0.4.0 implements protocol 1 over USB CDC at 115200 and, once paired, over TCP port 7521 with mDNS advertisement
as `_wsgm-ir._tcp`, and serves its built-in remotes over HTTP on port 80.

Firmware 0.3.0 added `sendCode`, `sendAc` and `protocols`, so an appliance whose remote is lost can be driven from
published codes or the library's A/C encoders. It also enlarged the USB receive queue to the frame limit: 0.2.0 kept the
core's 256-byte default and answered `malformed` when a full raw payload arrived over USB in one write.

Wi-Fi credentials, the token and the web credentials live in the ESP32 NVS `wsgmir` namespace. Built-in remotes are
fixed at build time, and there is no runtime command storage and no cloud dependency. The RGB LED and motor give brief
feedback, and touch gives feedback without transmitting a user command.

### The carrier frequency

The current envelope-capture implementation does not measure carrier frequency. Payloads distinguish
`assumed`, `protocol`, `measured` and `manual` provenance, and this firmware returns an explicitly assumed 38 kHz.
Whether the receiver hardware could measure one is still unknown. A separate per-command override preserves the original
captured value and its provenance, and the Tools UI shows the last learned command's carrier status, an override slider
and a reset action.

Do not read a measurement into a Pronto frequency field: ESPHome's
[Pronto decoder](https://github.com/esphome/esphome/blob/dev/esphome/components/remote_base/pronto_protocol.cpp)
currently just assigns 38000 during decode. That tells you about the software path, not about the receiver's hardware
limits.

## Flashing and recovery

Before replacing firmware, identify the chip and save its entire flash with esptool. Keep that backup private, because
the factory configuration may include network credentials. Only flash the confirmed XIAO ESP32-C3 endpoint.

For an attended upload:

```powershell
.codex/ir-tools/Scripts/python.exe -m platformio run -d src/WSGM.Plugin.Ir/Firmware -t upload --upload-port COM3
```

COM3 was this workstation's port, not a portable identity. Use whatever actually enumerated.

On this workstation, the bundled esptool 4.5.1's stub stalled during flash reads. esptool 5.1.0 with
`--no-stub` read the full 4 MiB and uploaded all four PlatformIO images. The private factory backup is
`.codex/issue-52/factory-flash.bin`, SHA-256
`0e7b02cb0e63d6e4fa4642d9f9ef512b68b89ff144f6d371e00c4be432de391f`.

The ESP32-C3 ROM loader is the recovery path, and Seeed also links a factory firmware flasher from the wiki. Reflashing
does not touch the host command library or the pairing file, but it does not clear NVS either. Forget Wi-Fi over USB, or
an esptool `erase-flash`, removes the stored credentials.

A firmware build is not the same thing as a successful capture or verified appliance behaviour, so hardware acceptance
has to keep those apart.

## What has actually been tested

**2026-09-11, reference XIAO, firmware 0.2.0.** Flashed as above, and the running C# plugin identified it as firmware
0.2.0 / protocol 1 with host name `wsgm-ir-15ef50`. Live USB checks passed protocol mismatch rejection, malformed-frame
recovery, invalid-send refusal, invalid Wi-Fi argument refusal, learn cancellation, learn timeout status and idle health
readback.

Later the same day the plugin paired the endpoint over USB. It joined the network, resolved as
`wsgm-ir-15ef50.local`, and the plugin verified its identity over Wi-Fi through both the paired name and an explicit
`host:port`. From an unpaired LAN client, identify answered while health, send, learn and cancel returned `unauthorized`
and `wifi` returned `usb-only`; a learn over Wi-Fi with no remote reported the timeout as an instruction. Then the HDMI
switch capture and replay described at the top, which is the first real appliance behaviour for one command. The assumed
38 kHz carrier was enough for that switch, and other appliances and carriers are still untested.

**2026-09-11, desktop PC, endpoint on COM5.** Hisense's published discrete NEC codes (address 4, command 0x71 POWER ON
and 0x72 POWER OFF, from Hisense's discrete IR command table) turned the TV on and off, sent as raw timings through
firmware 0.2.0. The same session captured the HDMI switch remote's input 1, input 2 and power buttons (NEC address 128,
commands 1, 2 and 3).

PlatformIO's bundled esptool 4.5.1 then flashed firmware 0.3.0 over COM5 without stalling. The endpoint kept its
pairing, rejoined Wi-Fi, parsed an unchunked full-length USB payload, and refused malformed codes, unknown protocols and
invalid A/C states without emitting. It sent the Hisense POWER ON through `sendCode` (NEC `20DF8E71`), and `sendAc` with
protocol `MIDEA` (Cool, 24 °C, fan auto) turned on a Koenic KAC 12020 portable air conditioner.

Its power off only registered once the endpoint was raised above the unit's opening air flap, which blocks line of sight
while it runs. Cool, Auto, Fan and Dry modes, set points of 20 and 24 °C, all three fan speeds and the swing toggle
worked too. Swing is a toggle in this protocol, so every send that requests it flips the louver, and frames the flap
blocked needed a second send. Heat, sleep and the other toggles are untested.

**Firmware 0.4.0** was flashed the same way. Once web credentials were set over USB, the endpoint answered 401 with no
password and with a wrong one, served the index and the Hisense page, redirected a remote address missing its trailing
slash, and refused a press without `X-WSGM-IR`, unknown remotes, buttons and sequences, and climate requests outside the
declared range or modes, all without emitting. A remote with an unknown protocol failed the build. Core logging is
compiled out, because NVS and web server logs over USB ran into the next protocol reply.

## Odds and ends

The selected-command widget can be pinned from the plugin page. It shows the selected command and uses the existing
explicit send action; pinning or displaying it never sends infrared.

See [protocol.md](protocol.md) for the shared wire contract.

## Licence

The main repository's GPL licensing applies to this plugin and its authored firmware. The common SDK keeps its MIT
boundary. PlatformIO downloads the separately licensed IRremoteESP8266, ArduinoJson and Adafruit NeoPixel dependencies,
and Wi-Fi, mDNS and NVS come from the Arduino ESP32 core.
