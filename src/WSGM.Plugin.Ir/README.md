# WSGM IR plugin

This independent `wsgm.infrared` package owns its command library, endpoint protocol and XIAO IR
Mate firmware. It uses the common Plugin SDK and has no Device SDK dependency. The Device plugin can
remain active alongside it. Hardware acceptance for #52 passed on the reference XIAO on 2026-09-11.

Implemented: endpoint identity/version checks over USB serial or the local network, bounded raw
learn/send, cancellation, command and scene storage, backup/restore, named actions, USB-only Wi-Fi
pairing with a per-endpoint token, common-host lifecycle and Tools management forms. The real
package has passed collectible host loading alongside a Device-category fixture and has paired,
identified and refused unpaired clients on a live network. On 2026-09-11 it learned a real HDMI
switch remote button over Wi-Fi as a 71-timing NEC frame (address 128, command 1) and replayed it
twice; the maintainer confirmed the switch changed to input 1 each time. A COM port list is
discovery information only; only a successful protocol identity reply establishes compatibility.

## Build and package

```powershell
dotnet test tests/WSGM.Plugin.Ir.Tests/WSGM.Plugin.Ir.Tests.csproj
dotnet publish src/WSGM.Plugin.Ir/WSGM.Plugin.Ir.csproj -c Release -r win-x64 -o publish/plugins/wsgm.ir
python -m venv .codex/ir-tools
.codex/ir-tools/Scripts/python.exe -m pip install platformio==6.1.18
.codex/ir-tools/Scripts/python.exe -m platformio run -d src/WSGM.Plugin.Ir/Firmware
```

The package contains `plugin.wsgm.json`, its entry assembly and package dependencies. Install and
enable it through the existing common plugin package workflow. Plugin preferences hold the USB
serial port, the endpoint connection (`usb` or `wifi`) and an optional Wi-Fi host name or IP that
overrides the paired one. Connect in Overlay Tools verifies the endpoint identity explicitly; learn,
send and scenes identify the endpoint themselves whenever no verified connection exists, so route
automation works after a restart, a resume or a dropped link without a manual Connect.
Identification is a read-only exchange. Loading, changing modes, connecting and pairing never emit
IR.

## Wi-Fi pairing

Pairing always runs over the USB cable, whatever connection is selected: holding the cable is the
proof of possession. Pair Wi-Fi over USB takes the network name and password, mints a random
48-character token if the plugin has none, and stores credentials and token on the endpoint. The
plugin keeps only the token, the endpoint's mDNS host name (`wsgm-ir-<last three MAC octets>.local`)
and its last address in `endpoint.json`, apart from the command library so backups never carry the
secret. The password is never written to plugin state or published. Pairing waits up to fifteen
seconds for the endpoint to join and reports Unconfirmed, not failure, if it has not yet; Connect
shows the join state afterwards. Forget Wi-Fi over USB clears both sides.

With the `wifi` connection the plugin opens plain TCP to port 7521 on the paired host name, or on
the preference override, which may carry an explicit `host:port`. Every request except identify
carries the token; the endpoint answers `unauthorized` otherwise, so other LAN clients cannot blast
commands, though the token travels unencrypted on the local network. The endpoint serves one host at
a time and the newest connection replaces an older one, so a reconnecting WSGM never waits on a dead
socket. Firmware 0.1.0 has no network support; Wi-Fi setup against it reports that explicitly.

## Library and actions

Tools action forms use the existing controller/touch keyboard. Learn takes a device and command
name; Select accepts the displayed `Device / command` name. The selection survives a plugin restart.
Rename and Relearn keep command identity, so existing scenes keep referring to that command. Relearn
also preserves any explicit carrier override. Repeat timing controls the selected command.

Create or replace scene takes a scene name and a semicolon-separated sequence of displayed command
names, plus a delay after each command. Run/delete accept a scene name. Core callers may use stable
IDs instead. A command referenced by a scene cannot be deleted until those references are removed.
Action editors keep drafts separate from published state; only the explicit action button
dispatches.

The host stores `library.json` in its assigned private state directory. Learned signals are complete
payloads, not firmware slot numbers. `library.backup.json` is an explicit backup in the same
directory; copy it elsewhere for protection against disk loss. Invalid replacement libraries never
overwrite the current file. Import requires that backup to exist. Current command and scene limits
are host validation bounds, not firmware slots.

Core automation can call `send` with a `command` ID or `scene` with a `scene` ID. Transmission
returns `Dispatched`: an endpoint acknowledgement proves emission, not that a TV or HDMI switch
changed state. Uncertain operations are not retried; a failed exchange drops the connection and the
next operation identifies the endpoint again before doing anything else. Endpoint refusals such as a
learn timeout or an overflowing capture are shown as plain instructions in Tools. Plugin suspend
closes the connection. Device replacement does not erase the host library.

## Reference hardware and recovery

The [Seeed wiki](https://wiki.seeedstudio.com/XIAO_IR_Mate_Smart_IR_Remote/) links the
[hardware archive](https://files.seeedstudio.com/wiki/XIAO_IR_MATE/XIAO_IR_REMOTE_hardware.zip) and
[ESPHome source](https://github.com/Seeed-Studio/xiao-esphome-projects/tree/main/projects/xiao_smart_ir_mate).
These were inspected on 2026-09-09 and retained locally under `_ref/XiaoIrMate` as reference
evidence. The supplied software specifies transmitter GPIO3, active-low receiver GPIO4, touch GPIO5
with pull-down, motor GPIO6 and one WS2812 GRB LED on GPIO7. The firmware uses the IR library's
standard active-low demodulating receiver handling and a NeoPixel driver, not a plain GPIO LED.

Firmware 0.2.0 implements protocol 1 over USB CDC at 115200 and, once paired, over TCP port 7521
with mDNS advertisement as `_wsgm-ir._tcp`. Credentials and the token live in the ESP32 NVS `wsgmir`
namespace; there is no persistent command database, cloud dependency or web interface. The RGB LED
and motor provide brief feedback; touch gives feedback without transmitting a user command. The
current envelope-capture implementation does not measure carrier frequency. Payloads distinguish
`assumed`, `protocol`, `measured` and `manual` provenance; this firmware returns an explicitly
assumed 38 kHz. Actual carrier-measurement capability of the receiver hardware remains unverified. A
separate per-command override preserves the original captured value and provenance. The initial
Tools UI includes the last learned command's carrier status, override slider and reset action.

Do not infer measurement from a Pronto frequency field: ESPHome's
[Pronto decoder](https://github.com/esphome/esphome/blob/dev/esphome/components/remote_base/pronto_protocol.cpp)
currently assigns 38000 during decode. That is evidence about the software path, not proof of the
exact receiver's hardware limitations.

Before replacing firmware, identify the chip and save its entire flash with esptool. Keep that
backup private because factory configuration may include network credentials. Only flash the
confirmed XIAO ESP32-C3 endpoint. For an attended upload:

```powershell
.codex/ir-tools/Scripts/python.exe -m platformio run -d src/WSGM.Plugin.Ir/Firmware -t upload --upload-port COM3
```

COM3 was this workstation's observed port, not a portable identity. Use the actual enumerated port.
On this workstation, bundled esptool 4.5.1's stub stalled during flash reads. esptool 5.1.0 with
`--no-stub` read the full 4 MiB and uploaded all four PlatformIO images successfully. The private
factory backup is `.codex/issue-52/factory-flash.bin`, SHA-256
`0e7b02cb0e63d6e4fa4642d9f9ef512b68b89ff144f6d371e00c4be432de391f`. Firmware 0.2.0 was flashed the
same way on 2026-09-11 and the running C# plugin identified it as firmware 0.2.0/protocol 1 with
host name `wsgm-ir-15ef50`. Live USB checks passed protocol mismatch rejection, malformed-frame
recovery, invalid-send refusal, invalid Wi-Fi argument refusal, learn cancellation, learn timeout
status and idle health readback. Later that day the plugin paired the endpoint over USB; it joined
the maintainer's network, resolved as `wsgm-ir-15ef50.local`, and the plugin verified its identity
over Wi-Fi through both the paired name and an explicit `host:port`. From an unpaired LAN client,
identify answered while health, send, learn and cancel returned `unauthorized` and `wifi` returned
`usb-only`; a learn over Wi-Fi with no remote reported the timeout as an instruction. The HDMI
switch capture and replay described above then established real appliance behavior for one command;
the assumed 38 kHz carrier was sufficient for that switch. Other appliances and carriers remain
unverified. The ESP32-C3 ROM loader remains the recovery path; Seeed also links a factory firmware
flasher from the wiki. Reflashing does not touch the host command library or pairing file, but it
does not clear NVS either: Forget Wi-Fi over USB, or an esptool `erase-flash`, removes stored
credentials. Hardware acceptance must distinguish a firmware build from successful capture and
verified appliance behavior.

See [protocol.md](protocol.md) for the shared wire contract. Main repository GPL licensing applies
to this plugin and its authored firmware. The common SDK retains its MIT boundary. PlatformIO
downloads the separately licensed IRremoteESP8266, ArduinoJson and Adafruit NeoPixel dependencies;
Wi-Fi, mDNS and NVS come from the Arduino ESP32 core.

The selected-command widget can be pinned from the plugin page. It shows the selected command and
uses the existing explicit send action; pinning or displaying it never sends infrared.
