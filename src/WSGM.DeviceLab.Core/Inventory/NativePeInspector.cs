using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WSGM.DeviceLab.Core.Inventory;

internal static class NativePeInspector
{
    private const int MaximumExports = 4096;
    private const int MaximumExportNameBytes = 1024;

    public static bool TryInspect(string path, out NativeBinaryInventory? inventory)
    {
        inventory = null;
        try
        {
            string resolved = Path.GetFullPath(path);
            using FileStream stream = new(resolved, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using PEReader pe = new(stream, PEStreamOptions.LeaveOpen);
            if (pe.PEHeaders.PEHeader is null)
            {
                return false;
            }

            (BinarySignatureState signature, string? signer) = ReadSigner(resolved);
            inventory = new NativeBinaryInventory
            {
                Path = resolved,
                Name = Path.GetFileName(resolved),
                Version = EmptyToNull(FileVersionInfo.GetVersionInfo(resolved).FileVersion),
                Architecture = pe.PEHeaders.CoffHeader.Machine.ToString(),
                Sha256 = Hash(stream),
                Signature = signature,
                SignerSubject = signer,
                Exports = ReadExports(pe),
            };
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or BadImageFormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    private static string Hash(FileStream stream)
    {
        stream.Position = 0;
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static (BinarySignatureState State, string? Subject) ReadSigner(string path)
    {
        try
        {
            using X509Certificate certificate = X509Certificate.CreateFromSignedFile(path);
            return (BinarySignatureState.Signed, EmptyToNull(certificate.Subject));
        }
        catch (CryptographicException)
        {
            return (BinarySignatureState.Unsigned, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (BinarySignatureState.Unknown, null);
        }
    }

    private static IReadOnlyList<string> ReadExports(PEReader pe)
    {
        DirectoryEntry directory = pe.PEHeaders.PEHeader!.ExportTableDirectory;
        if (directory.RelativeVirtualAddress == 0 || directory.Size < 40)
        {
            return [];
        }

        BlobReader table = pe.GetSectionData(directory.RelativeVirtualAddress).GetReader();
        if (table.RemainingBytes < 40)
        {
            return [];
        }

        table.Offset = 24;
        uint nameCount = table.ReadUInt32();
        _ = table.ReadUInt32();
        uint namePointerRva = table.ReadUInt32();
        _ = table.ReadUInt32();
        if (nameCount > MaximumExports || namePointerRva == 0)
        {
            return [];
        }

        BlobReader names = pe.GetSectionData((int)namePointerRva).GetReader();
        if (names.RemainingBytes < checked((int)nameCount * sizeof(uint)))
        {
            return [];
        }

        List<string> exports = [];
        for (int index = 0; index < nameCount; index++)
        {
            uint nameRva = names.ReadUInt32();
            if (nameRva == 0)
            {
                continue;
            }

            BlobReader nameReader = pe.GetSectionData((int)nameRva).GetReader();
            List<byte> bytes = [];
            while (nameReader.RemainingBytes > 0 && bytes.Count < MaximumExportNameBytes)
            {
                byte next = nameReader.ReadByte();
                if (next == 0)
                {
                    break;
                }

                bytes.Add(next);
            }

            if (bytes.Count != 0)
            {
                exports.Add(System.Text.Encoding.ASCII.GetString([.. bytes]));
            }
        }

        exports.Sort(StringComparer.Ordinal);
        return exports;
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
