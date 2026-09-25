using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Threading;

namespace WSGM.DeviceLab.Wizard;

/// <summary>One WMI class in <c>root\wmi</c>.</summary>
internal sealed record LabWmiClass
{
    /// <summary>Class name.</summary>
    public required string Name { get; init; }

    /// <summary>Base class chain, nearest first.</summary>
    public IReadOnlyList<string> Derivation { get; init; } = [];

    /// <summary>The WMI data block GUID, for classes an ACPI or driver provider backs.</summary>
    public string? Guid { get; init; }

    /// <summary>Method names; never invoked.</summary>
    public IReadOnlyList<string> Methods { get; init; } = [];

    /// <summary>Whether the class is abstract, an event class or otherwise has no instances to count.</summary>
    public string? NotCounted { get; init; }

    /// <summary>Instance count, capped at <see cref="LabSystemDump.MaximumWmiInstances" />.</summary>
    public int? Instances { get; init; }

    /// <summary>Why the instances could not be counted.</summary>
    public string? Problem { get; init; }
}

internal static partial class LabSystemDump
{
    /// <summary>Instance counts stop here; a count at the cap means "at least".</summary>
    public const int MaximumWmiInstances = 64;

    private const int MaximumWmiClasses = 2000;
    private const int MaximumWmiRows = 64;
    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WmiSectionBudget = TimeSpan.FromSeconds(120);

    private static LabSystemDumpSectionResult CollectWmi(LabSystemDumpContext context)
    {
        List<string> issues = [];
        List<LabWmiClass> classes = [];
        var scope = Scope("root\\wmi");
        var clock = Stopwatch.StartNew();
        using (ManagementObjectSearcher searcher = new(scope, new ObjectQuery("SELECT * FROM meta_class"), Enumeration()))
        {
            foreach (var item in searcher.Get())
            {
                using (item)
                {
                    context.Cancellation.ThrowIfCancellationRequested();
                    if (classes.Count >= MaximumWmiClasses)
                    {
                        AddIssue(issues, $"Only the first {MaximumWmiClasses} classes were listed.");
                        break;
                    }

                    if (item is not ManagementClass definition
                        || Text(definition, "__CLASS") is not { } name
                        || name.StartsWith("__", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    classes.Add(DescribeClass(definition, name));
                }
            }
        }

        // Instances are counted only; no instance data is read. The whole count has a time budget,
        // because a slow ACPI provider can take seconds per class.
        for (var index = 0; index < classes.Count; index++)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var entry = classes[index];
            if (entry.NotCounted is not null)
            {
                continue;
            }

            if (clock.Elapsed > WmiSectionBudget)
            {
                classes[index] = entry with { Problem = "not counted: time limit reached" };
                continue;
            }

            try
            {
                classes[index] = entry with { Instances = CountInstances(scope, entry.Name, context.Cancellation) };
            }
            catch (Exception ex) when (ex is ManagementException or TimeoutException or UnauthorizedAccessException
                                           or System.Runtime.InteropServices.COMException)
            {
                classes[index] = entry with { Problem = ex.Message.Trim() };
            }
        }

        if (clock.Elapsed > WmiSectionBudget)
        {
            AddIssue(issues, "Counting stopped at the time limit; some classes were not counted.");
        }

        context.WmiClasses = classes;
        var presence = VendorClassPresence(classes, issues);
        context.Write("wmi", new { Namespace = "root\\wmi", VendorClasses = presence, Classes = classes, Issues = issues });
        return Result("wmi", classes.Count, Plural(classes.Count, "class", "classes"), issues);
    }

    /// <summary>
    ///     Reports whether the vendor classes device plugins use are present: <c>MSI_ACPI</c>, every
    ///     <c>LENOVO_*</c> class and <c>SuRwECRegInterface</c>.
    /// </summary>
    /// <param name="classes">The <c>root\wmi</c> class list.</param>
    /// <returns>One entry per class looked for; a <c>LENOVO_*</c> entry is always present.</returns>
    public static List<object> VendorClassPresence(IReadOnlyList<LabWmiClass> classes)
    {
        List<object> presence = [];
        foreach (var name in (string[])["MSI_ACPI", "SuRwECRegInterface"])
        {
            var found = classes.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
            presence.Add(new { Name = name, Present = found is not null, found?.Methods, found?.Instances });
        }

        var lenovo = classes.Where(entry => entry.Name.StartsWith("LENOVO_", StringComparison.OrdinalIgnoreCase)).ToList();
        presence.Add(new { Name = "LENOVO_*", Present = lenovo.Count > 0, Classes = lenovo.Select(entry => entry.Name).ToList() });
        return presence;
    }

    // Also looks in root\cimv2 for a vendor class root\wmi does not have.
    private static List<object> VendorClassPresence(IReadOnlyList<LabWmiClass> classes, List<string> issues)
    {
        var presence = VendorClassPresence(classes);
        foreach (var name in (string[])["MSI_ACPI", "SuRwECRegInterface"])
        {
            if (classes.Any(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            try
            {
                using ManagementClass definition = new(Scope("root\\cimv2"), new ManagementPath(name), null);
                definition.Get();
                presence.Add(new { Name = name, Present = true, Namespace = "root\\cimv2" });
            }
            catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.NotFound)
            {
                // Absent from both namespaces; the root\wmi entry already says so.
            }
            catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException
                                           or System.Runtime.InteropServices.COMException)
            {
                AddIssue(issues, $@"root\cimv2 {name}: {ex.Message.Trim()}");
            }
        }

        return presence;
    }

    private static LabWmiClass DescribeClass(ManagementClass definition, string name)
    {
        var derivation = definition.Derivation?.Cast<string>().ToList() ?? [];
        List<string> methods = [];
        try
        {
            methods.AddRange(definition.Methods.Cast<MethodData>().Select(method => method.Name));
        }
        catch (ManagementException)
        {
            // Some providers refuse method metadata; the class is still listed.
        }

        var isAbstract = Qualifier(definition, "abstract") is true;
        var isEvent = derivation.Any(parent =>
            parent is "__Event" or "__ExtrinsicEvent" or "WMIEvent" or "MSWmi_Event");
        return new LabWmiClass
        {
            Name = name,
            Derivation = derivation,
            Guid = Qualifier(definition, "guid")?.ToString(),
            Methods = methods,
            NotCounted = isAbstract ? "abstract" : isEvent ? "event class" : null
        };
    }

    private static int CountInstances(ManagementScope scope, string className, CancellationToken cancellation)
    {
        var count = 0;
        using ManagementObjectSearcher searcher = new(scope, new ObjectQuery($"SELECT * FROM {className}"), Enumeration());
        foreach (var item in searcher.Get())
        {
            item.Dispose();
            cancellation.ThrowIfCancellationRequested();
            if (++count >= MaximumWmiInstances)
            {
                break;
            }
        }

        return count;
    }

    /// <summary>Reads allowed properties of the instances of one class.</summary>
    /// <param name="ns">Namespace.</param>
    /// <param name="className">Class name.</param>
    /// <param name="allowed">The only properties copied; everything else, serials included, is ignored.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>One dictionary per instance.</returns>
    private static List<Dictionary<string, object?>> WmiRows(
        string ns,
        string className,
        IReadOnlyCollection<string> allowed,
        CancellationToken cancellation)
    {
        List<Dictionary<string, object?>> rows = [];
        using ManagementObjectSearcher searcher = new(Scope(ns), new ObjectQuery($"SELECT * FROM {className}"), Enumeration());
        foreach (var item in searcher.Get())
        {
            using (item)
            {
                cancellation.ThrowIfCancellationRequested();
                Dictionary<string, object?> row = new(StringComparer.Ordinal);
                foreach (var property in item.Properties)
                {
                    if (allowed.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        row[property.Name] = JsonValue(property.Value);
                    }
                }

                rows.Add(row);
                if (rows.Count >= MaximumWmiRows)
                {
                    break;
                }
            }
        }

        return rows;
    }

    // Reads a class that may not exist on this machine; a missing class is recorded, not thrown.
    private static object WmiSection(
        string ns,
        string className,
        IReadOnlyCollection<string> allowed,
        List<string> issues,
        CancellationToken cancellation)
    {
        try
        {
            return WmiRows(ns, className, allowed, cancellation);
        }
        catch (Exception ex) when (ex is ManagementException or TimeoutException or UnauthorizedAccessException
                                       or System.Runtime.InteropServices.COMException)
        {
            AddIssue(issues, $"{className}: {ex.Message.Trim()}");
            return new { Problem = ex.Message.Trim() };
        }
    }

    private static object? JsonValue(object? value)
    {
        return value switch
        {
            null => null,
            string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double => value,
            char character => character.ToString(),
            DateTime time => time.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            Array array when array.Length <= 256 => array.Cast<object?>().Select(JsonValue).ToList(),
            Array array => $"({array.Length} values)",
            _ => value.ToString()
        };
    }

    private static ManagementScope Scope(string ns)
    {
        ManagementScope scope = new(ns, new ConnectionOptions { Timeout = WmiTimeout });
        scope.Connect();
        return scope;
    }

    private static EnumerationOptions Enumeration()
    {
        return new EnumerationOptions
        {
            Timeout = WmiTimeout,
            ReturnImmediately = true,
            Rewindable = false,
            DirectRead = false
        };
    }

    private static string? Text(ManagementBaseObject item, string property)
    {
        try
        {
            return item[property]?.ToString();
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    private static object? Qualifier(ManagementClass definition, string name)
    {
        try
        {
            return definition.Qualifiers[name].Value;
        }
        catch (ManagementException)
        {
            return null;
        }
    }
}
