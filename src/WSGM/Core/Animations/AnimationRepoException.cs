using System;

namespace WSGM.Core;

/// <summary>The repository could not be read, or refused.</summary>
/// <param name="message">Repository failure text suitable for the UI.</param>
public sealed class AnimationRepoException(string message) : Exception(message);
