using System;
using System.Buffers.Binary;
using System.Formats.Asn1;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;

namespace WSGM.Core;

/// <summary>Checks an Authenticode signature against a source descriptor's pinned publisher.</summary>
internal static class GameToolSignature
{
    /// <summary>Verifies the CMS signature and Authenticode digest of the downloaded buffer.</summary>
    /// <param name="bytes">The setup bytes, also used for hashing and embedded archive extraction.</param>
    /// <param name="source">The descriptor declaring the verification and pinned signer.</param>
    internal static void Verify(byte[] bytes, GameToolSource source)
    {
        using var reader = new PEReader(new MemoryStream(bytes));
        var headers = reader.PEHeaders;
        var pe = headers.PEHeader ?? throw new InvalidDataException("The setup is not a PE image.");
        var certificate = pe.CertificateTableDirectory;
        var offset = certificate.RelativeVirtualAddress;
        var end = checked(offset + certificate.Size);
        if (offset <= 0 || end > bytes.Length || certificate.Size < 8)
        {
            throw new InvalidDataException("The setup has no valid embedded signature.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
        if (length < 8 || length > certificate.Size)
        {
            throw new InvalidDataException("The signature is malformed.");
        }

        var signed = new SignedCms();
        signed.Decode(bytes.AsSpan(offset + 8, length - 8).ToArray());
        if (signed.ContentInfo.ContentType.Value != "1.3.6.1.4.1.311.2.1.4")
        {
            throw new InvalidDataException("The embedded signature is not an Authenticode file signature.");
        }
        signed.CheckSignature(verifySignatureOnly: true);
        if (signed.SignerInfos.Count != 1 || !string.Equals(signed.SignerInfos[0].Certificate?.Thumbprint, source.SignerThumbprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The download does not have the officially published signer.");
        }

        var content = new AsnReader(signed.ContentInfo.Content, AsnEncodingRules.BER).ReadSequence();
        content.ReadEncodedValue(); // SpcAttributeTypeAndOptionalValue; the next field is DigestInfo.
        var digestInfo = content.ReadSequence();
        var algorithm = digestInfo.ReadSequence().ReadObjectIdentifier();
        var expected = digestInfo.ReadOctetString();
        var actual = Digest(bytes, algorithm switch
        {
            "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
            "1.3.14.3.2.26" => HashAlgorithmName.SHA1,
            _ => throw new InvalidDataException("Unsupported Authenticode digest.")
        });
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            throw new InvalidDataException("The setup's signed file digest does not match.");
        }
    }
    /// <summary>Hashes PE headers, ordered sections and pre-certificate overlay according to Authenticode.</summary>
    /// <param name="bytes">The PE image.</param>
    /// <param name="algorithm">The signed digest algorithm.</param>
    /// <returns>The digest, excluding the certificate table and anything after it.</returns>
    internal static byte[] Digest(byte[] bytes, HashAlgorithmName algorithm)
    {
        using var reader = new PEReader(new MemoryStream(bytes));
        var headers = reader.PEHeaders;
        var pe = headers.PEHeader ?? throw new InvalidDataException("The setup is not a PE image.");
        var checksum = headers.PEHeaderStartOffset + 64;
        var security = headers.PEHeaderStartOffset + (pe.Magic == PEMagic.PE32Plus ? 112 : 96) + 32;
        var certificate = pe.CertificateTableDirectory.RelativeVirtualAddress;
        var end = certificate > 0 ? certificate : bytes.Length;
        if (end > bytes.Length || pe.SizeOfHeaders > end || security + 8 > pe.SizeOfHeaders)
        {
            throw new InvalidDataException("The signed PE layout is invalid.");
        }

        using var hash = IncrementalHash.CreateHash(algorithm);
        hash.AppendData(bytes.AsSpan(0, checksum));
        hash.AppendData(bytes.AsSpan(checksum + 4, security - checksum - 4));
        hash.AppendData(bytes.AsSpan(security + 8, pe.SizeOfHeaders - security - 8));
        var hashed = pe.SizeOfHeaders;
        var previousEnd = pe.SizeOfHeaders;
        foreach (var section in headers.SectionHeaders.OrderBy(section => section.PointerToRawData))
        {
            if (section.SizeOfRawData == 0)
            {
                continue;
            }

            if (section.PointerToRawData < previousEnd || section.SizeOfRawData > end - section.PointerToRawData)
            {
                throw new InvalidDataException("The signed PE sections overlap or exceed the certificate offset.");
            }

            hash.AppendData(bytes.AsSpan(section.PointerToRawData, section.SizeOfRawData));
            previousEnd = checked(section.PointerToRawData + section.SizeOfRawData);
            hashed = checked(hashed + section.SizeOfRawData);
        }

        if (hashed < end)
        {
            hash.AppendData(bytes.AsSpan(hashed, end - hashed));
        }

        return hash.GetHashAndReset();
    }

}
