# Overlay

Overlay owns the quick-access panel, game-mode taskbar, radio/audio/eject surfaces, and their shared
Steam Input lease and focus handover. The Power tab exposes explicit Standby and Hibernate actions;
both dismiss the overlay before asking Windows to suspend.

- `OverlayController` is the lifetime owner for both focused surfaces. Acquire the Steam Input lease
  before opening a surface and release it only after the last surface closes.
- Settings handoff transfers named ownership: Settings registers its claim before the deferred
  overlay close removes the overlay claim. Never abandon the old owner in the blocker's owner set,
  and acknowledge the close so Settings can end its temporary deactivation exemption.
- Preserve the 150 ms deferred close and touch-synthesized mouse filtering; removing either causes
  ghost clicks on controls behind the overlay.
- Raw-touch left/top gestures always emit Steam's Ctrl+1/Ctrl+2 Big Picture shortcuts, including
  while a game is foreground; bringing Steam's menu over the game is their purpose.
- Peer keyboard focus is part of the active sub-view: include its bounds in tap hit-testing, keep
  only one navigation active, and invalidate asynchronous picker loads when navigation changes.
- Artwork operations snapshot both the target app and navigation generation across awaits; bound
  thumbnail concurrency, decode thumbnails scaled, and dispose replaced bitmap trees immediately.
- Dismissal may restore focus only under the existing game-mode and suppression gates. Next-app
  cycling deliberately suppresses restoration.
- During the taskbar/overlay handover, do not route LB/RB to both navigations and do not rebuild
  switcher/tray collections wholesale while a gamepad focus target exists.
- Keep visual styling in `Themes\` tokens and shared controls; consumer XAML must not add literal
  colours or a second focus-adornment mechanism.
