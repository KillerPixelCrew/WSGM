using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using WindowsDeviceControl;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Reads Windows' Applications folder and activates packaged applications as the normal user.</summary>
/// <param name="context">The user-data directory for the scheduled-task handoff.</param>
internal sealed class WindowsInstalledApplicationHost(UserDataContext context) : IInstalledApplicationHost
{
    private const int IconSize = 48;

    /// <inheritdoc />
    public async Task<IReadOnlyList<InstalledProgram>> ListAsync(CancellationToken cancellationToken)
    {
        var listed = await InstalledApplications.ListAsync(cancellationToken).ConfigureAwait(false);
        return InstalledProgramCatalog.Merge(listed, File.Exists);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, Bitmap?>> ReadIconsAsync(IReadOnlyList<string> keys,
        CancellationToken cancellationToken)
    {
        var icons = await InstalledApplications.ReadIconsAsync(keys, IconSize, cancellationToken)
            .ConfigureAwait(false);
        return icons.ToDictionary(pair => pair.Key, pair => ToBitmap(pair.Value), StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public bool IsInstalled(string appUserModelId)
    {
        var separator = appUserModelId.IndexOf('!');
        return separator > 0 && InstalledApplications.IsPackageFamilyInstalled(appUserModelId[..separator]);
    }

    /// <inheritdoc />
    public string? WindowApplicationId(AppWindow window)
    {
        return DesktopWindows.ApplicationUserModelId(window.Hwnd, window.ProcessId);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     WSGM runs elevated, where package activation is refused, so the launcher runs through the same
    ///     least-privilege scheduled task as every ordinary program.
    /// </remarks>
    public Task<ScheduledTaskLaunchDisposition> ActivateAsync(string appUserModelId,
        CancellationToken cancellationToken)
    {
        return PackagedLauncherShortcut.ResolveLauncher() is { } launcher
            ? UnelevatedLauncher.TryStartViaScheduledTaskAsync(context, launcher,
                PackagedLaunchCommand.ComposeActivate(appUserModelId), DateTimeOffset.UtcNow.AddSeconds(10),
                Path.GetDirectoryName(launcher), cancellationToken)
            : Task.FromResult(ScheduledTaskLaunchDisposition.NotDispatched);
    }

    private static Bitmap? ToBitmap(InstalledApplicationIcon? icon)
    {
        if (icon is null)
        {
            return null;
        }

        WriteableBitmap bitmap = new(new PixelSize(icon.Width, icon.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = bitmap.Lock();
        var stride = icon.Width * 4;
        for (var row = 0; row < icon.Height; row++)
        {
            Marshal.Copy(icon.PremultipliedBgra, row * stride, buffer.Address + row * buffer.RowBytes, stride);
        }

        return bitmap;
    }
}
