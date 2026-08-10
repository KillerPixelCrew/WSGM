# Overlay

Overlay owns the quick-access panel, game-mode taskbar, radio/audio/eject surfaces, and their shared
Steam Input lease and focus handover.

- `OverlayController` is the lifetime owner for both focused surfaces. Acquire the Steam Input lease
  before opening a surface and release it only after the last surface closes.
- Preserve the 150 ms deferred close and touch-synthesized mouse filtering; removing either causes
  ghost clicks on controls behind the overlay.
- Peer keyboard focus is part of the active sub-view: include its bounds in tap hit-testing, keep
  only one navigation active, and invalidate asynchronous picker loads when navigation changes.
- Dismissal may restore focus only under the existing game-mode and suppression gates. Next-app
  cycling deliberately suppresses restoration.
- During the taskbar/overlay handover, do not route LB/RB to both navigations and do not rebuild
  switcher/tray collections wholesale while a gamepad focus target exists.
- Keep visual styling in `Themes\` tokens and shared controls; consumer XAML must not add literal
  colours or a second focus-adornment mechanism.
