# Glyph credits

Controller button glyphs from Kenney's "Input Prompts" pack (version 1.5),
https://kenney.nl/assets/input-prompts — licensed CC0 (public domain).
Thank you Kenney!

Files are named by the button's LABEL in each style:

| File | Xbox art | PlayStation art | Nintendo art |
|---|---|---|---|
| `a.svg` | A (south) | Cross (south) | A (east) |
| `b.svg` | B (east) | Circle (east) | B (south) |
| `x.svg` | X (west) | Square (west) | X (north) |
| `y.svg` | Y (north) | Triangle (north) | Y (west) |

The confirm action always shows `a.svg` and back always shows `b.svg`; when the
Nintendo style is selected, the *input mapping* swaps (XInput B confirms, XInput A
goes back) so that the button physically labeled A confirms — matching Nintendo
conventions. See `Input\GamepadNavigation.cs`.
