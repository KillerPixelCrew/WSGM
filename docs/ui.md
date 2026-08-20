# UI layer and the splash engine

Device-verified behaviour and the reasoning behind it. These are findings, not style: where a
section says device-verified or live-verified, it encodes something that only revealed itself on
real hardware or against a live Steam client, and changing it without re-verifying is a regression
waiting to happen.

**UI layer (rebuilt in 0.9.0 — read before touching any XAML).** All styling lives in `Themes\`
(`Palette.axaml` = the token set, `Typography.axaml`, `Shared.axaml`, plus `ControlThemes`/
`TabStripTheme`/`CardButtonTheme`); `App.axaml` is only includes. Rules: every colour comes from a
token — no hex literals in consumer XAML; the accent family (`HcAccentBrush`, `HcOnAccentBrush`,
`HcOnAccentCaptionBrush`) is consumed via **DynamicResource** because `Themes\AccentPalette.cs`
replaces it at runtime, everything else via StaticResource. One focus mechanism only:
`FocusAdorner={x:Null}` + a constant 2 px border that flips to the accent on `:focus` (Avalonia's
adorner is destroyed/rebuilt on every focus move and lost on activation blips). Shared controls in
`Controls\`: `TabStrip` (the LB/RB bumper tab bar used by BOTH the quick-access panel and Settings),
`CardButton`, `Icons` (stroke-style `StreamGeometry`; render them stroked with `Fill={x:Null}` —
filling collapses interior detail). `Core\RelayCommand.cs` is the hand-rolled AOT-safe ICommand.
Settings is `Settings\SettingsWindow` + six always-alive `Settings\Pages\*` UserControls toggled by
`IsVisible` (scroll positions survive switching), with recorder lifetime in
`Settings\ShortcutRecorders.cs`. **Layout floor: 1280x800 (Steam Deck), Settings min 1024x640** — a
page must fit without scrolling or it earns another tab. _Gotcha:_ Avalonia's `Shape` scales a
`Stretch=Uniform` geometry and then aligns it **top-left** inside the element box
(`CalculateSizeAndTransform` translates only by the geometry origin), so a square box around a
wide-and-short glyph parks it against the top. Give such a Path only its dominant dimension and let
the box hug the drawn content.

**Splash engine** (`Core\AppConfig.SplashConfig`, `Shell\SplashStyle/SplashPresets`,
`Core\SplashAssets/SplashTheme/ImageHeader`, `Shell\BootSplashWindow`): the splash is a pure
customization engine — text/caption with own colours+sizes, 12 spinner styles, background
colour/image/vignette, logo, per-element placement (anchor+padding, absolute X/Y, or attached to the
text block). Presets only prefill editable fields; **never key rendering off a preset**.
`.wsgmsplash` theme files are SHARED and therefore UNTRUSTED — the whole defense set must stay
intact: entry names must equal their own file name (a drive-relative `D:logo.png` is rooted, and
`Path.Combine` then discards the staging dir) plus a containment assert; per-entry and total
decompression caps enforced through a counted copy (central-directory sizes lie); image paths from
the JSON are ALWAYS replaced by what was actually extracted (a UNC path there makes Settings touch a
remote host when it thumbnails); `ImageHeader` gates declared pixel dimensions before any decode and
both logo and background decode under an output-area budget (byte caps bound only encoded size);
text/colour strings length-capped and every numeric field clamped in `ConfigStore.NormalizeSplash`,
the choke point BOTH config load and theme import pass through. Imports stage into a temp directory
owned (marker held `FileShare.None`) for the life of the Settings window, so a second window cannot
delete a first window's unsaved import. `SplashAssets` is a **two-phase transaction**: sidecars are
promoted only after `ConfigStore.Save` succeeds, a failed promotion reports a failed save and keeps
the previous path, and the picked path stays in the view model so a retry works.
