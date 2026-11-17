using WSGM.Core;

namespace WSGM.Shell;

/// <summary>The system folders a ROM library's folder holds, as the Steam page receives them.</summary>
/// <param name="Systems">Each recognised system folder.</param>
internal sealed record RomSystemsAnswer(RomSystemFolder[] Systems);
