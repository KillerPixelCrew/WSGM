namespace WSGM.Plugin.Ir;

/// <summary>A validated refusal that establishes that this operation emitted no IR.</summary>
/// <param name="message">
///     Reason the request was refused before emitting infrared, suitable for user feedback without
///     secrets.
/// </param>
internal sealed class IrRejectedException(string message) : Exception(message);
