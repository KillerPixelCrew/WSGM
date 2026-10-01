namespace WSGM.Plugin.Ir;

/// <summary>A validated refusal that establishes that this operation emitted no IR.</summary>
internal sealed class IrRejectedException(string message) : Exception(message);
