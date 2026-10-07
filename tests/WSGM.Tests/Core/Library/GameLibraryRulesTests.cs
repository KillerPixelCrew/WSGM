using WSGM.Core;
using WSGM.Testing;

namespace WSGM.Tests.Core.Library;

/// <summary>User-entered ROM rules remain usable when the saved configuration is normalized.</summary>
public sealed class GameLibraryRulesTests
{
    [Fact]
    public void RomRulesAcceptSpaceSeparatedCompoundAndLongExtensionsAndCanonicalizeSystemAliases()
    {
        using TemporaryDirectory temporary = new();
        GameLibraryConfig settings = new()
        {
            RomSources =
            [
                new RomSourceConfig
                {
                    Id = "rom:fixture",
                    Root = new ManagedContentPath { AbsolutePath = temporary.Root, Directory = true },
                    SystemId = "Genesis",
                    Extensions = ["CHD iso", "nkit.iso", ".abcdefghijklmnopqrstuvw", ".CHD"]
                }
            ]
        };

        GameLibraryRules.Normalize(settings);

        var source = Assert.Single(settings.RomSources);
        Assert.Equal("megadrive", source.SystemId);
        Assert.Equal([".chd", ".iso", ".nkit.iso", ".abcdefghijklmnopqrstuvw"], source.Extensions);
    }
}
