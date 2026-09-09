# WSGM IR plugin

This independent `wsgm.infrared` package owns its command library, USB protocol and XIAO IR Mate
firmware. It uses the common Plugin SDK and has no Device SDK dependency. The Device plugin can
remain active alongside it. This is the initial #52 implementation, not a completed management UX.

Implemented: endpoint identity/version checks, bounded raw learn/send, cancellation, command and
scene storage, backup/restore, named actions, common-host lifecycle and basic Tools contributions.
Still required: endpoint pairing UI, command selection/naming/editing/relearning UI, scene editing,
full package-host coexistence verification and live remote capture/transmission. A COM port list is
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
enable it through the existing common plugin package workflow. Set the USB port in plugin preferences,
then use Connect in Overlay Tools. Loading or changing modes does not emit IR automatically.

The host stores `library.json` in its assigned private state directory. Learned signals are complete
payloads, not firmware slot numbers. `library.backup.json` is an explicit backup in the same directory;
copy it elsewhere for protection against disk loss. Invalid replacement libraries never overwrite
the current file. Import requires that backup to exist. Current command and scene limits are host
validation bounds, not firmware slots.

Core automation can call `send` with a `command` ID or `scene` with a `scene` ID. Transmission returns
`Dispatched`: an endpoint acknowledgement proves emission, not that a TV or HDMI switch changed state.
Uncertain operations are not retried. Plugin suspend closes the serial connection; explicit Connect
revalidates it after resume. Device replacement does not erase the host library.

## Reference hardware and recovery

The [Seeed wiki](https://wiki.seeedstudio.com/XIAO_IR_Mate_Smart_IR_Remote/) links the
[hardware archive](https://files.seeedstudio.com/wiki/XIAO_IR_MATE/XIAO_IR_REMOTE_hardware.zip) and
[ESPHome source](https://github.com/Seeed-Studio/xiao-esphome-projects/tree/main/projects/xiao_smart_ir_mate).
These were inspected on 2026-09-09 and retained locally under `_ref/XiaoIrMate` as reference evidence.
The supplied software specifies transmitter GPIO3, active-low receiver GPIO4, touch GPIO5 with
pull-down, motor GPIO6 and one WS2812 GRB LED on GPIO7. The firmware uses the IR library's standard
active-low demodulating receiver handling and a NeoPixel driver, not a plain GPIO LED.

Firmware 0.1.0 implements protocol 1 over USB CDC at 115200. It has no Wi-Fi service or persistent
command database. The RGB LED and motor provide brief feedback; touch gives feedback without
transmitting a user command. The current envelope-capture implementation does not measure carrier
frequency. Payloads distinguish `assumed`, `protocol`, `measured` and `manual` provenance; this firmware
returns an explicitly assumed 38 kHz. Actual carrier-measurement capability of the receiver hardware
remains unverified. A separate per-command override preserves the original captured value and provenance.
The initial Tools UI includes the last learned command's carrier status, override slider and reset action.

Do not infer measurement from a Pronto frequency field: ESPHome's
[Pronto decoder](https://github.com/esphome/esphome/blob/dev/esphome/components/remote_base/pronto_protocol.cpp)
currently assigns 38000 during decode. That is evidence about the software path, not proof of the
exact receiver's hardware limitations.

Before replacing firmware, identify the chip and save its entire flash with esptool. Keep that backup
private because factory configuration may include network credentials. Only flash the confirmed
XIAO ESP32-C3 endpoint. For an attended upload:

```powershell
.codex/ir-tools/Scripts/python.exe -m platformio run -d src/WSGM.Plugin.Ir/Firmware -t upload --upload-port COM3
```

COM3 was this workstation's observed port, not a portable identity. Use the actual enumerated port.
The ESP32-C3 ROM loader remains the recovery path; Seeed also links a factory firmware flasher from
the wiki. Reflashing does not touch the host command library. Hardware acceptance must distinguish
a firmware build from successful capture and verified appliance behavior.

See [protocol.md](protocol.md) for the shared wire contract. Main repository GPL licensing applies
to this plugin and its authored firmware. The common SDK retains its MIT boundary. PlatformIO
downloads the separately licensed IRremoteESP8266, ArduinoJson and Adafruit NeoPixel dependencies.
