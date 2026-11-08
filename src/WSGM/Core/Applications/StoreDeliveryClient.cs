using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace WSGM.Core;

/// <summary>One downloadable Store package that Microsoft's delivery service offers.</summary>
/// <param name="Moniker">The package full name, such as <c>Publisher.App_1.2.3.0_neutral_~_hash</c>.</param>
/// <param name="UpdateId">The delivery service's update identity.</param>
/// <param name="Revision">The update identity's revision.</param>
/// <param name="Size">The file size in bytes.</param>
/// <param name="Sha256">The file's SHA-256 digest, as Microsoft publishes it.</param>
internal sealed record StorePackageOffer(string Moniker, string UpdateId, int Revision, long Size, byte[] Sha256)
{
    /// <summary>The package version the moniker names, or null when it names none.</summary>
    internal Version? Version
    {
        get
        {
            var parts = Moniker.Split('_');
            return parts.Length >= 2 && Version.TryParse(parts[1], out var version) ? version : null;
        }
    }

    /// <summary>Whether the offer is a bundle covering every architecture, which is what gets installed.</summary>
    internal bool IsNeutralBundle => Moniker.Split('_') is { Length: >= 3 } parts && parts[2] == "neutral";
}

/// <summary>Asks Microsoft's Store delivery service which packages a Store product offers, and where they are.</summary>
/// <remarks>
///     <para>
///         This is the Windows Update client web service the Store itself uses (FE3). It answers anonymous
///         requests, and a request names a flight ring, so it also reaches builds the retail Store does not
///         offer yet. Downloads come from Microsoft's own content delivery network.
///     </para>
///     <para>
///         The transport is injected. The response is Microsoft's and nested (each update's description is
///         XML escaped inside the SOAP body), so the parser is pure and tested against fixtures.
///     </para>
/// </remarks>
internal sealed class StoreDeliveryClient
{
    private const string Service = "https://fe3.delivery.mp.microsoft.com/ClientWebService/client.asmx";
    private const string SecuredService = "https://fe3cr.delivery.mp.microsoft.com/ClientWebService/client.asmx/secured";
    private const string ActionPrefix = "http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService/";

    /// <summary>
    ///     The update categories a Store client reports as already present. The service filters its answer
    ///     against them; without them it answers nothing. This is the list the Store client sends.
    /// </summary>
    private static readonly int[] InstalledNonLeafUpdateIds =
    [
        1, 2, 3, 11, 19, 544, 549, 2359974, 2359977, 5169044, 8788830, 23110993, 23110994, 54341900, 54343656,
        59830006, 59830007, 59830008, 60484010, 62450018, 62450019, 62450020, 66027979, 66053150, 97657898,
        98822896, 98959022, 98959023, 98959024, 98959025, 98959026, 104433538, 104900364, 105489019, 117765322,
        129905029, 130040031, 132387090, 132393049, 133399034, 138537048, 140377312, 143747671, 158941041,
        158941042, 158941043, 158941044, 159123858, 159130928, 164836897, 164847386, 164848327, 164852241,
        164852246, 164852252, 164852253
    ];

    private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace Ws = "http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService";

    private readonly HttpClient? _http;
    private readonly Func<string, string, CancellationToken, Task<string>> _post;

    /// <summary>Creates the client over an injected SOAP transport.</summary>
    /// <param name="post">
    ///     Posts a SOAP envelope to a URL and returns the response body; throws
    ///     <see cref="HttpRequestException" /> when the service could not be reached or refused.
    /// </param>
    internal StoreDeliveryClient(Func<string, string, CancellationToken, Task<string>>? post = null)
    {
        if (post is null)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        }

        _post = post ?? DefaultPostAsync;
    }

    /// <summary>Lists the packages a Store product offers on one flight ring.</summary>
    /// <param name="categoryId">The product's Windows Update category id.</param>
    /// <param name="ring">The flight ring: <c>Retail</c>, <c>RP</c>, <c>WIS</c> or <c>WIF</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>Every offered file with its identity, size and digest.</returns>
    /// <exception cref="HttpRequestException">The service could not be asked.</exception>
    /// <exception cref="InvalidOperationException">The service answered something unreadable.</exception>
    internal async Task<IReadOnlyList<StorePackageOffer>> FindAsync(string categoryId, string ring,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var cookieResponse = await _post(Service, Envelope("GetCookie", Service, now,
            "<GetCookie xmlns=\"" + Ws.NamespaceName + "\"><oldCookie></oldCookie>"
            + "<lastChange>2015-10-21T17:01:07.1472913Z</lastChange><currentTime>" + Stamp(now)
            + "</currentTime><protocolVersion>1.40</protocolVersion></GetCookie>"), cancellationToken)
            .ConfigureAwait(false);
        var cookie = ParseCookie(cookieResponse);

        var installed = string.Concat(InstalledNonLeafUpdateIds.Select(id =>
            "<int>" + id.ToString(CultureInfo.InvariantCulture) + "</int>"));
        var body = "<SyncUpdates xmlns=\"" + Ws.NamespaceName + "\"><cookie><Expiration>2045-03-11T02:02:48Z"
                   + "</Expiration><EncryptedData>" + SecurityElement.Escape(cookie) + "</EncryptedData></cookie>"
                   + "<parameters><ExpressQuery>false</ExpressQuery><InstalledNonLeafUpdateIDs>" + installed
                   + "</InstalledNonLeafUpdateIDs><OtherCachedUpdateIDs></OtherCachedUpdateIDs>"
                   + "<SkipSoftwareSync>false</SkipSoftwareSync>"
                   + "<NeedTwoGroupOutOfScopeUpdates>true</NeedTwoGroupOutOfScopeUpdates>"
                   + "<FilterAppCategoryIds><CategoryIdentifier><Id>" + SecurityElement.Escape(categoryId)
                   + "</Id></CategoryIdentifier></FilterAppCategoryIds>"
                   + "<TreatAppCategoryIdsAsInstalled>true</TreatAppCategoryIdsAsInstalled>"
                   + "<AlsoPerformRegularSync>false</AlsoPerformRegularSync><ComputerSpec/>"
                   + "<ExtendedUpdateInfoParameters><XmlUpdateFragmentTypes>"
                   + "<XmlUpdateFragmentType>Extended</XmlUpdateFragmentType></XmlUpdateFragmentTypes>"
                   + "<Locales><string>en-US</string><string>en</string></Locales></ExtendedUpdateInfoParameters>"
                   + "<ClientPreferredLanguages><string>en-US</string></ClientPreferredLanguages>"
                   + "<ProductsParameters><SyncCurrentVersionOnly>false</SyncCurrentVersionOnly>"
                   + "<DeviceAttributes>" + SecurityElement.Escape(DeviceAttributes(ring)) + "</DeviceAttributes>"
                   + "<CallerAttributes>E:Interactive=1&amp;IsSeeker=1&amp;</CallerAttributes><Products/>"
                   + "</ProductsParameters></parameters></SyncUpdates>";
        var sync = await _post(Service, Envelope("SyncUpdates", Service, now, body), cancellationToken)
            .ConfigureAwait(false);
        return ParseOffers(sync);
    }

    /// <summary>Asks where an offered file can be downloaded.</summary>
    /// <param name="offer">The file.</param>
    /// <param name="ring">The flight ring it was offered on.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The download address on Microsoft's delivery network.</returns>
    /// <exception cref="HttpRequestException">The service could not be asked.</exception>
    /// <exception cref="InvalidOperationException">The service named no download address.</exception>
    internal async Task<Uri> ResolveDownloadAsync(StorePackageOffer offer, string ring,
        CancellationToken cancellationToken)
    {
        var body = "<GetExtendedUpdateInfo2 xmlns=\"" + Ws.NamespaceName + "\"><updateIDs><UpdateIdentity>"
                   + "<UpdateID>" + SecurityElement.Escape(offer.UpdateId) + "</UpdateID><RevisionNumber>"
                   + offer.Revision.ToString(CultureInfo.InvariantCulture) + "</RevisionNumber></UpdateIdentity>"
                   + "</updateIDs><infoTypes><XmlUpdateFragmentType>FileUrl</XmlUpdateFragmentType>"
                   + "<XmlUpdateFragmentType>FileDecryption</XmlUpdateFragmentType></infoTypes>"
                   + "<deviceAttributes>" + SecurityElement.Escape(DeviceAttributes(ring)) + "</deviceAttributes>"
                   + "</GetExtendedUpdateInfo2>";
        var response = await _post(SecuredService,
                Envelope("GetExtendedUpdateInfo2", SecuredService, DateTime.UtcNow, body), cancellationToken)
            .ConfigureAwait(false);
        return ParseDownload(response);
    }

    /// <summary>Reads the cookie a GetCookie response carries.</summary>
    /// <param name="response">The SOAP response.</param>
    /// <returns>The encrypted cookie data.</returns>
    /// <exception cref="InvalidOperationException">The response carries no cookie.</exception>
    internal static string ParseCookie(string response)
    {
        var data = Load(response).Descendants(Ws + "EncryptedData").FirstOrDefault()?.Value;
        return string.IsNullOrEmpty(data)
            ? throw new InvalidOperationException("Microsoft's Store delivery service returned no session cookie.")
            : data;
    }

    /// <summary>Reads the offered files from a SyncUpdates response.</summary>
    /// <param name="response">The SOAP response.</param>
    /// <returns>Each file that names a package, with the identity its download is resolved by.</returns>
    /// <exception cref="InvalidOperationException">The response is not a SyncUpdates answer.</exception>
    internal static IReadOnlyList<StorePackageOffer> ParseOffers(string response)
    {
        var root = Load(response);
        var result = root.Descendants(Ws + "SyncUpdatesResult").FirstOrDefault()
                     ?? throw new InvalidOperationException(
                         "Microsoft's Store delivery service did not answer the update query.");

        // NewUpdates carries each update's identity; ExtendedUpdateInfo carries its files. Both use the
        // same numeric ID, and the file names its package in InstallerSpecificIdentifier.
        Dictionary<string, (string UpdateId, int Revision)> identities = new(StringComparer.Ordinal);
        foreach (var info in result.Elements(Ws + "NewUpdates").Elements(Ws + "UpdateInfo"))
        {
            var id = info.Element(Ws + "ID")?.Value;
            var identity = Fragment(info.Element(Ws + "Xml")?.Value).Descendants("UpdateIdentity").FirstOrDefault();
            if (id is null || identity?.Attribute("UpdateID")?.Value is not { Length: > 0 } updateId)
            {
                continue;
            }

            identities[id] = (updateId, int.TryParse(identity.Attribute("RevisionNumber")?.Value,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision)
                ? revision
                : 1);
        }

        List<StorePackageOffer> offers = [];
        foreach (var update in result.Elements(Ws + "ExtendedUpdateInfo").Elements(Ws + "Updates")
                     .Elements(Ws + "Update"))
        {
            var id = update.Element(Ws + "ID")?.Value;
            if (id is null || !identities.TryGetValue(id, out var identity))
            {
                continue;
            }

            foreach (var file in Fragment(update.Element(Ws + "Xml")?.Value).Descendants("File"))
            {
                var moniker = file.Attribute("InstallerSpecificIdentifier")?.Value;
                var digest = file.Elements("AdditionalDigest")
                    .FirstOrDefault(item => (string?)item.Attribute("Algorithm") == "SHA256")?.Value;
                if (string.IsNullOrEmpty(moniker) || string.IsNullOrEmpty(digest)
                                                  || !long.TryParse(file.Attribute("Size")?.Value,
                                                      NumberStyles.Integer, CultureInfo.InvariantCulture,
                                                      out var size))
                {
                    continue;
                }

                byte[] sha;
                try
                {
                    sha = Convert.FromBase64String(digest);
                }
                catch (FormatException)
                {
                    continue;
                }

                if (sha.Length == 32)
                {
                    offers.Add(new StorePackageOffer(moniker, identity.UpdateId, identity.Revision, size, sha));
                }
            }
        }

        return offers;
    }

    /// <summary>Reads the download address from a GetExtendedUpdateInfo2 response.</summary>
    /// <param name="response">The SOAP response.</param>
    /// <returns>The first HTTPS or HTTP address on a Microsoft delivery host.</returns>
    /// <exception cref="InvalidOperationException">No such address is named.</exception>
    internal static Uri ParseDownload(string response)
    {
        foreach (var url in Load(response).Descendants(Ws + "Url").Select(item => item.Value))
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && uri.Scheme is "https" or "http"
                && (uri.Host.EndsWith(".delivery.mp.microsoft.com", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.EndsWith(".dl.delivery.mp.microsoft.com", StringComparison.OrdinalIgnoreCase)))
            {
                return uri;
            }
        }

        throw new InvalidOperationException("Microsoft's Store delivery service named no download for the package.");
    }

    private static string DeviceAttributes(string ring)
    {
        return "E:BranchReadinessLevel=CB&CurrentBranch=rs_prerelease&FlightRing=" + ring
               + "&AttrDataVer=226&InstallLanguage=en-US&OSUILocale=en-US&InstallationType=Client"
               + "&FlightingBranchName=&OSSkuId=48&App=WU_STORE&AppVer=1407.2503.28012.0&OSArchitecture=AMD64"
               + "&IsFlightingEnabled=1&TelemetryLevel=1&DefaultUserRegion=244&WuClientVer=1310.2503.26012.0"
               + "&OSVersion=" + Environment.OSVersion.Version.ToString(4) + "&DeviceFamily=Windows.Desktop";
    }

    private static string Envelope(string action, string to, DateTime now, string body)
    {
        return "<s:Envelope xmlns:a=\"http://www.w3.org/2005/08/addressing\" xmlns:s=\"" + Soap.NamespaceName
               + "\"><s:Header><a:Action s:mustUnderstand=\"1\">" + ActionPrefix + action + "</a:Action>"
               + "<a:MessageID>urn:uuid:" + Guid.NewGuid() + "</a:MessageID><a:To s:mustUnderstand=\"1\">" + to
               + "</a:To><o:Security s:mustUnderstand=\"1\" "
               + "xmlns:o=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd\">"
               + "<Timestamp xmlns=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\">"
               + "<Created>" + Stamp(now) + "</Created><Expires>" + Stamp(now.AddMinutes(5)) + "</Expires></Timestamp>"
               + "<wuws:WindowsUpdateTicketsToken wsu:id=\"ClientMSA\" "
               + "xmlns:wsu=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\" "
               + "xmlns:wuws=\"http://schemas.microsoft.com/msus/2014/10/WindowsUpdateAuthorization\">"
               + "<TicketType Name=\"MSA\" Version=\"1.0\" Policy=\"MBI_SSL\"><User/></TicketType>"
               + "</wuws:WindowsUpdateTicketsToken></o:Security></s:Header><s:Body>" + body
               + "</s:Body></s:Envelope>";
    }

    private static string Stamp(DateTime time)
    {
        return time.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    private static XElement Load(string response)
    {
        try
        {
            return XElement.Parse(response);
        }
        catch (XmlException ex)
        {
            throw new InvalidOperationException("Microsoft's Store delivery service answered unreadable data.", ex);
        }
    }

    private static XElement Fragment(string? xml)
    {
        if (string.IsNullOrEmpty(xml))
        {
            return new XElement("Fragment");
        }

        try
        {
            return XElement.Parse("<Fragment>" + xml + "</Fragment>");
        }
        catch (XmlException)
        {
            return new XElement("Fragment");
        }
    }

    private async Task<string> DefaultPostAsync(string url, string envelope, CancellationToken cancellationToken)
    {
        using StringContent content = new(envelope, Encoding.UTF8, "application/soap+xml");
        using var response = await _http!.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Microsoft's Store delivery service refused the request ({(int)response.StatusCode}).", null,
                response.StatusCode);
        }

        return text;
    }
}
