namespace WSGM.Shell;

/// <summary>The answer to a launch change that needs the user to accept a risk first.</summary>
/// <param name="Acknowledge">Always true: ask, then send the mode with the acknowledgement.</param>
internal sealed record GameLibraryAcknowledgeAnswer(bool Acknowledge);
