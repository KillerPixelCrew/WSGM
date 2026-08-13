In no particular Order:

Now that we have CEF, fixing stuff that annoyed me for years in Steam:

- ~~Steam Download Priority Sorting by Name, Size and Type (Install/Update) preferibly over a Button
  added to the Download Page itself.~~ Done — `Core/SteamDownloadSort.cs`.
- ~~We have the Wakelock that disables the system going standby when stuff downloads but the screen
  can turn off. Can we get an event when the Screen Turns off and back on again? To mute Audio?
  Because Steam Makes Sounds when downloads finish.~~ Done — `Shell/DisplayOffMuteService.cs`.
  **Device verification still open:** whether the display-off notification actually fires when the
  Claw's screen times out under Modern Standby. The `Display state:` log lines are the test surface.
