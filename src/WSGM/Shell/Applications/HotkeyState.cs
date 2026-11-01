using WSGM.Core;

namespace WSGM.Shell;

/// <summary>A hotkey as the Overlay shows it: its definition and why its last press sent nothing.</summary>
/// <param name="Definition">A detached copy of the stored definition.</param>
/// <param name="Notice">The last press's refusal, or empty when it went through or was never pressed.</param>
/// <param name="Summary">The refusal in a few words for the Quick access tile, or empty with the notice.</param>
internal sealed record HotkeyState(HotkeyDefinition Definition, string Notice, string Summary = "");
