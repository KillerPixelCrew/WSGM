using System;

namespace WSGM.Core;

/// <summary>A theme folder that CSS Loader would refuse, with its reason.</summary>
/// <param name="message">The reason, in CSS Loader's words where it has them.</param>
public sealed class ThemeManifestException(string message) : Exception(message);
