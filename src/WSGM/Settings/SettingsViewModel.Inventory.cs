using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LibHandheld.Contracts;
using WSGM.Core;

namespace WSGM.Settings;

/// <summary>Detached read-only model metadata; capturing it never activates a device or GPU owner.</summary>
internal sealed record SettingsInventory(HandheldDefinition? Device, IReadOnlyList<BuiltinGpuDriver> Graphics);

public sealed partial class SettingsViewModel
{
    private bool _inventoryClosed;
    private Task? _inventoryWork;

    /// <summary>Explains a pending or failed inventory read without hiding the rest of Settings.</summary>
    public string InventoryDiscoveryText
    {
        get;
        private set
        {
            field = value;
            Raise(nameof(InventoryDiscoveryText));
            Raise(nameof(DeviceProfilesEmptyReason));
        }
    } = "";

    /// <summary>Starts pure machine discovery after the window opens; repeated requests share its one read.</summary>
    internal Task StartInventoryDiscoveryAsync()
    {
        if (_inventoryClosed)
        {
            return Task.CompletedTask;
        }

        return _inventoryWork ??= ReadInventoryAsync();
    }

    /// <summary>Prevents native discovery that is still returning from publishing into a closed window.</summary>
    internal void StopInventoryDiscovery()
    {
        _inventoryClosed = true;
    }

    private async Task ReadInventoryAsync()
    {
        InventoryDiscoveryText = "Reading device and graphics metadata…";
        try
        {
            var inventory = await Task.Run(_services.ReadInventory).ConfigureAwait(false);
            await PublishInventoryAsync(() =>
            {
                // A composer may already have exact metadata. Keep that authoring scope and its
                // draft; late discovery must not replace edits made while the read was running.
                if (_deviceProfileDefinition.Length == 0)
                {
                    LoadDeviceProfiles(inventory.Device);
                }

                LoadGraphicsDrivers(inventory.Graphics);
                InventoryDiscoveryText = "";
            }).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            await PublishInventoryAsync(() =>
            {
                InventoryDiscoveryText = "Device and graphics metadata could not be read: " + error.Message;
                _services.Report("Settings machine inventory failed.", error);
            }).ConfigureAwait(false);
        }
    }

    private Task PublishInventoryAsync(Action publish)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _services.PostToUi(() =>
        {
            try
            {
                if (!_inventoryClosed)
                {
                    publish();
                }

                completion.SetResult();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                completion.SetException(error);
            }
        });
        return completion.Task;
    }
}
