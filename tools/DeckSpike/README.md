# DeckSpike

A spike harness for the virtual Steam Deck controller. It presents a Steam Deck controller to this
machine's Steam through VIIPER, the same library and the same 64-byte Neptune frame WSGM sends, and
lets you drive the triggers, the digital trigger bits and the analogue scale from the keyboard while
every frame Steam writes back to the pad prints on the console. No handheld, no device plugin and no
WSGM session are involved.

It is outside WSGM.slnx on purpose, like `tools/OverlayMockup`: it is a bench, not a product.

## What it needs

- **usbip-win2**, the same pinned driver the WSGM setup installs. Without it VIIPER has nothing to
  attach to. The installer is `USBip-0.9.8.1-x64.exe`, staged under `publish\Payload\Controller`
  after a `build.ps1`, and `src\WSGM.Setup\Install-UsbipDriver.ps1` is how setup runs it. Installing
  it restarts every USB 3.0 hub once and asks for a reboot.
- **libviiper.dll** beside `DeckSpike.exe`. It comes from `src\WSGM\Native\Viiper`, which
  `eng\build-viiper.ps1` stages, and the project reference copies it into the output folder.
- **An elevated console**, because the attach goes through the USB/IP client driver.
- **Steam running** on this machine. It sees a Steam Deck controller (vendor 28DE, product 1205) and
  treats it like one: the controller settings, the Steam Input tester and any game's layout apply.

## Run

```powershell
dotnet build tools\DeckSpike -c Release
.\tools\DeckSpike\bin\Release\net10.0-windows10.0.19041.0\win-x64\DeckSpike.exe
```

Optional arguments set the starting experiment: `--scale=N` (the wire value for full travel, 32767
is what WSGM sends), `--threshold=PERCENT` (raise the digital bit at that travel instead of WSGM's
80 percent), `--bit=on` or `--bit=off` (hold both bits).

## Keys

| Key             | Does                                                                                                                                                                                                                                                                  |
| --------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `q` / `a`       | left trigger +5 / -5 percent (with Shift, 1 percent)                                                                                                                                                                                                                  |
| `w` / `s`       | right trigger +5 / -5 percent                                                                                                                                                                                                                                         |
| `e` / `d`       | left / right trigger to 100 percent                                                                                                                                                                                                                                   |
| `r`             | release both triggers                                                                                                                                                                                                                                                 |
| `z` / `x`       | ramp left / right from 0 to 100 percent over two seconds and hold; Shift releases again                                                                                                                                                                               |
| `b`             | cycle the digital bit: WSGM's rule, your threshold, forced on, forced off                                                                                                                                                                                             |
| `t`             | set the digital threshold in percent                                                                                                                                                                                                                                  |
| `m`             | cycle the full-travel wire value 32767, 35424, 40000, 65535 (Shift: type one)                                                                                                                                                                                         |
| Enter / Space   | tap A / hold and release A                                                                                                                                                                                                                                            |
| `f` / `p` / `h` | print the frame bytes / the state / this table                                                                                                                                                                                                                        |
| Esc             | remove the device and quit                                                                                                                                                                                                                                            |
| `1` to `5`      | scripted runs after a five-second countdown: 1 ramps LT under the current rule; 2 forces both bits on at rest (2.0.0); 3 pulls LT to 100 percent with no bit (2.0.1); 4 pulls LT to 100 percent under WSGM's rule (2.0.2); 5 pulls LT to 100 percent written as 35424 |

Every change prints one state line: both triggers as travel percent, the raw wire value, the L2 and
R2 bits, the bit rule and the scale in force. Frames Steam writes to the pad print as `<-` lines
once per distinct frame, with settings writes decoded by name, including `TriggerThresholdPercent`
(0x44) and `ImuMode` (0x30).

## Focus, and why the runs are scripted

Steam drives a shortcut's layout only while that shortcut is the foreground window. Every key
pressed in this console takes the focus away, so use the numbered runs: press the number here,
switch to the receiver, and the run starts after five seconds while the receiver is in front. Both
tools stamp every line with the local wall-clock time to the millisecond, so the two logs line up.

## The experiment it was built for

Steam's Full Pull on the Deck target. On an Xbox Ally X, 2.0.0 raised the digital bit with the first
movement and Full Pull fired at once; 2.0.1 left the bit clear and Full Pull never fired; 2.0.2
raises it past 80 percent, the rule HHD and InputPlumber use, and the tester still saw no Full Pull.
Steam's soft pull slider on that machine accepted the trigger up to 30309 of 32767, which is 92.5
percent of what WSGM sends for full travel. With this harness the questions are answered on one
desk: watch the `<-` lines for a `TriggerThresholdPercent` write when Steam configures the pad, pull
with `z` under each bit rule, and try the other full-travel values with `m` while the Steam Input
tester shows what Steam makes of them.

Answered on 2026-09-28 with `tools\SteamReceiver` in front: Full Pull is the digital bit and only
the bit, Soft Pull is the analogue value, and the 80 percent rule fires Full Pull at XInput 207
of 255. Every full-travel value above 32767 reads as a negative signed short and Steam drops the
frame's trigger entirely, so the `m` cycle beyond 32767 demonstrates only that.

## What it does not do

It writes nothing to `%LOCALAPPDATA%\WSGM`, touches no HidHide entry and reads no physical pad. It
is a virtual device only; unplugging it is Esc.
