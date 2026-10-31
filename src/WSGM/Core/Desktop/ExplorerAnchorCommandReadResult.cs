namespace WSGM.Core;

/// <summary>One completed anchor command read or the verified action reached after disconnect.</summary>
/// <param name="Command">Received line, or null after disconnection.</param>
/// <param name="DisconnectAction">Verified post-disconnect action, or null when a command was received.</param>
internal readonly record struct ExplorerAnchorCommandReadResult(
    string? Command,
    ExplorerAnchorDisconnectAction? DisconnectAction);
