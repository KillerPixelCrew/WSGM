# UI publication fixtures

`claw-ui-publication.json` supplies a simulated Claw publication for headless overlay captures. On
2026-09-20 its layout hints and choice display labels were migrated from the corresponding
production descriptors in `ClawPlugin`. Capability identities, generations and stored observation
values were retained. These readings are fixture data, not a fresh hardware validation result.

On 2026-09-29 the Claw's `display.variable-refresh` descriptor and state were removed by hand,
because variable refresh moved to the Intel graphics package. `ClawPluginTests` still writes the
full publication (`StartAsync_FakeHardware_PublishesDirectCapabilityAndOemSurfaces`), and a
regenerated file would no longer carry the row either.

`intel-graphics-ui-publication.json` supplies the Intel graphics package's publication for the
Device > GPU section: a Graphics adapter section (Frame delivery, Image quality, Driver, Live
status) and a Built-in display section (Refresh, Picture, Colour, Power savings). Ids, labels,
ranges, profile scopes and apply timings follow the descriptors in
`external/libgpu-driver-interact`; the values are fixture data, not a hardware reading. Arc Sync
uses the Custom profile with variable refresh on, so its four range rows are writable.
`FixtureGraphicsSource` adds the running game's Low latency override and projects the rows through
the production Graphics bridge. The Claw captures take their Power and thermals variable refresh row
from this fixture, as the device bridge does with a running graphics package.
