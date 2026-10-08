using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace WSGM.Core;

/// <summary>Installs local Eden firmware into its native NAND layout with an atomic directory swap.</summary>
internal static class EmulatorFirmware
{
    internal static void InstallEden(EmulatorInstallation installed, EmulatorPrerequisite requirement, string source,
        CancellationToken token)
    {
        // Eden's QtCommon::Content::InstallFirmware copies NCA files into registered, then
        // verifies/decrypts with its core. WSGM verifies content-addressed dumps before activation;
        // Eden still owns key compatibility and firmware decryption on its next start.
        var destination = EmulatorStorage.PrerequisitePath(installed, requirement);
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, ".wsgm-firmware-" + Guid.NewGuid().ToString("N"));
        var backup = destination + ".before-wsgm-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stage);
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            if (Directory.Exists(source))
            {
                foreach (var file in Directory.EnumerateFiles(source, "*.nca", new EnumerationOptions
                             { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    using var input = File.OpenRead(file);
                    Copy(input, Path.GetFileName(file), input.Length);
                }
            }
            else if (Path.GetExtension(source).Equals(".nca", StringComparison.OrdinalIgnoreCase))
            {
                using var input = File.OpenRead(source);
                Copy(input, Path.GetFileName(source), input.Length);
            }
            else
            {
                using var archive = ZipFile.OpenRead(source);
                foreach (var entry in archive.Entries.Where(entry =>
                             entry.Name.EndsWith(".nca", StringComparison.OrdinalIgnoreCase)))
                {
                    using var input = entry.Open();
                    Copy(input, entry.Name, entry.Length);
                }
            }

            if (names.Count == 0)
            {
                throw new InvalidDataException("The firmware package contains no NCA files.");
            }

            token.ThrowIfCancellationRequested();
            if (Directory.Exists(destination))
            {
                if (new DirectoryInfo(destination).LinkTarget is not null)
                {
                    throw new InvalidDataException(
                        "The native NAND firmware folder is a link. Configure its target in Eden first.");
                }

                Directory.Move(destination, backup);
            }

            try
            {
                Directory.Move(stage, destination);
            }
            catch
            {
                if (Directory.Exists(backup))
                {
                    Directory.Move(backup, destination);
                }

                throw;
            }

            return;

            void Copy(Stream input, string name, long size)
            {
                token.ThrowIfCancellationRequested();
                var id = name.Split('.')[0];
                if (id.Length != 32 || !id.All(Uri.IsHexDigit) || name != Path.GetFileName(name)
                    || !names.Add(name) || names.Count > 1024 || size < 0xC00 || size > 512 * 1024 * 1024
                    || (total += size) > 4L * 1024 * 1024 * 1024)
                {
                    throw new InvalidDataException(
                        "The firmware package contains an invalid, duplicate or oversized NCA file.");
                }

                var path = Path.Combine(stage, name);
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                {
                    var buffer = new byte[65536];
                    long written = 0;
                    int count;
                    while ((count = input.Read(buffer)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        written += count;
                        if (written > size)
                        {
                            throw new InvalidDataException("The NCA file exceeds its declared size.");
                        }

                        output.Write(buffer, 0, count);
                    }

                    if (written != size)
                    {
                        throw new InvalidDataException("The NCA file is incomplete.");
                    }
                }

                using var staged = File.OpenRead(path);
                var hash = Convert.ToHexString(SHA256.HashData(staged));
                if (!hash.StartsWith(id, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Firmware content does not match its NCA content ID: " + name);
                }
            }
        }
        finally
        {
            if (Directory.Exists(stage))
            {
                Directory.Delete(stage, true);
            }
        }
    }
}
