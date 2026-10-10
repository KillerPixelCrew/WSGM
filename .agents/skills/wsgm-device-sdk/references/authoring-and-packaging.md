# Handheld source contributions

New handhelds are source contributions to `external/libhandheld`, with one exact model row and the
smallest source-grounded transport. There is no device-plugin manifest, package loader, packing CLI
or separate SDK project. Device Lab `scaffold` emits identity, decoder research, observed fixtures
and provenance; it does not generate an installable DLL.

Read the current library public API and nearest family guidance. HC 1.3.1.6 under
`_ref/HandheldCompanion` is the primary Windows reference; HHD is the cross-check for uncovered
behaviour. Record exact reference paths, selectors, packet bytes, limits, readback capability,
firmware binding and hardware evidence per model. Do not infer support from resemblance.

Model metadata must contain actual declared roles, controller availability, hardware-access
requirements and hardware-verification state. Detection is pure and uses the caller's identity.
Never add guessed EC writes, implicit mode changes or generic CPU fallbacks. Keep native handles
inside the selected engine's explicit acquisition/stop lifetime.

Ship family LICENSE, PROVENANCE and THIRD_PARTY_NOTICES as files alongside the library as well as
embedded glyph notices. Keep PromptFont OFL and victor-borges MIT notices with their glyphs. Signed
PawnIO modules and helper binaries need exact source/version/digest pins and their notices; never
disable PawnIO signature verification.

Cover exact detection, protocol bytes, pre-dispatch cancellation, partial writes, lifecycle cleanup,
held input release and firmware-bound recovery with hardware-free fixtures. Tests follow the parent
manual-first policy. Builds and fixtures do not prove controller re-enumeration, fan, motion,
lighting, rumble or power behaviour. An attended Device Lab report and provenance update establish
that evidence. Release, deploy and live hardware actions need the maintainer's requested scope.
