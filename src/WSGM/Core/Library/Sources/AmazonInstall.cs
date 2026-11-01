namespace WSGM.Core;

/// <summary>One row of the Amazon Games app's install table.</summary>
/// <param name="Id">The game's Amazon id.</param>
/// <param name="ProductTitle">Its title.</param>
/// <param name="InstallDirectory">Where it is installed.</param>
public sealed record AmazonInstall(string Id, string ProductTitle, string InstallDirectory);
