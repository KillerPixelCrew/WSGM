using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Contracts.Identity;
using WSGM.Device.Contracts.Packaging;
using WSGM.DeviceLab.Core.Catalog;
using WSGM.DeviceLab.Core.Capture;
using WSGM.DeviceLab.Core.Evidence;
using WSGM.DeviceLab.Core.Preflight;

namespace WSGM.DeviceLab.Core.Scaffolding;

/// <summary>Creates deterministic, fail-closed plugin scaffolds from exact accepted evidence.</summary>
public static class DevicePluginScaffoldGenerator
{
    /// <summary>Current generator identity embedded into output.</summary>
    public const string GeneratorVersion = "wsgm-device-scaffold@1";

    /// <summary>Creates an in-memory plan; no output path or hardware is touched.</summary>
    /// <param name="request">Exact identity, pinned modules, resources, capabilities, and claims.</param>
    /// <returns>Deterministic file plan.</returns>
    public static ScaffoldGenerationPlan Create(ScaffoldGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = Canonicalize(request);
        Validate(request);

        EvidenceLock evidenceLock = EvidenceLockBuilder.Build(
            request.Input.DeviceDefinitionId,
            request.Input.GeneratorVersion,
            request.Claims,
            [.. request.Modules.Select(module => (module.ModuleId, module.Version))]);
        string evidenceJson = DeviceLabJson.Serialize(evidenceLock);
        string evidenceHash = Hash(evidenceJson);
        if (!string.Equals(evidenceHash, request.Input.EvidenceLock.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Frozen scaffold input does not match the current canonical evidence lock.");
        }

        Dictionary<string, EvidenceClaim> claims = request.Claims.ToDictionary(
            claim => claim.ClaimId,
            StringComparer.Ordinal);
        Dictionary<string, string> unavailable = new(StringComparer.Ordinal);
        foreach (ScaffoldCapabilitySelection capability in request.Capabilities)
        {
            EvidenceClaim[] required = [.. capability.RequiredClaimIds.Select(id => claims[id])];
            if (required.Length == 0)
            {
                unavailable[capability.CapabilityId] = "hardware-evidence-missing";
            }
            else if (required.Any(claim => claim.State is not (ClaimState.HardwareVerified or ClaimState.RetailApproved)
                || claim.Counterexamples.Count != 0))
            {
                unavailable[capability.CapabilityId] = "hardware-evidence-incomplete";
            }
            else if (capability.WriteEligibility is WriteEligibility.Quarantined)
            {
                unavailable[capability.CapabilityId] = "resource-quarantined";
            }
        }

        PluginManifest manifest = Manifest(request);
        IReadOnlyList<ManifestValidationError> manifestErrors = PluginManifestValidator.Validate(manifest);
        if (manifestErrors.Count != 0)
        {
            throw new InvalidDataException(string.Join(" ", manifestErrors.Select(error => error.Message)));
        }
        byte[] manifestBytes = PluginManifestReader.ToCanonicalUtf8(manifest);
        List<ScaffoldGeneratedFile> files =
        [
            Generated("plugin.wsgm.json", Encoding.UTF8.GetString(manifestBytes) + "\n"),
            Generated("evidence.lock.json", evidenceJson + "\n"),
            Generated("scaffold-input.json", DeviceLabJson.Serialize(request.Input) + "\n"),
            Generated($"{request.PackageId}.csproj", ProjectFile(request)),
            Generated("Properties/AssemblyInfo.cs", AssemblyInfo(request)),
            Generated("Generated/ExactDetector.g.cs", ExactDetector(request)),
            Generated("Generated/ResourceGraph.g.cs", ResourceGraph(request)),
            Generated("Generated/ModuleComposition.g.cs", ModuleComposition(request)),
            Generated("Generated/Capabilities.g.cs", Capabilities(request, claims, unavailable)),
            Generated("Generated/RecoveryJournal.g.cs", RecoveryJournal(request)),
            Handwritten("PluginLifecycle.cs", LifecycleSkeleton(request)),
            Handwritten("README.md", Readme(request)),
            Generated("BRINGUP.md", BringUpReport(request, unavailable)),
            Generated($"tests/{request.PackageId}.Tests.csproj", TestProject(request)),
            Generated("tests/GeneratedContractTests.cs", GeneratedTests(request, unavailable)),
        ];
        files.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));

        string inputHash = Hash(DeviceLabJson.Serialize(request.Input));
        ScaffoldOutputManifest output = new()
        {
            SchemaVersion = ScaffoldSchema.CurrentVersion,
            InputSha256 = inputHash,
            GeneratorVersion = GeneratorVersion,
            RuntimeApi = request.Input.RuntimeApi,
            Status = ScaffoldStatus.Scaffolded,
            Files = [.. files.Select(file => new ScaffoldOutputFile
            {
                Path = file.Path,
                Ownership = file.Ownership,
                OwnershipMarker = file.Ownership is ScaffoldFileOwnership.Generated
                    ? ScaffoldSchema.GeneratedMarker
                    : ScaffoldSchema.HandwrittenTemplateMarker,
                Sha256 = Hash(file.Content),
            })],
        };
        IReadOnlyList<CaptureValidationError> outputErrors = ScaffoldSchemaValidator.Validate(output);
        if (outputErrors.Count != 0)
        {
            throw new InvalidDataException(string.Join(" ", outputErrors.Select(error => error.Message)));
        }

        return new ScaffoldGenerationPlan
        {
            Input = request.Input,
            EvidenceLock = evidenceLock,
            Files = files,
            Output = output,
            UnavailableCapabilities = unavailable,
        };
    }

    private static void Validate(ScaffoldGenerationRequest request)
    {
        IReadOnlyList<CaptureValidationError> inputErrors = ScaffoldSchemaValidator.Validate(request.Input);
        if (inputErrors.Count != 0)
        {
            throw new InvalidDataException(string.Join(" ", inputErrors.Select(error => error.Message)));
        }

        if (!string.Equals(request.Input.GeneratorVersion, GeneratorVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Frozen input was created for a different generator.");
        }

        ValidateIdentifier(request.PackageId, nameof(request.PackageId), allowDots: true);
        ValidateNamespace(request.RootNamespace);
        ValidateText(request.Identity.SystemManufacturer, "identity manufacturer");
        ValidateText(request.Identity.BaseboardProduct, "identity board");
        ValidateIdentifier(request.Identity.EndpointId, "identity endpoint", allowDots: true);
        if (request.Identity.FirmwareIdentities.Count == 0
            || request.Identity.ProductIds.Count == 0
            || request.Identity.VendorId.Length != 4)
        {
            throw new InvalidDataException("Exact firmware and USB endpoint identifiers are required.");
        }

        Dictionary<string, ScaffoldModuleSelection> modules = Unique(
            request.Modules,
            module => module.ModuleId,
            "module");
        if (modules.Count != request.Input.ModuleLocks.Count
            || request.Input.ModuleLocks.Any(module =>
                !modules.TryGetValue(module.ModuleId, out ScaffoldModuleSelection? selected)
                || selected.Version != module.Version))
        {
            throw new InvalidDataException("Selected modules must exactly match the frozen version pins.");
        }

        Dictionary<string, ScaffoldResourceSelection> resources = Unique(
            request.Resources,
            resource => resource.ResourceId,
            "resource");
        Dictionary<string, ScaffoldCapabilitySelection> capabilities = Unique(
            request.Capabilities,
            capability => capability.CapabilityId,
            "capability");
        Dictionary<string, EvidenceClaim> claims = Unique(
            request.Claims,
            claim => claim.ClaimId,
            "claim");
        foreach (ScaffoldCapabilitySelection capability in capabilities.Values)
        {
            if (!resources.ContainsKey(capability.ResourceId)
                || capability.RequiredClaimIds.Any(id => !claims.ContainsKey(id)))
            {
                throw new InvalidDataException($"Capability '{capability.CapabilityId}' has an unresolved resource or claim.");
            }
        }

        foreach (EvidenceClaim claim in claims.Values)
        {
            if (!string.Equals(claim.Scope.BaseboardProduct, request.Identity.BaseboardProduct, StringComparison.OrdinalIgnoreCase)
                || claim.State is ClaimState.Rejected)
            {
                throw new InvalidDataException($"Claim '{claim.ClaimId}' is rejected or belongs to another board.");
            }

            if (claim.Scope.BiosVersion is { Length: > 0 } firmware
                && !request.Identity.FirmwareIdentities.Contains(firmware, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"Claim '{claim.ClaimId}' belongs to unselected firmware '{firmware}'.");
            }
        }
    }

    private static ScaffoldGenerationRequest Canonicalize(ScaffoldGenerationRequest request) => request with
    {
        Input = request.Input with
        {
            ModuleLocks = [.. request.Input.ModuleLocks.OrderBy(module => module.ModuleId, StringComparer.Ordinal)],
            FixtureIds = [.. request.Input.FixtureIds.Order(StringComparer.Ordinal)],
        },
        Identity = request.Identity with
        {
            FirmwareIdentities = [.. request.Identity.FirmwareIdentities.Order(StringComparer.Ordinal)],
            ProductIds = [.. request.Identity.ProductIds.Order(StringComparer.Ordinal)],
        },
        Modules = [.. request.Modules.OrderBy(module => module.ModuleId, StringComparer.Ordinal)],
        Resources = [.. request.Resources
            .OrderBy(resource => resource.ResourceId, StringComparer.Ordinal)
            .Select(resource => resource with
            {
                RecoveryJournalFields = [.. resource.RecoveryJournalFields.Order(StringComparer.Ordinal)],
            })],
        Capabilities = [.. request.Capabilities
            .OrderBy(capability => capability.CapabilityId, StringComparer.Ordinal)
            .Select(capability => capability with
            {
                RequiredClaimIds = [.. capability.RequiredClaimIds.Order(StringComparer.Ordinal)],
            })],
        Claims = [.. request.Claims.OrderBy(claim => claim.ClaimId, StringComparer.Ordinal)],
    };

    private static PluginManifest Manifest(ScaffoldGenerationRequest request) => new()
    {
        SchemaVersion = PluginManifestValidator.MaxSupportedSchemaVersion,
        Id = request.PackageId,
        Version = "0.1.0",
        DisplayName = request.DisplayName,
        Publisher = request.Publisher,
        MinApiVersion = request.Input.RuntimeApi.MinimumVersion,
        MaxApiVersion = request.Input.RuntimeApi.MaximumVersion,
        EntryPoint = $"{request.RootNamespace}.dll",
        Devices =
        [
            new DeviceDefinition
            {
                Id = request.Input.DeviceDefinitionId,
                DisplayName = request.DisplayName,
                Identity =
                [
                    Required(IdentitySignal.SmbiosSystemManufacturer, request.Identity.SystemManufacturer),
                    Required(IdentitySignal.SmbiosBaseboardProduct, request.Identity.BaseboardProduct),
                    Required(IdentitySignal.BiosVersion, request.Identity.FirmwareIdentities),
                    Required(IdentitySignal.UsbVendorId, [request.Identity.VendorId], request.Identity.EndpointId),
                    Required(IdentitySignal.UsbProductId, request.Identity.ProductIds, request.Identity.EndpointId),
                ],
                UsbEndpoints =
                [
                    new UsbEndpointDeclaration
                    {
                        Id = request.Identity.EndpointId,
                        Role = request.Identity.EndpointRole,
                        VendorId = request.Identity.VendorId,
                        ProductIds = request.Identity.ProductIds,
                    },
                ],
                Resources = [.. request.Resources
                    .OrderBy(resource => resource.ResourceId, StringComparer.Ordinal)
                    .Select(resource => new ResourceDeclaration
                    {
                        Id = resource.ResourceId,
                        Kind = resource.Kind,
                        // A generated Developer scaffold never receives write authority. Exact
                        // verified operations are added later by reviewed handwritten code.
                        Access = ResourceAccess.Read,
                        EndpointId = resource.EndpointId,
                    })],
                Modules = [.. request.Modules
                    .OrderBy(module => module.ModuleId, StringComparer.Ordinal)
                    .Select(module => new ModuleReference
                    {
                        Id = module.ModuleId,
                        Version = module.Version,
                        Layer = module.Layer,
                    })],
                Capabilities = [.. request.Capabilities
                    .Select(capability => capability.CapabilityId)
                    .Order(StringComparer.Ordinal)],
            },
        ],
        Provenance = new PackageProvenance
        {
            Source = "WSGM Device Lab scaffold from sanitized capture and evidence lock",
            License = "GPL-3.0-or-later",
            ProvenanceClass = ProvenanceClass.IndependentCapture,
        },
    };

    private static IdentityObservation Required(
        IdentitySignal signal,
        string value,
        string? endpointId = null) => Required(signal, [value], endpointId);

    private static IdentityObservation Required(
        IdentitySignal signal,
        IReadOnlyList<string> values,
        string? endpointId = null) => new()
    {
        Signal = signal,
        Strength = IdentityStrength.Required,
        Values = values,
        EndpointId = endpointId,
    };

    private static ScaffoldGeneratedFile Generated(string path, string content) => new()
    {
        Path = path,
        Ownership = ScaffoldFileOwnership.Generated,
        Content = Normalize(content),
    };

    private static ScaffoldGeneratedFile Handwritten(string path, string content) => new()
    {
        Path = path,
        Ownership = ScaffoldFileOwnership.HandwrittenTemplate,
        Content = Normalize(content),
    };

    private static string ProjectFile(ScaffoldGenerationRequest request) => $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0-windows</TargetFramework>
            <LangVersion>latest</LangVersion>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <AssemblyName>{{request.RootNamespace}}</AssemblyName>
            <RootNamespace>{{request.RootNamespace}}</RootNamespace>
          </PropertyGroup>
          <ItemGroup>
            <ProjectReference Include="$(WsgmRepositoryRoot)\src\WSGM.Device.Sdk\WSGM.Device.Sdk.csproj" Condition="'$(WsgmRepositoryRoot)' != ''" />
            <ProjectReference Include="..\..\src\WSGM.Device.Sdk\WSGM.Device.Sdk.csproj" Condition="'$(WsgmRepositoryRoot)' == ''" />
          </ItemGroup>
          <ItemGroup>
            <Compile Remove="tests\**\*.cs" />
          </ItemGroup>
        </Project>
        """;

    private static string AssemblyInfo(ScaffoldGenerationRequest request) => $$"""
        // {{ScaffoldSchema.GeneratedMarker}}
        using System.Runtime.CompilerServices;

        [assembly: InternalsVisibleTo("{{request.PackageId}}.Tests")]
        """;

    private static string ExactDetector(ScaffoldGenerationRequest request) => $$"""
        // {{ScaffoldSchema.GeneratedMarker}}
        namespace {{request.RootNamespace}}.Generated;

        internal static class ExactDetector
        {
            internal static readonly string[] Firmware = [{{Literals(request.Identity.FirmwareIdentities)}}];
            internal static readonly string[] ProductIds = [{{Literals(request.Identity.ProductIds)}}];

            internal static bool Matches(string manufacturer, string board, string firmware) =>
                string.Equals(manufacturer.Trim(), "{{Escape(request.Identity.SystemManufacturer)}}", StringComparison.OrdinalIgnoreCase)
                && string.Equals(board.Trim(), "{{Escape(request.Identity.BaseboardProduct)}}", StringComparison.OrdinalIgnoreCase)
                && Firmware.Contains(firmware, StringComparer.Ordinal);

            internal static bool MatchesEndpoint(string endpointId, string vendorId, string productId) =>
                string.Equals(endpointId, "{{Escape(request.Identity.EndpointId)}}", StringComparison.Ordinal)
                && string.Equals(vendorId, "{{Escape(request.Identity.VendorId)}}", StringComparison.OrdinalIgnoreCase)
                && ProductIds.Contains(productId, StringComparer.OrdinalIgnoreCase);
        }
        """;

    private static string ResourceGraph(ScaffoldGenerationRequest request)
    {
        string rows = string.Join(",\n", request.Resources
            .OrderBy(resource => resource.ResourceId, StringComparer.Ordinal)
            .Select(resource => $"        new(\"{Escape(resource.ResourceId)}\", \"{resource.Kind}\", \"Read\", {NullableLiteral(resource.EndpointId)})"));
        return $$"""
            // {{ScaffoldSchema.GeneratedMarker}}
            namespace {{request.RootNamespace}}.Generated;

            internal sealed record ResourceNode(string Id, string Kind, string Access, string? EndpointId);

            internal static class ResourceGraph
            {
                internal static readonly ResourceNode[] All =
                [
            {{rows}}
                ];
            }
            """;
    }

    private static string ModuleComposition(ScaffoldGenerationRequest request)
    {
        string rows = string.Join(",\n", request.Modules
            .OrderBy(module => module.ModuleId, StringComparer.Ordinal)
            .Select(module => $"        new(\"{Escape(module.ModuleId)}\", {module.Version}, \"{module.Layer}\")"));
        return $$"""
            // {{ScaffoldSchema.GeneratedMarker}}
            namespace {{request.RootNamespace}}.Generated;

            internal sealed record ModuleLock(string Id, int Version, string Layer);

            internal static class ModuleComposition
            {
                internal static readonly ModuleLock[] All =
                [
            {{rows}}
                ];
            }
            """;
    }

    private static string Capabilities(
        ScaffoldGenerationRequest request,
        IReadOnlyDictionary<string, EvidenceClaim> claims,
        IReadOnlyDictionary<string, string> unavailable)
    {
        StringBuilder methods = new();
        List<string> rows = [];
        foreach (ScaffoldCapabilitySelection capability in request.Capabilities.OrderBy(
            capability => capability.CapabilityId,
            StringComparer.Ordinal))
        {
            bool available = !unavailable.ContainsKey(capability.CapabilityId);
            string reason = available ? "available" : unavailable[capability.CapabilityId];
            EvidenceClaim? parseClaim = capability.RequiredClaimIds
                .Select(id => claims[id])
                .FirstOrDefault(claim => capability.GenerateParser
                    && available
                    && claim.Offset is not null
                    && claim.WidthBits is > 0 and <= 32);
            string method = "null";
            if (parseClaim is not null)
            {
                method = $"Parse_{Identifier(capability.CapabilityId)}";
                int offset = parseClaim.Offset!.Value;
                int widthBytes = (parseClaim.WidthBits!.Value + 7) / 8;
                uint mask = parseClaim.Mask ?? uint.MaxValue;
                methods.AppendLine($"    internal static int {method}(ReadOnlySpan<byte> response)");
                methods.AppendLine("    {");
                methods.AppendLine($"        if (response.Length < {offset + widthBytes}) throw new ArgumentException(\"Response is shorter than verified parser shape.\", nameof(response));");
                methods.AppendLine("        uint value = 0;");
                for (int index = 0; index < widthBytes; index++)
                {
                    int shift = parseClaim.Endian is Endianness.Big
                        ? (widthBytes - 1 - index) * 8
                        : index * 8;
                    methods.AppendLine($"        value |= (uint)response[{offset + index}] << {shift};");
                }

                methods.AppendLine($"        return checked((int)(value & 0x{mask:x8}u));");
                methods.AppendLine("    }");
                methods.AppendLine();
            }

            rows.Add($"        new(\"{Escape(capability.CapabilityId)}\", \"{Escape(capability.ResourceId)}\", {available.ToString().ToLowerInvariant()}, \"{reason}\", {method})");
        }

        return $$"""
            // {{ScaffoldSchema.GeneratedMarker}}
            namespace {{request.RootNamespace}}.Generated;

            internal delegate int VerifiedParser(ReadOnlySpan<byte> response);
            internal sealed record CapabilityRegistration(string Id, string ResourceId, bool Available, string Reason, VerifiedParser? Parser);

            internal static class CapabilityRegistrations
            {
                internal static readonly CapabilityRegistration[] All =
                [
            {{string.Join(",\n", rows)}}
                ];

            {{methods.ToString().TrimEnd()}}
            }
            """;
    }

    private static string RecoveryJournal(ScaffoldGenerationRequest request)
    {
        string rows = string.Join(",\n", request.Resources
            .OrderBy(resource => resource.ResourceId, StringComparer.Ordinal)
            .Select(resource => $"        new(\"{Escape(resource.ResourceId)}\", [{Literals(resource.RecoveryJournalFields.Order(StringComparer.Ordinal).ToArray())}])"));
        return $$"""
            // {{ScaffoldSchema.GeneratedMarker}}
            namespace {{request.RootNamespace}}.Generated;

            internal sealed record RecoveryJournalShape(string ResourceId, string[] RequiredFields);

            internal static class RecoveryJournalShapes
            {
                internal static readonly RecoveryJournalShape[] All =
                [
            {{rows}}
                ];
            }
            """;
    }

    private static string LifecycleSkeleton(ScaffoldGenerationRequest request) => $$"""
        // {{ScaffoldSchema.HandwrittenTemplateMarker}}
        using {{request.RootNamespace}}.Generated;

        namespace {{request.RootNamespace}};

        internal enum ScaffoldCommandOutcome { Unavailable }

        internal sealed class PluginLifecycle
        {
            internal bool Detect(string manufacturer, string board, string firmware) =>
                ExactDetector.Matches(manufacturer, board, firmware);

            internal ValueTask ActivateAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Register only Generated.CapabilityRegistrations. Unverified entries stay unavailable.
                return ValueTask.CompletedTask;
            }

            internal ValueTask<ScaffoldCommandOutcome> ExecuteCommandAsync(string capabilityId, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // No hardware setter is scaffolded from similarity. Add reviewed handwritten code later.
                return ValueTask.FromResult(ScaffoldCommandOutcome.Unavailable);
            }

            internal ValueTask DeactivateAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Restore each resource from its generated journal shape before releasing its lease.
                return ValueTask.CompletedTask;
            }
        }
        """;

    private static string Readme(ScaffoldGenerationRequest request) => $$"""
        <!-- {{ScaffoldSchema.HandwrittenTemplateMarker}} -->
        # {{request.DisplayName}}

        Status: **Scaffolded / Developer**.

        This project was generated from a sanitized capture and an evidence lock. It is not trusted,
        privileged, hardware verified, retail approved, or supported. Document exact supported
        firmware, dependencies, hardware verification, and recovery evidence before requesting review.
        """;

    private static string BringUpReport(
        ScaffoldGenerationRequest request,
        IReadOnlyDictionary<string, string> unavailable)
    {
        string missing = unavailable.Count == 0
            ? "- None; all generated parsers still require review."
            : string.Join("\n", unavailable.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"- `{pair.Key}`: `{pair.Value}`"));
        return $$"""
            <!-- {{ScaffoldSchema.GeneratedMarker}} -->
            # Bring-up report

            - Device definition: `{{request.Input.DeviceDefinitionId}}`
            - Board: `{{request.Identity.BaseboardProduct}}`
            - Firmware: `{{string.Join("`, `", request.Identity.FirmwareIdentities.Order(StringComparer.Ordinal))}}`
            - Source capture SHA-256: `{{request.Input.SourceCaptureSha256}}`
            - Status: **Scaffolded / Developer only**

            ## Unavailable or incomplete capabilities

            {{missing}}

            Generation grants no package trust, privilege, hardware verification, or retail approval.
            Similarity never generated power limits, fan conversion, RGB offsets, charge policy,
            persistent writes, firmware synchronization, rollback code, or placeholder setters.
            """;
    }

    private static string TestProject(ScaffoldGenerationRequest request) => $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0-windows</TargetFramework>
            <IsTestProject>true</IsTestProject>
            <IsPackable>false</IsPackable>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <AssemblyName>{{request.PackageId}}.Tests</AssemblyName>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.8.1" />
            <PackageReference Include="xunit" Version="2.9.3" />
            <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" PrivateAssets="all" />
          </ItemGroup>
          <ItemGroup>
            <ProjectReference Include="..\{{request.PackageId}}.csproj" />
          </ItemGroup>
        </Project>
        """;

    private static string GeneratedTests(
        ScaffoldGenerationRequest request,
        IReadOnlyDictionary<string, string> unavailable)
    {
        string unavailableAssertion = unavailable.Count == 0
            ? "Assert.All(CapabilityRegistrations.All, capability => Assert.True(capability.Available));"
            : $"Assert.Contains(CapabilityRegistrations.All, capability => capability.Id == \"{Escape(unavailable.Keys.Order(StringComparer.Ordinal).First())}\" && !capability.Available);";
        return $$"""
            // {{ScaffoldSchema.GeneratedMarker}}
            using {{request.RootNamespace}}.Generated;
            using Xunit;

            namespace {{request.RootNamespace}}.Tests;

            public class GeneratedContractTests
            {
                [Fact]
                public void Detection_RequiresExactBoardAndKnownFirmware()
                {
                    Assert.True(ExactDetector.Matches("{{Escape(request.Identity.SystemManufacturer)}}", "{{Escape(request.Identity.BaseboardProduct)}}", "{{Escape(request.Identity.FirmwareIdentities[0])}}"));
                    Assert.False(ExactDetector.Matches("{{Escape(request.Identity.SystemManufacturer)}}", "wrong-board", "{{Escape(request.Identity.FirmwareIdentities[0])}}"));
                    Assert.False(ExactDetector.Matches("{{Escape(request.Identity.SystemManufacturer)}}", "{{Escape(request.Identity.BaseboardProduct)}}", "unknown-firmware"));
                }

                [Fact]
                public void EndpointBinding_RequiresExactEndpointVidAndPid()
                {
                    Assert.True(ExactDetector.MatchesEndpoint("{{Escape(request.Identity.EndpointId)}}", "{{Escape(request.Identity.VendorId)}}", "{{Escape(request.Identity.ProductIds[0])}}"));
                    Assert.False(ExactDetector.MatchesEndpoint("wrong", "{{Escape(request.Identity.VendorId)}}", "{{Escape(request.Identity.ProductIds[0])}}"));
                }

                [Fact]
                public void CapabilitySnapshot_PreservesUnavailableReason()
                {
                    Assert.Equal({{request.Capabilities.Count}}, CapabilityRegistrations.All.Length);
                    {{unavailableAssertion}}
                }

                [Fact]
                public async Task CommandIntent_HasNoGeneratedHardwareSetter()
                {
                    PluginLifecycle plugin = new();
                    Assert.Equal(ScaffoldCommandOutcome.Unavailable, await plugin.ExecuteCommandAsync("candidate", CancellationToken.None));
                }

                [Fact]
                public void CaptureReplay_UsesOnlyGeneratedVerifiedParserWhenPresent()
                {
                    Assert.All(CapabilityRegistrations.All, capability =>
                        Assert.True(capability.Parser is null || capability.Available));
                }
            }
            """;
    }

    private static Dictionary<string, T> Unique<T>(
        IReadOnlyList<T> values,
        Func<T, string> key,
        string label)
    {
        Dictionary<string, T> result = new(StringComparer.Ordinal);
        foreach (T value in values)
        {
            string id = key(value);
            if (string.IsNullOrWhiteSpace(id) || !result.TryAdd(id, value))
            {
                throw new InvalidDataException($"Every {label} ID must be nonempty and unique.");
            }
        }

        return result;
    }

    private static void ValidateIdentifier(string value, string label, bool allowDots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, label);
        if (value.Length > CaptureSchema.MaximumIdentifierLength
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_'
                || allowDots && (character == '.' || character == ' '))))
        {
            throw new InvalidDataException($"{label} contains unsupported characters.");
        }
    }

    private static void ValidateText(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > CaptureSchema.MaximumTextLength
            || value.Any(char.IsControl))
        {
            throw new InvalidDataException($"{label} must be bounded printable text.");
        }
    }

    private static void ValidateNamespace(string value)
    {
        foreach (string part in value.Split('.'))
        {
            if (part.Length == 0
                || !(char.IsLetter(part[0]) || part[0] == '_')
                || part.Skip(1).Any(character => !(char.IsLetterOrDigit(character) || character == '_')))
            {
                throw new InvalidDataException("Root namespace must contain valid identifier segments.");
            }
        }
    }

    private static string Identifier(string value)
    {
        StringBuilder result = new();
        foreach (char character in value)
        {
            result.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        if (result.Length == 0 || char.IsDigit(result[0]))
        {
            result.Insert(0, '_');
        }

        return result.ToString();
    }

    private static string Literals(IReadOnlyList<string> values) =>
        string.Join(", ", values.Select(value => $"\"{Escape(value)}\""));

    private static string NullableLiteral(string? value) => value is null
        ? "null"
        : $"\"{Escape(value)}\"";

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string Normalize(string content) => content
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n')
        .TrimStart('\n')
        .TrimEnd() + "\n";

    private static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}

/// <summary>Writes a scaffold plan once and never overwrites an existing path.</summary>
public static class DevicePluginScaffoldWriter
{
    /// <summary>Writes every planned file and the output manifest under one new explicit directory.</summary>
    /// <param name="plan">Validated in-memory plan.</param>
    /// <param name="outputDirectory">New destination directory.</param>
    /// <param name="boundaries">Protected environment paths.</param>
    public static void Write(
        ScaffoldGenerationPlan plan,
        string outputDirectory,
        DeviceLabPathBoundaries boundaries)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(boundaries);
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
        {
            throw new IOException("Scaffold output must be a new directory; regeneration never overwrites developer files.");
        }

        DeviceLabOutputPathDecision decision = DeviceLabOutputPathPolicy.Evaluate(
            outputDirectory,
            DeviceLabOutputTargetKind.Directory,
            boundaries);
        if (!decision.IsAllowed || decision.FullPath is null)
        {
            throw new IOException(decision.Reason ?? "Scaffold output path was rejected.");
        }

        Directory.CreateDirectory(decision.FullPath);
        foreach (ScaffoldGeneratedFile file in plan.Files)
        {
            string fullPath = Path.GetFullPath(Path.Combine(decision.FullPath, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(decision.FullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Scaffold file escaped its output directory.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            using FileStream stream = new(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] content = Encoding.UTF8.GetBytes(file.Content);
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }

        string manifestPath = Path.Combine(decision.FullPath, "scaffold-output.json");
        using FileStream manifest = new(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] bytes = Encoding.UTF8.GetBytes(DeviceLabJson.Serialize(plan.Output) + "\n");
        manifest.Write(bytes);
        manifest.Flush(flushToDisk: true);
    }
}

/// <summary>Plans regeneration changes without mutating generated or handwritten files.</summary>
public static class DevicePluginScaffoldRegeneration
{
    /// <summary>Compares evidence, fixtures, and generated content; handwritten files are ignored.</summary>
    /// <param name="previous">Existing accepted plan.</param>
    /// <param name="current">New plan.</param>
    /// <returns>Semantic review requirements.</returns>
    public static ScaffoldRegenerationReview Compare(
        ScaffoldGenerationPlan previous,
        ScaffoldGenerationPlan current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        IReadOnlyList<EvidenceLockChange> evidence = EvidenceLockBuilder.Diff(
            previous.EvidenceLock,
            current.EvidenceLock);
        string[] fixtures = [.. previous.Input.FixtureIds
            .Except(current.Input.FixtureIds, StringComparer.Ordinal)
            .Concat(current.Input.FixtureIds.Except(previous.Input.FixtureIds, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)];
        Dictionary<string, ScaffoldGeneratedFile> before = previous.Files
            .Where(file => file.Ownership is ScaffoldFileOwnership.Generated)
            .ToDictionary(file => file.Path, StringComparer.Ordinal);
        string[] changed = [.. current.Files
            .Where(file => file.Ownership is ScaffoldFileOwnership.Generated)
            .Where(file => !before.TryGetValue(file.Path, out ScaffoldGeneratedFile? old)
                || !string.Equals(old.Content, file.Content, StringComparison.Ordinal))
            .Select(file => file.Path)
            .Concat(before.Keys.Except(current.Files.Select(file => file.Path), StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

        return new ScaffoldRegenerationReview
        {
            EvidenceChanges = evidence,
            FixtureChanges = fixtures,
            GeneratedFileChanges = changed,
            RequiresExplicitReview = fixtures.Length != 0
                || !EvidenceLockBuilder.MayAcceptWithoutReview(evidence),
        };
    }
}

/// <summary>Offline clean-directory build and fixture-test verifier for one generated scaffold.</summary>
public static class DevicePluginScaffoldVerifier
{
    /// <summary>Runs generated offline tests without activating a plugin or opening hardware.</summary>
    /// <param name="scaffoldDirectory">Written scaffold root.</param>
    /// <param name="repositoryRoot">Repository providing Contracts and SDK project references.</param>
    /// <param name="packageId">Generated package ID.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <returns>True only when the generated test project exits successfully.</returns>
    public static async Task<bool> VerifyAsync(
        string scaffoldDirectory,
        string repositoryRoot,
        string packageId,
        CancellationToken cancellationToken)
    {
        string testProject = Path.Combine(scaffoldDirectory, "tests", $"{packageId}.Tests.csproj");
        ProcessStartInfo start = new()
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("test");
        start.ArgumentList.Add(testProject);
        start.ArgumentList.Add("--nologo");
        start.ArgumentList.Add($"-p:WsgmRepositoryRoot={Path.GetFullPath(repositoryRoot)}");
        using Process process = new() { StartInfo = start };
        if (!process.Start())
        {
            return false;
        }

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return false;
        }

        _ = await standardOutput.ConfigureAwait(false);
        _ = await standardError.ConfigureAwait(false);
        return process.ExitCode == 0;
    }
}
