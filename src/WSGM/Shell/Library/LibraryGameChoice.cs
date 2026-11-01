namespace WSGM.Shell;

/// <summary>A library game the whitelist and blacklist filters can pick.</summary>
/// <param name="AppId">The unsigned app id, the form the filters store.</param>
/// <param name="Name">Steam's title.</param>
internal sealed record LibraryGameChoice(long AppId, string Name);
