namespace WSGM.Core;

/// <summary>A runtime and the one sentence of evidence that decided it.</summary>
/// <param name="Runtime">The classification.</param>
/// <param name="Evidence">Why, in words the import preview shows the user.</param>
public sealed record XboxRuntimeClassification(XboxRuntime Runtime, string Evidence);
