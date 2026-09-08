using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>Dependency-first activation order with failures isolated from independent packages.</summary>
internal sealed record CommonPluginDependencyPlan(IReadOnlyList<PluginManifest> Ordered, IReadOnlyDictionary<string, string> Rejected)
{
    internal static CommonPluginDependencyPlan Create(IReadOnlyList<PluginManifest> manifests)
    {
        if (manifests.Count > 128 || manifests.Any(manifest => PluginManifestReader.Validate(manifest).Count != 0))
        { throw new ArgumentException("Dependency planning requires bounded validated manifests."); }
        Dictionary<string, string> rejected = new(StringComparer.Ordinal);
        Dictionary<string, PluginManifest> packages = new(StringComparer.Ordinal);
        foreach (var group in manifests.GroupBy(manifest => manifest.Id, StringComparer.Ordinal))
        {
            if (group.Count() != 1) { rejected[group.Key] = "Duplicate package identity."; }
            else { packages.Add(group.Key, group.Single()); }
        }
        foreach (var package in packages.Values)
        {
            foreach (var dependency in package.Dependencies)
            {
                if (!packages.TryGetValue(dependency.Id, out var installed))
                { rejected[package.Id] = "Missing or ambiguous dependency: " + dependency.Id; break; }
                var version = NumericVersion(installed.Version);
                if (version < NumericVersion(dependency.MinimumVersion)
                    || (dependency.MaximumVersionExclusive is { } maximum && version >= NumericVersion(maximum)))
                { rejected[package.Id] = "Incompatible dependency: " + dependency.Id; break; }
            }
        }
        List<PluginManifest> ordered = [];
        HashSet<string> admitted = new(StringComparer.Ordinal);
        var pending = packages.Values.OrderBy(package => package.Id, StringComparer.Ordinal).ToList();
        while (pending.Count > 0)
        {
            bool progressed = false;
            for (int index = pending.Count - 1; index >= 0; index--)
            {
                var package = pending[index];
                var blocked = package.Dependencies.FirstOrDefault(dependency => rejected.ContainsKey(dependency.Id));
                if (blocked is not null) { rejected[package.Id] = "Dependency was rejected: " + blocked.Id; }
                if (rejected.ContainsKey(package.Id))
                { pending.RemoveAt(index); progressed = true; continue; }
                if (!package.Dependencies.All(dependency => admitted.Contains(dependency.Id))) { continue; }
                ordered.Add(package);
                admitted.Add(package.Id);
                pending.RemoveAt(index);
                progressed = true;
            }
            if (progressed) { continue; }
            foreach (var package in pending) { rejected[package.Id] = "Dependency cycle or dependency on a cycle."; }
            break;
        }
        return new(ordered.AsReadOnly(), new ReadOnlyDictionary<string, string>(rejected));
    }

    private static Version NumericVersion(string text)
    {
        var parsed = Version.Parse(text);
        return new(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));
    }
}
