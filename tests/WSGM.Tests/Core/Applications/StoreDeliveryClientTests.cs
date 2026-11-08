using System.Security;
using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>
///     Reading Microsoft's Store delivery service answers. Each update's description arrives as XML escaped inside
///     the SOAP body, the identity in NewUpdates and the files in ExtendedUpdateInfo, joined by the same ID.
/// </summary>
public sealed class StoreDeliveryClientTests
{
    internal const string Bundle = "Microsoft.GamingApp_2612.1001.23.0_neutral_~_8wekyb3d8bbwe";
    internal static readonly byte[] Digest = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();

    [Fact]
    public void AnOfferJoinsItsIdentityAndItsFile()
    {
        var offers = StoreDeliveryClient.ParseOffers(Sync((7, "update-7", 3, Bundle, 1234, Digest)));

        var offer = Assert.Single(offers);
        Assert.Equal(Bundle, offer.Moniker);
        Assert.Equal("update-7", offer.UpdateId);
        Assert.Equal(3, offer.Revision);
        Assert.Equal(1234, offer.Size);
        Assert.Equal(Digest, offer.Sha256);
        Assert.Equal(new Version(2612, 1001, 23, 0), offer.Version);
        Assert.True(offer.IsNeutralBundle);
    }

    [Fact]
    public void FilesWithoutAnIdentityADigestOrASizeAreSkipped()
    {
        var response = Sync((1, "with-id", 1, Bundle, 10, Digest))
            .Replace("<NewUpdates>", "<NewUpdates><UpdateInfo><ID>9</ID><Xml></Xml></UpdateInfo>");
        response = response.Replace("</Updates>",
            "<Update><ID>2</ID><Xml>" + SecurityElement.Escape(FileXml("orphan_1.0.0.0_neutral_~_x", 5, Digest))
            + "</Xml></Update></Updates>");

        Assert.Equal(["with-id"], StoreDeliveryClient.ParseOffers(response).Select(offer => offer.UpdateId));
        Assert.Empty(StoreDeliveryClient.ParseOffers(Sync((1, "short", 1, Bundle, 10, [1, 2, 3]))));
    }

    [Fact]
    public void AnAnswerThatIsNotAnUpdateListIsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => StoreDeliveryClient.ParseOffers("<not-xml"));
        Assert.Throws<InvalidOperationException>(() =>
            StoreDeliveryClient.ParseOffers("<Envelope xmlns=\"http://www.w3.org/2003/05/soap-envelope\"/>"));
    }

    [Fact]
    public void TheCookieIsReadAndAMissingOneIsRefused()
    {
        Assert.Equal("secret", StoreDeliveryClient.ParseCookie(Cookie("secret")));
        Assert.Throws<InvalidOperationException>(() => StoreDeliveryClient.ParseCookie(Cookie("")));
    }

    [Fact]
    public void OnlyAMicrosoftDeliveryAddressIsAccepted()
    {
        Assert.Equal("tlu.dl.delivery.mp.microsoft.com",
            StoreDeliveryClient.ParseDownload(Urls("https://example.com/file",
                "http://tlu.dl.delivery.mp.microsoft.com/filestreamingservice/files/a?P1=1")).Host);
        Assert.Throws<InvalidOperationException>(() =>
            StoreDeliveryClient.ParseDownload(Urls("https://delivery.mp.microsoft.com.example.com/file")));
    }

    internal static string Cookie(string data)
    {
        return Wrap("<GetCookieResponse xmlns=\"" + Ws + "\"><GetCookieResult><EncryptedData>" + data
                    + "</EncryptedData></GetCookieResult></GetCookieResponse>");
    }

    internal static string Sync(params (int Id, string UpdateId, int Revision, string Moniker, long Size,
        byte[] Sha256)[] updates)
    {
        var infos = string.Concat(updates.Select(update => "<UpdateInfo><ID>" + update.Id + "</ID><Xml>"
            + SecurityElement.Escape("<UpdateIdentity UpdateID=\"" + update.UpdateId + "\" RevisionNumber=\""
                                     + update.Revision + "\" /><Properties UpdateType=\"Software\" />")
            + "</Xml></UpdateInfo>"));
        var files = string.Concat(updates.Select(update => "<Update><ID>" + update.Id + "</ID><Xml>"
            + SecurityElement.Escape(FileXml(update.Moniker, update.Size, update.Sha256)) + "</Xml></Update>"));
        return Wrap("<SyncUpdatesResponse xmlns=\"" + Ws + "\"><SyncUpdatesResult><NewUpdates>" + infos
                    + "</NewUpdates><ExtendedUpdateInfo><Updates>" + files
                    + "</Updates></ExtendedUpdateInfo></SyncUpdatesResult></SyncUpdatesResponse>");
    }

    internal static string Urls(params string[] urls)
    {
        return Wrap("<GetExtendedUpdateInfo2Response xmlns=\"" + Ws + "\"><GetExtendedUpdateInfo2Result>"
                    + "<FileLocations>" + string.Concat(urls.Select(url =>
                        "<FileLocation><Url>" + SecurityElement.Escape(url) + "</Url></FileLocation>"))
                    + "</FileLocations></GetExtendedUpdateInfo2Result></GetExtendedUpdateInfo2Response>");
    }

    private const string Ws = "http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService";

    private static string FileXml(string moniker, long size, byte[] sha256)
    {
        return "<ExtendedProperties /><Files><File FileName=\"f.msixbundle\" Size=\"" + size
               + "\" InstallerSpecificIdentifier=\"" + moniker + "\"><AdditionalDigest Algorithm=\"SHA256\">"
               + Convert.ToBase64String(sha256) + "</AdditionalDigest></File></Files>";
    }

    private static string Wrap(string body)
    {
        return "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body>" + body
               + "</s:Body></s:Envelope>";
    }
}
