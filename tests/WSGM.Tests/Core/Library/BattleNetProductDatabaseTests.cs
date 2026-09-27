using System.Text;
using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>Battle.net's own record of installs, for games it wrote no uninstall entry for.</summary>
public sealed class BattleNetProductDatabaseTests
{
    private static byte[] Field(int number, byte[] value)
    {
        List<byte> bytes = [(byte)((number << 3) | 2)];
        var length = value.Length;
        while (length >= 0x80)
        {
            bytes.Add((byte)(length | 0x80));
            length >>= 7;
        }

        bytes.Add((byte)length);
        bytes.AddRange(value);
        return [.. bytes];
    }

    private static byte[] Install(string uid, string code, string path)
    {
        return
        [
            .. Field(1, Encoding.UTF8.GetBytes(uid)),
            .. Field(2, Encoding.UTF8.GetBytes(code)),
            .. Field(3, Field(1, Encoding.UTF8.GetBytes(path)))
        ];
    }

    /// <summary>A <c>product.db</c> recording these installs.</summary>
    /// <param name="installs">Each install's uid, product code and path.</param>
    /// <returns>The file's bytes.</returns>
    internal static byte[] Database(params (string Uid, string Code, string Path)[] installs)
    {
        return [.. installs.SelectMany(install => Field(1, Install(install.Uid, install.Code, install.Path)))];
    }

    [Fact]
    public void EveryInstallIsReadWithItsPath()
    {
        byte[] database =
        [
            .. Field(1, Install("prometheus", "pro", "C:/Program Files (x86)/Overwatch")),
            .. Field(1, Install("fenris", "fen", "D:/Games/Diablo IV"))
        ];

        var installs = BattleNetLibrarySource.ParseProducts(database);

        Assert.Equal(
            [("prometheus", "C:/Program Files (x86)/Overwatch"), ("fenris", "D:/Games/Diablo IV")],
            installs);
    }

    [Fact]
    public void ATruncatedFileYieldsWhatWasRead()
    {
        byte[] whole = [.. Field(1, Install("prometheus", "pro", "C:/Games/Overwatch"))];
        byte[] database = [.. whole, .. whole[..5]];

        Assert.Single(BattleNetLibrarySource.ParseProducts(database));
    }

    [Fact]
    public void NoFileIsNoInstalls()
    {
        Assert.Empty(BattleNetLibrarySource.ParseProducts(null));
    }
}
