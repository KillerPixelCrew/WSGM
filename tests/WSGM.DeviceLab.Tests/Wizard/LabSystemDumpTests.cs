using System.Text;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Tests.Wizard;

public sealed class LabSystemDumpTests
{
    [Fact]
    public void ParseSmbios_RemovesSerialUuidAndRepeatedSerialText()
    {
        List<byte> table = [];
        byte[] system = new byte[0x1B];
        system[0] = 1;
        system[1] = 0x1B;
        system[2] = 0x01;
        system[4] = 1;
        system[5] = 2;
        system[6] = 3;
        system[7] = 4;
        for (var index = 0; index < 16; index++)
        {
            system[8 + index] = 0xAB;
        }

        system[0x19] = 5;
        system[0x1A] = 6;
        table.AddRange(system);
        AddStrings(table, "Maker", "Model", "1.0", "SER12345", "SKU1", "Family");
        table.AddRange(new byte[] { 11, 5, 0x02, 0x00, 1 });
        AddStrings(table, "SER12345");
        table.AddRange(new byte[] { 127, 4, 0xFF, 0xFF, 0, 0 });

        var parsed = LabSystemDump.ParseSmbios(Raw(table));

        Assert.Equal("3.4", parsed.Version);
        Assert.Equal(3, parsed.Structures.Count);
        var entry = parsed.Structures[0];
        Assert.Equal("System", entry.TypeName);
        Assert.Equal("0x0001", entry.Handle);
        Assert.Equal(["Maker", "Model", "1.0", LabSystemDump.RemovedText, "SKU1", "Family"], entry.Strings);
        Assert.DoesNotContain("AB", entry.Data!);
        Assert.Equal([LabSystemDump.RemovedText], parsed.Structures[1].Strings);
        Assert.Empty(parsed.Structures[2].Strings);
        Assert.Empty(parsed.Issues);
    }

    [Fact]
    public void ParseSmbios_RemovesMemoryPartAndAssetStrings()
    {
        List<byte> table = [];
        byte[] memory = new byte[0x1B];
        memory[0] = 17;
        memory[1] = 0x1B;
        memory[0x17] = 1; // manufacturer
        memory[0x18] = 2; // serial
        memory[0x19] = 3; // asset tag
        memory[0x1A] = 4; // part number
        table.AddRange(memory);
        AddStrings(table, "Samsung", "0001", "Tag01", "K4A8G");

        var parsed = LabSystemDump.ParseSmbios(Raw(table));

        Assert.Equal(
            ["Samsung", LabSystemDump.RemovedText, LabSystemDump.RemovedText, LabSystemDump.RemovedText],
            parsed.Structures[0].Strings);
    }

    [Fact]
    public void ParseSmbios_LeavesOemDataOutAndReportsBadLength()
    {
        List<byte> table = [];
        table.AddRange(new byte[] { 0x85, 6, 0x10, 0x00, 0x12, 0x34, 0, 0 });
        table.AddRange(new byte[] { 2, 0x40, 0x11, 0x00 });

        var parsed = LabSystemDump.ParseSmbios(Raw(table));

        Assert.Single(parsed.Structures);
        Assert.Null(parsed.Structures[0].Data);
        Assert.NotEmpty(parsed.Issues);
    }

    [Fact]
    public void ParseAcpiHeader_ReadsStandardFields()
    {
        var table = new byte[40];
        Encoding.ASCII.GetBytes("DSDT").CopyTo(table, 0);
        BitConverter.GetBytes(40u).CopyTo(table, 4);
        table[8] = 2;
        Encoding.ASCII.GetBytes("ALASKA").CopyTo(table, 10);
        Encoding.ASCII.GetBytes("A M I ").CopyTo(table, 16);
        BitConverter.GetBytes(0x01072009u).CopyTo(table, 24);
        Encoding.ASCII.GetBytes("INTL").CopyTo(table, 28);

        var header = LabSystemDump.ParseAcpiHeader(table);

        Assert.NotNull(header);
        Assert.Equal("DSDT", header.Signature);
        Assert.Equal(40u, header.Length);
        Assert.Equal(2, header.Revision);
        Assert.Equal("ALASKA", header.OemId);
        Assert.Equal("A M I", header.OemTableId);
        Assert.Equal("INTL", header.CreatorId);
    }

    [Fact]
    public void AcpiNaming_MatchesEnumerationOrderAndSkipsLicenceTables()
    {
        var id = BitConverter.ToUInt32(Encoding.ASCII.GetBytes("SSDT"));

        Assert.Equal("SSDT", LabSystemDump.AcpiSignature(id));
        Assert.Equal("acpi-SSDT.aml", LabSystemDump.AcpiFileName("SSDT", 1));
        Assert.Equal("acpi-SSDT-3.aml", LabSystemDump.AcpiFileName("SSDT", 3));
        Assert.True(LabSystemDump.IsLicenceTable("MSDM"));
        Assert.True(LabSystemDump.IsLicenceTable("SLIC"));
        Assert.False(LabSystemDump.IsLicenceTable("DSDT"));
    }

    [Fact]
    public void EdidManufacturer_DecodesWindowsByteOrder()
    {
        Assert.Equal("DEL", LabSystemDump.EdidManufacturer(0xAC10));
    }

    [Fact]
    public void ParseHidCaps_ReadsValueRangeAndLimits()
    {
        var entry = new byte[72];
        BitConverter.GetBytes((ushort)0x01).CopyTo(entry, 0);
        entry[2] = 5;
        entry[12] = 1;
        entry[15] = 1;
        BitConverter.GetBytes((ushort)16).CopyTo(entry, 18);
        BitConverter.GetBytes((ushort)2).CopyTo(entry, 20);
        BitConverter.GetBytes(-32768).CopyTo(entry, 40);
        BitConverter.GetBytes(32767).CopyTo(entry, 44);
        BitConverter.GetBytes((ushort)0x30).CopyTo(entry, 56);
        BitConverter.GetBytes((ushort)0x31).CopyTo(entry, 58);

        var caps = LabSystemDump.ParseHidCaps(entry, true);

        Assert.Equal(5, caps.ReportId);
        Assert.Equal("0x0001", caps.UsagePage);
        Assert.Equal("0x0030", caps.UsageMin);
        Assert.Equal("0x0031", caps.UsageMax);
        Assert.Equal(16, caps.BitSize);
        Assert.Equal(2, caps.ReportCount);
        Assert.Equal(-32768, caps.LogicalMin);
        Assert.Equal(32767, caps.LogicalMax);
    }

    [Fact]
    public void DecodeProperty_ReadsStringListsAndGuids()
    {
        var list = Encoding.Unicode.GetBytes("HID\\VID_1\0HID_DEVICE\0\0");
        var guid = Guid.NewGuid();

        Assert.Equal(["HID\\VID_1", "HID_DEVICE"], LabSystemDump.DecodeProperty(0x2012, list));
        Assert.Equal([guid.ToString("D")], LabSystemDump.DecodeProperty(0x0D, guid.ToByteArray()));
        Assert.Empty(LabSystemDump.DecodeProperty(0x07, [1, 0, 0, 0]));
    }

    [Fact]
    public void Summarize_CountsSectionsAndFailures()
    {
        LabSystemDumpSectionResult[] results =
        [
            Section("acpi", 38),
            Section("device-tree", 412),
            Section("hid", 57),
            Section("sensors", 3),
            new LabSystemDumpSectionResult
                { Id = "wmi", Status = LabSystemDumpSectionStatus.Failed, Summary = "could not be read" }
        ];

        Assert.Equal(
            "38 ACPI tables, 412 devices, 57 HID collections, 3 sensors, 1 part could not be read",
            LabSystemDump.Summarize(results));
    }

    [Fact]
    public void Run_TurnsAFailingSectionIntoAResult()
    {
        LabSystemDumpSection section = new("broken", "Broken", _ => throw new InvalidOperationException("boom"));
        LabSystemDumpContext context = new(null!, string.Empty, false, CancellationToken.None);

        var result = LabSystemDump.Run(section, context);

        Assert.Equal(LabSystemDumpSectionStatus.Failed, result.Status);
        Assert.Contains("boom", result.Issues[0]);
    }

    [Fact]
    public void ParseEdid_ReadsNativeModeAndRefreshRangeWithoutSerial()
    {
        var edid = new byte[128];
        new byte[] { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 }.CopyTo(edid, 0);
        edid[8] = 0x09; // "BOE": 0x09E5
        edid[9] = 0xE5;
        edid[10] = 0x1B;
        edid[11] = 0x0A;
        edid[12] = 0x78; // serial bytes, never read
        edid[18] = 1;
        edid[19] = 4;
        edid[24] = 0x01;

        // Detailed timing: 1200x1920 portrait, 148.5 MHz clock, blanking 80/40.
        var dtd = edid.AsSpan(54, 18);
        BitConverter.GetBytes((ushort)14850).CopyTo(dtd);
        dtd[2] = 1200 & 0xFF;
        dtd[3] = 80;
        dtd[4] = (byte)((1200 >> 8) << 4);
        dtd[5] = 1920 & 0xFF;
        dtd[6] = 40;
        dtd[7] = (byte)((1920 >> 8) << 4);

        // Range limits 48-120 Hz.
        var range = edid.AsSpan(72, 18);
        range[3] = 0xFD;
        range[5] = 48;
        range[6] = 120;

        // Serial text descriptor, which must not appear anywhere.
        var serial = edid.AsSpan(90, 18);
        serial[3] = 0xFF;
        Encoding.ASCII.GetBytes("SECRET1\n").CopyTo(serial[5..]);

        var parsed = LabSystemDump.ParseEdid(edid);

        Assert.NotNull(parsed);
        Assert.Equal("BOE", parsed.Manufacturer);
        Assert.Equal("0x0A1B", parsed.ProductCode);
        Assert.Equal(1200, parsed.NativeWidth);
        Assert.Equal(1920, parsed.NativeHeight);
        Assert.Equal("portrait", parsed.NativeOrientation);
        Assert.Equal(48, parsed.MinimumRefreshHz);
        Assert.Equal(120, parsed.MaximumRefreshHz);
        Assert.True(parsed.VariableRefreshLikely);
        Assert.DoesNotContain(parsed.Text, text => text.Contains("SECRET"));
    }

    [Fact]
    public void ParseEdid_RejectsOtherBytes()
    {
        Assert.Null(LabSystemDump.ParseEdid(new byte[128]));
    }

    [Fact]
    public void EcRows_FormatsSixteenPerRowAndMarksUnreadRegisters()
    {
        var registers = Enumerable.Range(0, 32).Select(value => value == 17 ? -1 : value).ToList();

        var rows = LabSystemDump.EcRows(registers);

        Assert.Equal(2, rows.Count);
        Assert.StartsWith("00: 00 01 02", rows[0]);
        Assert.StartsWith("10: 10 ?? 12", rows[1]);
    }

    [Fact]
    public void VendorClassPresence_ReportsNamedAndLenovoClasses()
    {
        LabWmiClass[] classes =
        [
            new() { Name = "MSI_ACPI", Methods = ["Get_EC"] },
            new() { Name = "LENOVO_GAMEZONE_DATA" },
            new() { Name = "WmiMonitorBrightness" }
        ];

        var json = System.Text.Json.JsonSerializer.Serialize(LabSystemDump.VendorClassPresence(classes));

        Assert.Contains("\"Name\":\"MSI_ACPI\",\"Present\":true", json);
        Assert.Contains("\"Name\":\"SuRwECRegInterface\",\"Present\":false", json);
        Assert.Contains("LENOVO_GAMEZONE_DATA", json);
        Assert.Single(LabSystemDump.ChargeLimitSignals([classes[0]]));
    }

    private static LabSystemDumpSectionResult Section(string id, int count)
    {
        return new LabSystemDumpSectionResult
            { Id = id, Status = LabSystemDumpSectionStatus.Completed, Count = count, Summary = string.Empty };
    }

    private static void AddStrings(List<byte> table, params string[] strings)
    {
        foreach (var text in strings)
        {
            table.AddRange(Encoding.ASCII.GetBytes(text));
            table.Add(0);
        }

        table.Add(0);
    }

    private static byte[] Raw(List<byte> table)
    {
        List<byte> raw = [0, 3, 4, 0];
        raw.AddRange(BitConverter.GetBytes((uint)table.Count));
        raw.AddRange(table);
        return [.. raw];
    }
}
