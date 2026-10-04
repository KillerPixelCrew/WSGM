// SPDX-License-Identifier: MIT

namespace WSGM.Shared;

/// <summary>Cross-version kernel object names shared by WSGM, setup and Device Lab.</summary>
internal static class SessionProtocolNames
{
    internal const string ExitForUpdate = @"Local\WSGM.ExitForUpdate";
    internal const string ExitForUninstall = @"Local\WSGM.ExitForUninstall";
    internal const string ShellMutex = @"Local\WSGM.Shell";
    internal const string DeviceOwner = @"Global\WSGM.DeviceOwner";
    internal const string AnchorRecoverySettled = @"Local\WSGM.ShellAnchor.RecoverySettled";
}
