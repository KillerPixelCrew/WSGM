using System;

namespace WSGM.Core;

/// <summary>A refusal or failure whose message is meant for the user.</summary>
internal sealed class GameToolException(string message, Exception? inner = null) : Exception(message, inner);
