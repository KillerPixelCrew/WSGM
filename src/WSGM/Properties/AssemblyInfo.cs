using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("WSGM.Tests")]
[assembly: InternalsVisibleTo("WSGM.UiTests")]
// The virtual Steam Deck spike harness (tools/DeckSpike) drives the Deck encoder and the VIIPER
// bindings directly, so what it puts on the wire is what WSGM puts there.
[assembly: InternalsVisibleTo("DeckSpike")]
