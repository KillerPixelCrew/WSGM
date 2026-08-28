using System.Reflection;
using System.Runtime.InteropServices;
using WSGM.Device.Contracts.Ipc;

namespace WSGM.Device.Contracts.Tests;

/// <summary>
/// The gate that keeps the frozen contract frozen: no device-specific knowledge leaks into it, every
/// public surface is documented, and the protocol version and fingerprint move only deliberately.
/// </summary>
public class ContractFreezeTests
{
    [Fact]
    public void TheProtocolVersionAndFingerprintAreTheFrozenValues()
    {
        // Changing either is a compatibility event, not an implementation detail. This test is the
        // reminder that DeviceHost, the SDK, the scaffold generator, the validator, the packer, and
        // every published plugin target these exact values.
        Assert.Equal(1, DeviceProtocol.MinSupportedVersion);
        Assert.Equal(1, DeviceProtocol.MaxSupportedVersion);
        Assert.Equal("wsgm-device-v2", DeviceProtocol.SchemaFingerprint);
    }

    [Fact]
    public void TheContractsAssembly_ContainsNoDeviceSpecificConstant()
    {
        // WSGM must be able to express "sustained power limit, 8-30 W, step 1" without knowing that
        // one device reaches it through a named WMI method and another through an EC transaction. A
        // vendor method name, WMI namespace, ACPI device, or USB identifier appearing as a value here
        // means device knowledge has crossed into the semantic layer.
        string[] deviceSpecific =
        [
            "MSI_", "root\\WMI", "root/WMI", "ACPI\\", "VID_", "PID_",
            "MS-1T", "PawnIO", "DeviceIoControl", "\\\\.\\",
        ];

        List<string> offenders = [];

        foreach (Type type in typeof(DeviceProtocol).Assembly.GetTypes())
        {
            foreach (FieldInfo field in type.GetFields(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            {
                // IsLiteral first: GetRawConstantValue throws for a static readonly field, which is
                // most of them.
                if (!field.IsLiteral || field.GetRawConstantValue() is not string value)
                {
                    continue;
                }

                foreach (string token in deviceSpecific)
                {
                    if (value.Contains(token, StringComparison.OrdinalIgnoreCase))
                    {
                        offenders.Add($"{type.Name}.{field.Name} = \"{value}\"");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0,
            $"Device-specific values in the semantic contract: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void EveryPublicTypeIsDocumented()
    {
        // The compiler already enforces this - CS1591 is on and the Release build treats warnings as
        // errors - so this asserts the XML file is actually produced and shipped, which is what a
        // plugin author consumes in their IDE.
        Assembly contracts = typeof(DeviceProtocol).Assembly;
        string xmlPath = Path.ChangeExtension(contracts.Location, ".xml");

        Assert.True(File.Exists(xmlPath), $"No XML documentation produced at {xmlPath}.");

        string documentation = File.ReadAllText(xmlPath);

        // Nested types emitted by the JSON source generator are excluded: they are generated
        // plumbing that no plugin author writes against, and the generator does not document them.
        // The generator's own entry point is hand-written and is checked like everything else.
        IEnumerable<Type> authored = contracts.GetExportedTypes()
            .Where(t => !t.IsNested);

        foreach (Type type in authored)
        {
            Assert.Contains($"T:{type.FullName}", documentation, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NoPublicTypeExposesARawPointerOrHandle()
    {
        // A handle crossing the boundary would hand a peer the means to act on hardware directly,
        // which is precisely what the semantic contract exists to avoid. Lease types name resources;
        // they never carry one.
        List<string> offenders = [];

        foreach (Type type in typeof(DeviceProtocol).Assembly.GetExportedTypes())
        {
            foreach (PropertyInfo property in type.GetProperties(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Type propertyType = property.PropertyType;

                if (propertyType.IsPointer
                    || propertyType == typeof(IntPtr)
                    || propertyType == typeof(UIntPtr)
                    || typeof(SafeHandle).IsAssignableFrom(propertyType)
                    || typeof(System.IO.Stream).IsAssignableFrom(propertyType))
                {
                    offenders.Add($"{type.Name}.{property.Name}: {propertyType.Name}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            $"Raw handles or streams on the contract surface: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheContractsAssemblyHasNoDependencyBeyondTheBcl()
    {
        // Anything referenced here is compiled into WSGM's NativeAOT image, so the dependency set is
        // WSGM's dependency set. Kept empty rather than carefully chosen.
        string[] referenced = typeof(DeviceProtocol).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => !name.StartsWith("System.", StringComparison.Ordinal)
                && !string.Equals(name, "System", StringComparison.Ordinal)
                && !string.Equals(name, "netstandard", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(referenced);
    }
}
