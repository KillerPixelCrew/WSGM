using System;
using System.IO;
using System.Linq;
using WSGM.DeviceLab.Application;
using WSGM.DeviceLab.Inventory;

namespace WSGM.DeviceLab.Wizard;

internal static partial class LabSystemDump
{
    // Presence only; the inventory collector never invokes a method or reads instance data.
    private static readonly (string Namespace, string ClassName)[] InventoryWmiProbes =
    [
        ("root\\wmi", "MSI_ACPI"),
        ("root\\wmi", "MSI_Event"),
        ("root\\wmi", "SuRwECRegInterface"),
        ("root\\wmi", "LENOVO_GAMEZONE_DATA"),
        ("root\\wmi", "LENOVO_OTHER_METHOD"),
        ("root\\wmi", "LENOVO_FAN_METHOD"),
        ("root\\wmi", "AsusAtkWmi_WMNB")
    ];

    // The same inventory the Identity step records, taken again here so the dump is complete on its
    // own, with the vendor WMI classes probed.
    private static LabSystemDumpSectionResult CollectInventory(LabSystemDumpContext context)
    {
        var inventory = WindowsInventoryCollector.Collect(DateTimeOffset.UtcNow, InventoryWmiProbes, context.Cancellation);
        DurableFile.WriteNewText(
            Path.Combine(context.Attempt, "inventory.json"),
            DeviceLabJson.Serialize(inventory) + "\n");
        var issues = inventory.CollectionIssues.Select(issue => $"{issue.Lane}: {issue.Error}").ToList();
        var present = inventory.WmiClasses.Count(entry => entry.Access is WmiAccess.Available or WmiAccess.AccessDenied);
        return Result("inventory", inventory.UsbInterfaces.Count,
            $"{Plural(inventory.UsbInterfaces.Count, "USB or HID interface", "USB or HID interfaces")}, "
            + $"{Plural(present, "vendor WMI class", "vendor WMI classes")}", issues);
    }
}
