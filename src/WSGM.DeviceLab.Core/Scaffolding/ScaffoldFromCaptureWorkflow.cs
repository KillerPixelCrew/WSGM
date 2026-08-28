using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WSGM.Device.Contracts.Ipc;
using WSGM.DeviceLab.Core.Capture;
using WSGM.DeviceLab.Core.Evidence;
using WSGM.DeviceLab.Core.Inventory;
using WSGM.DeviceLab.Core.Preflight;

namespace WSGM.DeviceLab.Core.Scaffolding;

/// <summary>Creates a conservative exact-identity scaffold directly from a sanitized capture.</summary>
public static partial class ScaffoldFromCaptureWorkflow
{
    /// <summary>Builds and writes a read-only scaffold from validated shareable evidence.</summary>
    /// <param name="capturePath">Sanitized source capture.</param>
    /// <param name="outputDirectory">New explicit output directory.</param>
    /// <param name="publisher">Unverified publisher label for review metadata.</param>
    /// <param name="boundaries">Filesystem safety boundaries.</param>
    /// <returns>The deterministic scaffold plan.</returns>
    public static ScaffoldGenerationPlan Run(
        string capturePath,
        string outputDirectory,
        string publisher,
        DeviceLabPathBoundaries boundaries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capturePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(publisher);
        ArgumentNullException.ThrowIfNull(boundaries);

        byte[] captureBytes = File.ReadAllBytes(capturePath);
        using MemoryStream capture = new(captureBytes, writable: false);
        CaptureBundleReadResult read = CaptureBundleReader.Read(capture);
        if (!read.Succeeded || read.Bundle is null)
        {
            throw new InvalidDataException($"Source capture was rejected: {read.Failure} ({read.Detail}).");
        }

        MachineInventoryFacts facts = SelectExactFacts(read.Bundle);
        string slug = Slug(facts.Board);
        string packageId = $"wsgm.device.scaffold.{slug}";
        string rootNamespace = $"WSGM.Device.Scaffold.{Identifier(slug)}";
        string deviceId = $"scaffold.{slug}";
        EvidenceClaim[] claims = [.. read.Bundle.Claims
            .Where(claim => claim.State is not ClaimState.Rejected
                && string.Equals(claim.Scope.BaseboardProduct, facts.Board, StringComparison.OrdinalIgnoreCase))
            .OrderBy(claim => claim.ClaimId, StringComparer.Ordinal)];
        EvidenceLock evidenceLock = EvidenceLockBuilder.Build(
            deviceId,
            DevicePluginScaffoldGenerator.GeneratorVersion,
            claims,
            []);
        string evidenceHash = Hash(Encoding.UTF8.GetBytes(DeviceLabJson.Serialize(evidenceLock)));
        ScaffoldInputManifest input = new()
        {
            SchemaVersion = ScaffoldSchema.CurrentVersion,
            DeviceDefinitionId = deviceId,
            SourceCaptureSha256 = Hash(captureBytes),
            GeneratorVersion = DevicePluginScaffoldGenerator.GeneratorVersion,
            RuntimeApi = new ScaffoldRuntimeApi
            {
                MinimumVersion = DeviceProtocol.MinSupportedVersion,
                MaximumVersion = DeviceProtocol.MaxSupportedVersion,
                NegotiatedVersion = DeviceProtocol.MaxSupportedVersion,
                SchemaFingerprint = DeviceProtocol.SchemaFingerprint,
            },
            ModuleLocks = [],
            EvidenceLock = new ScaffoldEvidenceLockReference { Sha256 = evidenceHash },
            FixtureIds = [],
        };
        ScaffoldGenerationRequest request = new()
        {
            Input = input,
            PackageId = packageId,
            RootNamespace = rootNamespace,
            DisplayName = $"{facts.Manufacturer} {facts.Board} Device Scaffold",
            Publisher = publisher,
            Identity = new ScaffoldExactIdentity
            {
                SystemManufacturer = facts.Manufacturer,
                BaseboardProduct = facts.Board,
                FirmwareIdentities = [facts.Firmware],
                EndpointId = "primary-usb",
                EndpointRole = "captured exact endpoint; role unverified",
                VendorId = facts.VendorId,
                ProductIds = [facts.ProductId],
            },
            Modules = [],
            Resources = [],
            Capabilities = [],
            Claims = claims,
        };
        ScaffoldGenerationPlan plan = DevicePluginScaffoldGenerator.Create(request);
        DevicePluginScaffoldWriter.Write(plan, outputDirectory, boundaries);
        return plan;
    }

    private static MachineInventoryFacts SelectExactFacts(SanitizedCaptureBundle bundle)
    {
        string manufacturer = bundle.Inventory.Firmware.SystemManufacturer
            ?? throw new InvalidDataException("Capture has no exact SMBIOS system manufacturer.");
        string board = bundle.Inventory.Firmware.BaseboardProduct
            ?? throw new InvalidDataException("Capture has no exact baseboard product.");
        string firmware = bundle.Inventory.Firmware.BiosVersion
            ?? throw new InvalidDataException("Capture has no exact BIOS identity.");
        UsbInterfaceInventory? endpoint = bundle.Inventory.UsbInterfaces
            .Where(candidate => candidate.Present
                && candidate.VendorId is { Length: 4 }
                && candidate.ProductId is { Length: 4 })
            .OrderBy(candidate => candidate.VendorId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.ProductId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (endpoint is null)
        {
            throw new InvalidDataException("Capture has no present exact USB VID/PID endpoint.");
        }

        return new MachineInventoryFacts(
            manufacturer,
            board,
            firmware,
            endpoint.VendorId!,
            endpoint.ProductId!);
    }

    private static string Slug(string value)
    {
        string slug = NonIdentifier().Replace(value.ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrEmpty(slug) ? "unknown-device" : slug;
    }

    private static string Identifier(string slug)
    {
        StringBuilder builder = new();
        foreach (string segment in slug.Split('-', StringSplitOptions.RemoveEmptyEntries))
        {
            builder.Append(char.ToUpperInvariant(segment[0])).Append(segment.AsSpan(1));
        }

        return builder.Length == 0 ? "UnknownDevice" : builder.ToString();
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonIdentifier();

    private sealed record MachineInventoryFacts(
        string Manufacturer,
        string Board,
        string Firmware,
        string VendorId,
        string ProductId);
}
