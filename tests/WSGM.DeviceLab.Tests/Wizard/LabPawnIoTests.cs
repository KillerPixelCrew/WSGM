using WSGM.Device.Tests;
using WSGM.DeviceLab.Inventory;
using WSGM.DeviceLab.Knowledge;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Tests.Wizard;

public sealed class LabPawnIoTests
{
    [Fact]
    public async Task Install_RecordsBeforeRunningAndOffersRemovalAfterwards()
    {
        using TemporaryDirectory temporary = new();
        var state = State(temporary);
        FakeHost host = new(null);
        host.OnInstall = () => Assert.True(state.Read().PawnIoInstalledByLab);
        var pawnIo = new LabPawnIo(state, host);

        var outcome = await pawnIo.InstallAsync(CancellationToken.None);

        Assert.True(outcome.Running);
        Assert.True(pawnIo.CanOfferRemoval());
        Assert.Null(await pawnIo.RemoveAsync(CancellationToken.None));
        Assert.False(state.Read().PawnIoInstalledByLab);
    }

    [Fact]
    public async Task Install_ThatLeavesNothingInstalledClearsTheRecord()
    {
        using TemporaryDirectory temporary = new();
        var state = State(temporary);
        FakeHost host = new(null) { InstallProblem = "exit 1" };

        var outcome = await new LabPawnIo(state, host).InstallAsync(CancellationToken.None);

        Assert.False(outcome.Running);
        Assert.False(state.Read().PawnIoInstalledByLab);
    }

    [Fact]
    public void Reconcile_KeepsAnInstallThatFinishedAfterTheWindowClosed()
    {
        using TemporaryDirectory temporary = new();
        var state = State(temporary);
        state.Update(changes => changes with { PawnIoInstalledByLab = true });

        var pawnIo = new LabPawnIo(state, new FakeHost("2.2.0"));
        pawnIo.Reconcile();

        Assert.True(state.Read().PawnIoInstalledByLab);
        Assert.True(pawnIo.CanOfferRemoval());
    }

    [Fact]
    public void Reconcile_ForgetsAnInstallThatNeverHappened()
    {
        using TemporaryDirectory temporary = new();
        var state = State(temporary);
        state.Update(changes => changes with { PawnIoInstalledByLab = true });

        new LabPawnIo(state, new FakeHost(null)).Reconcile();

        Assert.False(state.Read().PawnIoInstalledByLab);
    }

    [Fact]
    public async Task Replace_NeverOffersRemovalOfTheTestersDriver()
    {
        using TemporaryDirectory temporary = new();
        var state = State(temporary);
        FakeHost host = new("1.9.0");
        var pawnIo = new LabPawnIo(state, host);

        var outcome = await pawnIo.ReplaceAsync(host.Detect(), CancellationToken.None);

        Assert.True(outcome.Running);
        Assert.Equal("1.9.0", state.Read().PawnIoReplacedVersion);
        Assert.False(pawnIo.CanOfferRemoval());
        Assert.NotNull(await pawnIo.RemoveAsync(CancellationToken.None));
        Assert.Equal(1, host.Uninstalls);
    }

    [Fact]
    public async Task Replace_ReportsTheTesterLostTheirDriverWhenTheInstallFails()
    {
        using TemporaryDirectory temporary = new();
        FakeHost host = new("1.9.0") { InstallProblem = "exit 5" };

        var outcome = await new LabPawnIo(State(temporary), host).ReplaceAsync(host.Detect(), CancellationToken.None);

        Assert.True(outcome.LostPrevious);
        Assert.False(outcome.Running);
    }

    [Fact]
    public async Task Replace_KeepsNoRecordWhenTheOldDriverCannotBeRemoved()
    {
        using TemporaryDirectory temporary = new();
        var state = State(temporary);
        FakeHost host = new("1.9.0") { UninstallProblem = "access denied" };

        var outcome = await new LabPawnIo(state, host).ReplaceAsync(host.Detect(), CancellationToken.None);

        Assert.False(outcome.LostPrevious);
        Assert.Null(state.Read().PawnIoReplacedVersion);
        Assert.Equal(0, host.Installs);
    }

    [Fact]
    public void Signature_AcceptsOnlyThePinnedSigner()
    {
        var installer = Path.Combine(RepositoryRoot(), "artifacts", "pawnio", "PawnIO_setup.exe");
        if (!File.Exists(installer))
        {
            // Acquired only by eng/acquire-pawnio.ps1; the pin check in eng/verify.ps1 covers its absence.
            return;
        }

        using var held = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.Null(AuthenticodeSignature.Verify(installer, held.SafeFileHandle, PawnIoSetup.Pin.SignerThumbprint));
        Assert.NotNull(AuthenticodeSignature.Verify(installer, held.SafeFileHandle, new string('0', 40)));
    }

    [Fact]
    public void Signature_RejectsAnUnsignedFile()
    {
        using TemporaryDirectory temporary = new();
        var path = Path.Combine(temporary.Root, "unsigned.exe");
        File.WriteAllBytes(path, [0x4D, 0x5A, 0, 0]);
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.NotNull(AuthenticodeSignature.Verify(path, held.SafeFileHandle, PawnIoSetup.Pin.SignerThumbprint));
    }

    [Fact]
    public void Identity_RecordsControllerFirmwareAndCpuIdWithoutSerials()
    {
        var inventory = new MachineInventory
        {
            SchemaVersion = 1,
            Firmware = new FirmwareInventory { BaseboardManufacturer = "Contoso", BaseboardProduct = "X1" },
            Processor = new ProcessorInventory { Name = "CPU", Family = 25, Model = 116, Stepping = 1 },
            UsbInterfaces =
            [
                new UsbInterfaceInventory
                {
                    InstanceId = @"HID\VID_0DB0&PID_1901\7&1&0", DeviceClass = "HIDClass", VendorId = "0DB0",
                    ProductId = "1901", DeviceRelease = "0229", Present = true
                },
                new UsbInterfaceInventory
                {
                    InstanceId = @"USB\VID_0DB0&PID_1901\SERIAL", DeviceClass = "USB", VendorId = "0DB0",
                    ProductId = "1901", DeviceRelease = "0229", Present = true
                }
            ],
            CapturedAt = DateTimeOffset.UnixEpoch
        };

        var facts = LabIdentity.Observe(DeviceKnowledgeBase.Default, inventory).Facts;

        Assert.Equal("25-116-1", facts.ProcessorIdentity);
        Assert.Equal(["0DB0:1901 0229"], facts.ControllerFirmware);
    }

    private static LabMachineState State(TemporaryDirectory temporary)
    {
        return new LabMachineState(Path.Combine(temporary.Root, "machine.json"));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WSGM.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    private sealed class FakeHost(string? installed) : IPawnIoHost
    {
        private string? _installed = installed;

        public string? InstallProblem { get; init; }

        public string? UninstallProblem { get; init; }

        public Action? OnInstall { get; set; }

        public int Installs { get; private set; }

        public int Uninstalls { get; private set; }

        public PawnIoStatus Detect()
        {
            return new PawnIoStatus(_installed, _installed is null ? null : @"C:\Program Files\PawnIO",
                _installed is not null, _installed is null ? 2 : 0);
        }

        public Task<string?> InstallAsync(CancellationToken cancellationToken)
        {
            OnInstall?.Invoke();
            Installs++;
            if (InstallProblem is null)
            {
                _installed = "2.2.0";
            }

            return Task.FromResult(InstallProblem);
        }

        public Task<string?> UninstallAsync(PawnIoStatus status, CancellationToken cancellationToken)
        {
            Uninstalls++;
            if (UninstallProblem is null)
            {
                _installed = null;
            }

            return Task.FromResult(UninstallProblem);
        }
    }
}
