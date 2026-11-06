using System;
using System.Buffers.Binary;
using System.Formats.Asn1;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;

namespace WSGM.Core;

/// <summary>Checks ReShade's self-signed Authenticode signature against its officially published signer.</summary>
internal static class GameToolSignature
{
    // Published at https://reshade.me/#download. ReShade deliberately uses a self-signed certificate.
    private const string ReShadeSigner = "589690208A5E52FB96980C4A6698F50ACD47C49F";

    /// <summary>Verifies both the CMS signature and its Authenticode digest over the exact downloaded PE.</summary>
    /// <param name="path">The official standard setup download.</param>
    internal static void VerifyReShade(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var reader = new PEReader(new MemoryStream(bytes));
        var headers = reader.PEHeaders;
        var pe = headers.PEHeader ?? throw new InvalidDataException("The setup is not a PE image.");
        var certificate = pe.CertificateTableDirectory;
        var offset = certificate.RelativeVirtualAddress;
        var end = checked(offset + certificate.Size);
        if (offset <= 0 || end > bytes.Length || certificate.Size < 8)
        {
            throw new InvalidDataException("The ReShade setup has no valid embedded signature.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
        if (length < 8 || length > certificate.Size)
        {
            throw new InvalidDataException("The ReShade signature is malformed.");
        }

        var signed = new SignedCms();
        signed.Decode(bytes.AsSpan(offset + 8, length - 8).ToArray());
        signed.CheckSignature(verifySignatureOnly: true);
        if (signed.SignerInfos.Count != 1 || signed.SignerInfos[0].Certificate?.Thumbprint != ReShadeSigner)
        {
            throw new InvalidDataException("The ReShade download does not have the officially published signer.");
        }

        var content = new AsnReader(signed.ContentInfo.Content, AsnEncodingRules.BER).ReadSequence();
        content.ReadEncodedValue(); // SpcAttributeTypeAndOptionalValue; the next field is DigestInfo.
        var digestInfo = content.ReadSequence();
        var algorithm = digestInfo.ReadSequence().ReadObjectIdentifier();
        var expected = digestInfo.ReadOctetString();
        var checksum = headers.PEHeaderStartOffset + 64;
        var securityDirectory = headers.PEHeaderStartOffset + (pe.Magic == PEMagic.PE32Plus ? 112 : 96) + 32;
        using var hash = IncrementalHash.CreateHash(algorithm switch
        {
            "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
            "1.3.14.3.2.26" => HashAlgorithmName.SHA1,
            _ => throw new InvalidDataException("Unsupported ReShade Authenticode digest.")
        });
        hash.AppendData(bytes.AsSpan(0, checksum));
        hash.AppendData(bytes.AsSpan(checksum + 4, securityDirectory - checksum - 4));
        hash.AppendData(bytes.AsSpan(securityDirectory + 8, offset - securityDirectory - 8));
        hash.AppendData(bytes.AsSpan(end));
        if (!CryptographicOperations.FixedTimeEquals(expected, hash.GetHashAndReset()))
        {
            throw new InvalidDataException("The ReShade setup's signed file digest does not match.");
        }
    }
}
