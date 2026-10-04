using System;
using System.IO;

namespace WSGM.Core;

/// <summary>The explicit filesystem and lock identity for one user's WSGM state.</summary>
/// <param name="Root">The directory containing configuration and sidecars.</param>
/// <param name="ConfigMutexName">The cross-process configuration mutex name.</param>
public sealed record UserDataContext(string Root, string ConfigMutexName)
{
    /// <summary>Creates the current account's context without initializing logging or configuration.</summary>
    /// <returns>The current account's production paths and unchanged lock name.</returns>
    public static UserDataContext ForCurrentUser()
    {
        return new UserDataContext(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WSGM"),
            @"Local\WSGM.Config");
    }
}
