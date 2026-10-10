using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using WSGM.DeviceLab.Application;
using WSGM.DeviceLab.Capture;
using WSGM.DeviceLab.Fixtures;
using WSGM.DeviceLab.Inventory;
using WSGM.DeviceLab.Preflight;

namespace WSGM.DeviceLab.Scaffolding;

/// <summary>Exact identity copied into a new LibHandheld contribution starter.</summary>
internal sealed record PluginScaffoldIdentity
{
    /// <summary>Required SMBIOS system manufacturer.</summary>
    public required string SystemManufacturer { get; init; }

    /// <summary>Required SMBIOS baseboard product.</summary>
    public required string BaseboardProduct { get; init; }

    /// <summary>Required SMBIOS system SKU.</summary>
    public required string SystemSku { get; init; }

    /// <summary>Required exact BIOS version.</summary>
    public required string BiosVersion { get; init; }

    /// <summary>Required USB vendor identifier.</summary>
    public required string UsbVendorId { get; init; }

    /// <summary>Required USB product identifier.</summary>
    public required string UsbProductId { get; init; }

    /// <summary>Required USB device release.</summary>
    public required string UsbDeviceRelease { get; init; }
}

/// <summary>Files written by token replacement from the checked-in LibHandheld contribution template.</summary>
internal sealed record PluginScaffoldResult
{
    /// <summary>New absolute output directory.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Stable contribution ID.</summary>
    public required string ContributionId { get; init; }

    /// <summary>Native family namespace.</summary>
    public required string RootNamespace { get; init; }

    /// <summary>Exact copied device identity.</summary>
    public required PluginScaffoldIdentity Identity { get; init; }

    /// <summary>Relative files written from the checked-in templates.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];
}

/// <summary>Copies the checked-in LibHandheld contribution template and replaces exact identity tokens.</summary>
internal static partial class ScaffoldFromCaptureWorkflow
{
    private const string ResourcePrefix = "WSGM.DeviceLab.Templates.MinimalPlugin.";

    private static readonly IReadOnlyList<TemplateFile> Templates =
    [
        new("contribution.json.template", "contribution.json"),
        new("DeviceIdentity.cs.template", "DeviceIdentity.cs"),
        new("ReportDecoder.cs.template", "ReportDecoder.cs"),
        new("README.md.template", "README.md"),
        new("LICENSE.txt.template", "LICENSE.txt")
    ];

    /// <summary>Writes a hardware-empty starter from one validated current capture.</summary>
    /// <param name="capturePath">Sanitized source capture.</param>
    /// <param name="outputDirectory">New explicit output directory.</param>
    /// <param name="boundaries">Filesystem safety boundaries.</param>
    /// <param name="usbInstanceId">Exact endpoint selection when the capture contains more than one candidate.</param>
    /// <param name="cancellationToken">Cancels validation or publication.</param>
    /// <returns>The copied template files and exact identity.</returns>
    public static PluginScaffoldResult Run(
        string capturePath,
        string outputDirectory,
        DeviceLabPathBoundaries boundaries,
        string? usbInstanceId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capturePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(boundaries);
        cancellationToken.ThrowIfCancellationRequested();

        using FileStream capture = new(capturePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var read = CaptureBundleReader.Read(capture, cancellationToken);
        if (!read.Succeeded || read.Bundle is null)
        {
            throw new InvalidDataException($"Source capture was rejected: {read.Failure} ({read.Detail}).");
        }

        var identity = SelectExactIdentity(read.Bundle, usbInstanceId);
        capture.Position = 0;
        var sourceHash = Convert.ToHexStringLower(SHA256.HashData(capture));
        cancellationToken.ThrowIfCancellationRequested();
        return Write(identity, outputDirectory, boundaries, new PluginScaffoldExtras
        {
            SourceCapture = read.Bundle,
            SourceCaptureSha256 = sourceHash
        }, cancellationToken);
    }

    /// <summary>Renders the LibHandheld contribution template for an exact identity and publishes it as a new directory.</summary>
    /// <param name="identity">Exact identity the generated detection matches.</param>
    /// <param name="outputDirectory">New explicit output directory.</param>
    /// <param name="boundaries">Filesystem safety boundaries.</param>
    /// <param name="extras">
    ///     Extra templates and tokens, and the contribution's hardware and observed-role lists; null renders the
    ///     captured board as the one hardware rule and no capabilities.
    /// </param>
    /// <param name="cancellationToken">Cancels rendering or publication.</param>
    /// <returns>The written files and identity.</returns>
    internal static PluginScaffoldResult Write(
        PluginScaffoldIdentity identity,
        string outputDirectory,
        DeviceLabPathBoundaries boundaries,
        PluginScaffoldExtras? extras,
        CancellationToken cancellationToken)
    {
        var slug = Slug(identity.BaseboardProduct);
        var rootNamespace = $"LibHandheld.Families.{Identifier(slug)}";
        var packageId = $"libhandheld.{slug}";
        var displayName = $"{identity.SystemManufacturer} {identity.BaseboardProduct} Handheld Contribution";
        var tokens = Tokens(
            rootNamespace,
            packageId,
            displayName,
            identity);
        tokens["HARDWARE_JSON"] = extras?.HardwareJson
                                  ??
                                  $"{{ \"baseboardProduct\": \"{tokens["BOARD_JSON"]}\", \"systemSku\": \"{tokens["SYSTEM_SKU_JSON"]}\" }}";
        // Observed roles are evidence only; the contribution registers no native capabilities.
        tokens["CAPABILITIES_JSON"] = extras?.CapabilitiesJson ?? "[]";
        foreach (var (key, value) in extras?.Tokens ?? new Dictionary<string, string>())
        {
            tokens.Add(key, value);
        }

        List<(string Path, string Content)> rendered = [];
        foreach (var template in Templates.Concat(extras?.Templates ?? []))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = template.OutputPath.Replace("{rootNamespace}", rootNamespace, StringComparison.Ordinal);
            var content = ReplaceTokens(ReadTemplate(template.ResourceName), tokens);
            rendered.Add((path, Normalize(content)));
        }

        using var metadata = JsonDocument.Parse(rendered.Single(file => file.Path == "contribution.json").Content);

        var output = DeviceLabOutputPathPolicy.Evaluate(
            outputDirectory,
            DeviceLabOutputTargetKind.Directory,
            boundaries);
        if (!output.IsAllowed || output.FullPath is null)
        {
            throw new IOException(output.Reason ?? "Scaffold output path was rejected.");
        }

        if (Directory.Exists(output.FullPath) || File.Exists(output.FullPath))
        {
            throw new IOException("Scaffold output must be a new directory.");
        }

        var parent = Path.GetDirectoryName(output.FullPath)
                     ?? throw new IOException("Scaffold output has no parent directory.");
        var temporary = DurableFile.StagingPath(output.FullPath);
        try
        {
            Directory.CreateDirectory(parent);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(temporary);
            foreach (var (relative, content) in rendered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.GetFullPath(Path.Combine(temporary, relative));
                if (!path.StartsWith(
                        temporary + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("A template output escaped the scaffold directory.");
                }

                DurableFile.WriteNew(path, file => file.Write(Encoding.UTF8.GetBytes(content)));
            }

            if (extras?.SourceCapture is { } sourceCapture)
            {
                FixtureExtractionWorkflow.Extract(sourceCapture, extras.SourceCaptureSha256!, "libhandheld-capture",
                    Path.Combine(temporary, "fixtures"), boundaries, cancellationToken);
                foreach (var fixture in Directory.EnumerateFiles(Path.Combine(temporary, "fixtures"), "*",
                             SearchOption.AllDirectories))
                {
                    rendered.Add((Path.GetRelativePath(temporary, fixture), string.Empty));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(output.FullPath) || File.Exists(output.FullPath))
            {
                throw new IOException("Scaffold output was created before publication.");
            }

            Directory.Move(temporary, output.FullPath);
        }
        catch
        {
            DurableFile.TryDeleteDirectory(temporary);
            throw;
        }

        return new PluginScaffoldResult
        {
            OutputDirectory = output.FullPath,
            ContributionId = packageId,
            RootNamespace = rootNamespace,
            Identity = identity,
            Files = [.. rendered.Select(file => file.Path).Order(StringComparer.Ordinal)]
        };
    }

    private static PluginScaffoldIdentity SelectExactIdentity(
        SanitizedCaptureBundle bundle,
        string? usbInstanceId)
    {
        UsbInterfaceInventory[] endpoints =
        [
            .. bundle.Inventory.UsbInterfaces
                .Where(candidate => candidate is
                {
                    Present: true,
                    VendorId.Length: 4,
                    ProductId.Length: 4,
                    DeviceRelease.Length: 4
                })
                .OrderBy(candidate => candidate.VendorId, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.ProductId, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.DeviceRelease, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.InstanceId, StringComparer.Ordinal)
        ];
        if (endpoints.Length == 0)
        {
            throw new InvalidDataException("Capture has no present exact USB VID/PID/release endpoint.");
        }

        UsbInterfaceInventory endpoint;
        if (string.IsNullOrWhiteSpace(usbInstanceId))
        {
            if (endpoints.Length != 1)
            {
                var choices = string.Join(
                    ", ",
                    endpoints.Take(16).Select(candidate => candidate.InstanceId));
                throw new InvalidDataException(
                    $"Capture has {endpoints.Length} exact USB endpoints. Select one exact instance ID: {choices}.");
            }

            endpoint = endpoints[0];
        }
        else
        {
            UsbInterfaceInventory[] matches =
            [
                .. endpoints.Where(candidate => string.Equals(
                    candidate.InstanceId,
                    usbInstanceId,
                    StringComparison.Ordinal))
            ];
            if (matches.Length != 1)
            {
                throw new InvalidDataException(
                    "The selected USB instance ID did not identify exactly one present exact endpoint.");
            }

            endpoint = matches[0];
        }

        return new PluginScaffoldIdentity
        {
            SystemManufacturer = bundle.Inventory.Firmware.SystemManufacturer
                                 ?? throw new InvalidDataException("Capture has no exact SMBIOS system manufacturer."),
            BaseboardProduct = bundle.Inventory.Firmware.BaseboardProduct
                               ?? throw new InvalidDataException("Capture has no exact baseboard product."),
            SystemSku = bundle.Inventory.Firmware.SystemSku
                        ?? throw new InvalidDataException("Capture has no exact SMBIOS system SKU."),
            BiosVersion = bundle.Inventory.Firmware.BiosVersion
                          ?? throw new InvalidDataException("Capture has no exact BIOS version."),
            UsbVendorId = endpoint.VendorId!,
            UsbProductId = endpoint.ProductId!,
            UsbDeviceRelease = endpoint.DeviceRelease!
        };
    }

    private static Dictionary<string, string> Tokens(
        string rootNamespace,
        string packageId,
        string displayName,
        PluginScaffoldIdentity identity)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ROOT_NAMESPACE"] = rootNamespace,
            ["PACKAGE_ID_JSON"] = Json(packageId),
            ["PACKAGE_ID_CS"] = CSharp(packageId),
            ["DISPLAY_NAME_JSON"] = Json(displayName),
            ["DISPLAY_NAME_MD"] = Markdown(displayName),
            ["MANUFACTURER_CS"] = CSharp(identity.SystemManufacturer),
            ["MANUFACTURER_MD"] = Markdown(identity.SystemManufacturer),
            ["BOARD_CS"] = CSharp(identity.BaseboardProduct),
            ["BOARD_MD"] = Markdown(identity.BaseboardProduct),
            ["BOARD_JSON"] = Json(identity.BaseboardProduct),
            ["SYSTEM_SKU_CS"] = CSharp(identity.SystemSku),
            ["SYSTEM_SKU_MD"] = Markdown(identity.SystemSku),
            ["SYSTEM_SKU_JSON"] = Json(identity.SystemSku),
            ["BIOS_CS"] = CSharp(identity.BiosVersion),
            ["BIOS_MD"] = Markdown(identity.BiosVersion),
            ["USB_VENDOR_CS"] = CSharp(identity.UsbVendorId),
            ["USB_PRODUCT_CS"] = CSharp(identity.UsbProductId),
            ["USB_RELEASE_CS"] = CSharp(identity.UsbDeviceRelease),
            ["USB_MD"] = Markdown($"{identity.UsbVendorId}:{identity.UsbProductId} release {identity.UsbDeviceRelease}")
        };
    }

    private static string ReadTemplate(string name)
    {
        var assembly = typeof(ScaffoldFromCaptureWorkflow).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourcePrefix + name)
                           ?? throw new InvalidDataException($"Checked-in contribution template '{name}' is missing.");
        using StreamReader reader = new(stream, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }

    private static string ReplaceTokens(string template, IReadOnlyDictionary<string, string> tokens)
    {
        var rendered = template;
        foreach (var (key, value) in tokens)
        {
            rendered = rendered.Replace($"{{{{{key}}}}}", value, StringComparison.Ordinal);
        }

        return rendered.Contains("{{", StringComparison.Ordinal)
            ? throw new InvalidDataException("A checked-in plugin template contains an unresolved token.")
            : rendered;
    }

    private static string Slug(string value)
    {
        var slug = NonIdentifier().Replace(value.ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrEmpty(slug) ? "unknown-device" : slug;
    }

    private static string Identifier(string slug)
    {
        StringBuilder builder = new();
        foreach (var segment in slug.Split('-', StringSplitOptions.RemoveEmptyEntries))
        {
            builder.Append(char.ToUpperInvariant(segment[0])).Append(segment.AsSpan(1));
        }

        return builder.Length == 0 ? "UnknownDevice"
            : char.IsDigit(builder[0]) ? "Device" + builder
            : builder.ToString();
    }

    internal static string CSharp(string value)
    {
        return value
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }

    private static string Json(string identity)
    {
        return JsonEncodedText.Encode(identity).ToString();
    }

    private static string Markdown(string value)
    {
        return value
            .Replace('`', '\'')
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }

    private static string Normalize(string content)
    {
        return content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd() + "\n";
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonIdentifier();

    /// <summary>One checked-in template and the path it renders to.</summary>
    /// <param name="ResourceName">Template file name under <c>Templates/MinimalPlugin</c>.</param>
    /// <param name="OutputPath">Relative output path; <c>{rootNamespace}</c> is replaced.</param>
    internal sealed record TemplateFile(string ResourceName, string OutputPath);
}

/// <summary>What another scaffold source adds to the LibHandheld contribution template.</summary>
internal sealed record PluginScaffoldExtras
{
    /// <summary>Validated sanitized capture for recorded decoder fixtures.</summary>
    public SanitizedCaptureBundle? SourceCapture { get; init; }

    /// <summary>Hash computed from the same retained capture handle.</summary>
    public string? SourceCaptureSha256 { get; init; }

    /// <summary>The contribution's observed hardware rules as JSON objects, joined by a comma.</summary>
    public string? HardwareJson { get; init; }

    /// <summary>The contribution's observed capability roles as a JSON array.</summary>
    public string? CapabilitiesJson { get; init; }

    /// <summary>Templates rendered in addition to the minimal ones.</summary>
    public IReadOnlyList<ScaffoldFromCaptureWorkflow.TemplateFile> Templates { get; init; } = [];

    /// <summary>Tokens those templates use.</summary>
    public IReadOnlyDictionary<string, string> Tokens { get; init; } = new Dictionary<string, string>();
}
