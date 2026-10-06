using System.Text.Json;
using WSGM.DeviceLab.Application;
using WSGM.DeviceLab.Inventory;
using WSGM.Testing;

namespace WSGM.DeviceLab.Tests.Application;

public sealed class DeviceLabInventoryInputTests
{
    [Fact]
    public void Candidates_ReadsAValidInventoryBeyondTheOldByteCapWithoutOpeningHardware()
    {
        using TemporaryDirectory temporary = new();
        var path = temporary.GetPath("inventory.json");
        var inventory = new MachineInventory
        {
            SchemaVersion = WindowsInventoryCollector.CurrentSchemaVersion,
            CapturedAt = DateTimeOffset.UtcNow,
            Firmware = new FirmwareInventory { BaseboardManufacturer = "Contoso", BaseboardProduct = "HH-1" }
        };
        using (FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(output, inventory, DeviceLabJsonContext.Default.MachineInventory);
            var padding = new byte[64 * 1024];
            Array.Fill(padding, (byte)' ');
            for (var i = 0; i < 513; i++)
            {
                output.Write(padding);
            }
        }

        var candidates = DeviceLabApplication.Candidates(path);

        Assert.Empty(candidates.ReadOnlyProbes);
    }
}
