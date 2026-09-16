// SPDX-License-Identifier: MIT

using System.Xml.Linq;

namespace WSGM.Device.Tests;

/// <summary>Reads files from the WSGM checkout the tests were built in.</summary>
/// <remarks>
///     Linked into several test projects, so a missing checkout throws a plain exception
///     instead of using a test-framework assertion.
/// </remarks>
internal static class RepositoryFiles
{
    /// <summary>The nearest directory above the test output that holds WSGM.slnx.</summary>
    internal static string Root
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null
                   && !File.Exists(Path.Combine(directory.FullName, "WSGM.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName
                   ?? throw new DirectoryNotFoundException(
                       "Run the tests from a WSGM checkout; no WSGM.slnx was found above the test output.");
        }
    }

    /// <summary>Loads a project file by its path relative to the repository root.</summary>
    internal static XDocument LoadProject(string relativePath)
    {
        return XDocument.Load(Path.Combine(Root, relativePath));
    }
}
