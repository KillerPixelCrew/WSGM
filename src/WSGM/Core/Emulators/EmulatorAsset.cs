namespace WSGM.Core;

internal sealed record EmulatorAsset(string Name, string Url, string Sha256 = "", long Size = 0, string Revision = "");
