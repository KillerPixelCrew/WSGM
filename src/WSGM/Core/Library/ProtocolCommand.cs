namespace WSGM.Core;

/// <summary>A program and the arguments that open one URI.</summary>
/// <param name="Program">The registered program, unquoted and fully qualified.</param>
/// <param name="Arguments">Its arguments with the URI substituted.</param>
public sealed record ProtocolCommand(string Program, string Arguments);
