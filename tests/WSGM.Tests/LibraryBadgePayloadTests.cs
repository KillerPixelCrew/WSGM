using WSGM.Core;

namespace WSGM.Tests;

public sealed class LibraryBadgePayloadTests
{
    [Fact]
    public void EachEntryCarriesTheNameKindAndConnectionSeparately()
    {
        string literal = SteamPageBridge.BuildMapLiteral(new Dictionary<long, SteamLibraryBadge>
        {
            [70] = new("Games", SteamLibraryKind.Card, Connected: true),
        });

        Assert.Equal("{\"70\":{n:\"Games\",k:\"card\",c:1}}", literal);
    }

    [Fact]
    public void ADisconnectedLibraryStillCarriesItsNameAndSaysItIsAbsent()
    {
        // The name comes from the card's own marker, so it keeps naming the library while the
        // library is gone. Dropping the entry would make the game read as internal instead.
        string literal = SteamPageBridge.BuildMapLiteral(new Dictionary<long, SteamLibraryBadge>
        {
            [70] = new("Blue card", SteamLibraryKind.Card, Connected: false),
        });

        Assert.Contains("n:\"Blue card\"", literal, StringComparison.Ordinal);
        Assert.Contains("c:0", literal, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionIsAlwaysEmittedSoAbsentNeverReadsAsInternal()
    {
        // The script tells "no entry" (internal library) from "entry, disconnected". A key left
        // out when false would collapse the two.
        foreach (bool connected in new[] { true, false })
        {
            string literal = SteamPageBridge.BuildMapLiteral(new Dictionary<long, SteamLibraryBadge>
            {
                [1] = new("L", SteamLibraryKind.External, connected),
            });

            Assert.Contains(connected ? "c:1" : "c:0", literal, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnExternalLibraryIsNotLabelledAsACard()
        => Assert.Contains(
            "k:\"external\"",
            SteamPageBridge.BuildMapLiteral(new Dictionary<long, SteamLibraryBadge>
            {
                [1] = new("NAS", SteamLibraryKind.External, Connected: true),
            }),
            StringComparison.Ordinal);

    [Fact]
    public void ALibraryNameIsEscapedRatherThanClosingTheLiteral()
    {
        // The map is spliced into a script, so a name is attacker-adjacent input: it comes from a
        // marker file on a card anybody can write.
        string literal = SteamPageBridge.BuildMapLiteral(new Dictionary<long, SteamLibraryBadge>
        {
            [1] = new("\"};alert(1);//", SteamLibraryKind.Card, Connected: true),
        });

        Assert.DoesNotContain("\"};alert", literal, StringComparison.Ordinal);
    }

    [Fact]
    public void NoTrackedLibrariesIsAnEmptyMapRatherThanNothing()
        => Assert.Equal("{}", SteamPageBridge.BuildMapLiteral(new Dictionary<long, SteamLibraryBadge>()));

    [Fact]
    public void SeveralGamesOnOneLibraryEachGetTheirOwnEntry()
    {
        SteamLibraryBadge card = new("Games", SteamLibraryKind.Card, Connected: true);
        string literal = SteamPageBridge.BuildMapLiteral(new Dictionary<long, SteamLibraryBadge>
        {
            [70] = card,
            [220] = card,
        });

        Assert.Contains("\"70\":", literal, StringComparison.Ordinal);
        Assert.Contains("\"220\":", literal, StringComparison.Ordinal);
    }
}
