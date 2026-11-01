namespace WSGM.Core;

/// <summary>One unselected package retained on disk, with its reason.</summary>
internal sealed record PluginPackageNotice(string PackagePath, string Id, string Reason);
