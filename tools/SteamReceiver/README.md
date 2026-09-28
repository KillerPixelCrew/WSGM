# SteamReceiver

The other end of the bench. `tools/DeckSpike` sends frames into Steam as a virtual Steam Deck;
SteamReceiver is a program you add to Steam and launch from it, so it sits where a game sits and
shows what Steam Input delivers: the virtual XInput pad Steam creates for the shortcut's layout,
with trigger values on the 0 to 255 scale a game reads, every button edge, and any keys the layout
injects. Together the two show, for one frame on the wire, what the game gets.

It is a window of its own, not a console. Steam Input applies a shortcut's layout to the process
that owns the foreground window, and a console program's window belongs to Windows Terminal or
conhost, so a console receiver only ever saw the desktop layout. Everything it shows is also
appended to `steam-receiver.log` beside the executable, and "Copy all" puts the window's text on the
clipboard.

It is outside WSGM.slnx and references nothing of WSGM. It is framework-dependent, so the .NET 10
desktop runtime on the machine is what runs it.

## Build

```powershell
dotnet build tools\SteamReceiver -c Release
```

The executable is `tools\SteamReceiver\bin\Release\net10.0-windows\win-x64\SteamReceiver.exe`.

## Add it to Steam

1. In Steam, Games, "Add a Non-Steam Game to My Library", browse to `SteamReceiver.exe`.
2. Launch it from Steam, not from Explorer. The first lines show `SteamAppId` and `SteamGameId`;
   when they are set, Steam Input owns the process and the pad it shows is Steam's virtual
   controller, not the raw Deck.
3. Open the shortcut's controller layout. For the trigger question, set the left trigger's soft pull
   action to button A and its full pull action to button B; then a soft pull prints `A DOWN` and a
   full pull prints `B DOWN`. Leaving the trigger as analogue output shows how Steam scales the
   Deck's 32767 onto the 0 to 255 a game reads.

## What it shows

- `slotN connected` and `disconnected` as Steam's virtual pad appears.
- One line per change of trigger value, button state or a stick moving by more than 2000: the time
  since start, the slot, both triggers with the highest value seen so far, the buttons held, and the
  sticks when they moved.
- A `DOWN` or `up` line for every button edge.
- A `key` line for every key the window receives while it has focus, which is how an action mapped
  to a keyboard key shows up.

Esc closes it.

## The experiment

With DeckSpike attached and this receiver in front, running through Steam:

1. `z` in DeckSpike: the trigger ramps to 100 percent with the digital bit rising past 80 percent.
   The receiver shows when `A` (soft pull) and `B` (full pull) fire, and the LT value Steam derives.
2. `b` twice for "forced on", then `e`: the bit is set from rest. Whether `B` fires at once is what
   2.0.0 did.
3. `b` again for "forced off", then `e`: no bit at all, which is what 2.0.1 did.
4. `m` to change the full-travel wire value: the receiver's `LT max` says how Steam scales it.
