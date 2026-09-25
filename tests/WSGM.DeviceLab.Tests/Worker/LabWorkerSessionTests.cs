using System.Reflection;
using System.Text.Json;
using WSGM.DeviceLab.Transports;
using WSGM.DeviceLab.Wizard;
using WSGM.DeviceLab.Worker;

namespace WSGM.DeviceLab.Tests.Worker;

public sealed class LabWorkerSessionTests
{
    private DateTime _now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Call_RefusesAWriteBeforeAnyCheckpoint()
    {
        var (session, service) = Open();

        Assert.Throws<InvalidOperationException>(() => session.Call(nameof(IFakeService.Write), [Json(5)]));
        Assert.Empty(service.Written);
    }

    [Fact]
    public void Call_AllowsAReadWithoutACheckpoint()
    {
        var (session, _) = Open();

        Assert.Equal(7, session.Call(nameof(IFakeService.Read), [])!.Value.GetInt32());
    }

    [Fact]
    public void Call_RefusesAWriteWhileTheCheckpointAwaitsItsAcknowledgement()
    {
        var (session, service) = Open();
        var (_, original) = session.Checkpoint();

        Assert.Equal(7, original.GetInt32());
        Assert.Throws<InvalidOperationException>(() => session.Call(nameof(IFakeService.Write), [Json(5)]));
        Assert.Empty(service.Written);
    }

    [Fact]
    public void Call_AllowsAWriteAfterTheAcknowledgement()
    {
        var (session, service) = Open();
        var (token, _) = session.Checkpoint();

        session.Acknowledge(token);
        session.Call(nameof(IFakeService.Write), [Json(5)]);

        Assert.True(session.Armed);
        Assert.Equal([5], service.Written);
    }

    [Fact]
    public void Stream_RequiresAnAcknowledgedCheckpoint()
    {
        var (session, service) = Open();
        session.Stream(nameof(IFakeService.Stream), [Json(5)]);
        var (token, _) = session.Checkpoint();
        session.Stream(nameof(IFakeService.Stream), [Json(6)]);

        Assert.Empty(service.Written);

        session.Acknowledge(token);
        session.Stream(nameof(IFakeService.Stream), [Json(7)]);

        Assert.Equal([7], service.Written);
    }

    [Fact]
    public void Stream_ZeroesAnOutputThatStopsSendingFrames()
    {
        var (session, service) = Open();
        var (token, _) = session.Checkpoint();
        session.Acknowledge(token);
        session.Stream(nameof(IFakeService.Stream), [Json(7)]);

        _now += LabWorkerHost.StreamTimeout + TimeSpan.FromMilliseconds(1);
        session.ZeroIfStale();

        Assert.Equal([7, 0], service.Written);
    }

    [Fact]
    public void Stream_DoesNotRetryAfterAFailedFrame()
    {
        var (session, service) = Open();
        var (token, _) = session.Checkpoint();
        session.Acknowledge(token);

        session.Stream(nameof(IFakeService.Stream), [Json(9)]);
        session.Stream(nameof(IFakeService.Stream), [Json(9)]);
        session.Stream(nameof(IFakeService.Stream), [Json(7)]);
        _now += LabWorkerHost.StreamTimeout + TimeSpan.FromMilliseconds(1);
        session.ZeroIfStale();

        Assert.Equal([0], service.Written);
    }

    [Fact]
    public void Stream_ReportsAFailedFrameUntilTheNextAcknowledgedCheckpoint()
    {
        var (session, _) = Open();
        var (token, _) = session.Checkpoint();
        session.Acknowledge(token);

        session.Stream(nameof(IFakeService.Stream), [Json(7)]);
        Assert.Null(session.StreamError);
        session.Stream(nameof(IFakeService.Stream), [Json(9)]);

        Assert.Equal("The motor write failed.", session.StreamError);
        session.Release(token);
        var (next, _) = session.Checkpoint();
        session.Acknowledge(next);
        Assert.Null(session.StreamError);
    }

    [Fact]
    public void Checkpoint_ANullSnapshotReachesTheWizardAsTheDefault()
    {
        FakeNullService service = new();
        LabWorkerService registration = new("null", typeof(IFakeNullService), (_, _) => service);
        using LabWorkerSession session = new(registration, service, new LabPowerLog(), () => _now);

        var (token, original) = session.Checkpoint();
        var line = JsonSerializer.Serialize(
            new LabWorkerResponse { Id = 1, Ok = true, Result = LabWorkerHost.Result(original), Token = token },
            LabWorkerHost.WireOptions);
        var response = JsonSerializer.Deserialize<LabWorkerResponse>(line, LabWorkerHost.WireOptions)!;

        Assert.Equal(JsonValueKind.Null, original.ValueKind);
        Assert.Null(response.Result);
        Assert.Null(LabWorkerClient.Snapshot<int?>(response.Result));
        Assert.Null(LabWorkerClient.Snapshot<int?>(original));
        Assert.Null(LabWorkerClient.Snapshot<string>(response.Result));
        Assert.Equal(7, LabWorkerClient.Snapshot<int?>(Json(7)));
    }

    [Fact]
    public void Call_BindsTheCallsCancellationTokenWithoutSendingIt()
    {
        var (session, _) = Open();
        using CancellationTokenSource cancel = new();

        Assert.False(session.Call(nameof(IFakeService.Wait), [Json(1)], cancel.Token)!.Value.GetBoolean());
        cancel.Cancel();
        Assert.True(session.Call(nameof(IFakeService.Wait), [Json(1)], cancel.Token)!.Value.GetBoolean());
        Assert.Throws<ArgumentException>(() => session.Call(nameof(IFakeService.Wait), [Json(1), Json(2)]));
    }

    [Fact]
    public void Checkpoint_RefusesASecondWhileOneIsPendingOrArmed()
    {
        var (session, _) = Open();
        var (token, _) = session.Checkpoint();

        Assert.Throws<InvalidOperationException>(() => session.Checkpoint());
        session.Acknowledge(token);
        Assert.Throws<InvalidOperationException>(() => session.Checkpoint());
    }

    [Fact]
    public void Acknowledge_RefusesAWrongToken()
    {
        var (session, _) = Open();
        _ = session.Checkpoint();

        Assert.Throws<InvalidOperationException>(() => session.Acknowledge("not-the-token"));
        Assert.False(session.Armed);
    }

    [Fact]
    public void Acknowledge_RefusesAnAcknowledgementAfterFiveSeconds()
    {
        var (session, service) = Open();
        var (token, _) = session.Checkpoint();

        _now += LabWorkerHost.AcknowledgeTimeout + TimeSpan.FromMilliseconds(1);

        Assert.Throws<InvalidOperationException>(() => session.Acknowledge(token));
        Assert.False(session.Armed);
        Assert.Throws<InvalidOperationException>(() => session.Call(nameof(IFakeService.Write), [Json(5)]));
        Assert.Empty(service.Written);
    }

    [Fact]
    public void Release_RefusesWritesAgainAndAllowsANewCheckpoint()
    {
        var (session, service) = Open();
        var (token, _) = session.Checkpoint();
        session.Acknowledge(token);

        session.Release(token);

        Assert.False(session.Armed);
        Assert.Throws<InvalidOperationException>(() => session.Call(nameof(IFakeService.Write), [Json(5)]));
        Assert.Empty(service.Written);
        _ = session.Checkpoint();
    }

    [Fact]
    public void Call_RefusesAMethodTheInterfaceDoesNotDeclare()
    {
        var (session, _) = Open();

        Assert.Throws<InvalidOperationException>(() => session.Call(nameof(FakeService.NotOnTheInterface), []));
    }

    [Fact]
    public void HardwareWorker_RegistersRumbleAndCuratedInitWithCheckpointedWrites()
    {
        var rumble = LabWorkerServices.All[LabRumbleWorker.Service.Name];
        var init = LabWorkerServices.All[LabCuratedInitWorker.Service.Name];

        Assert.Equal(typeof(ILabRumbleWorker), rumble.Interface);
        Assert.Equal(typeof(ILabCuratedInitWorker), init.Interface);
        Assert.NotNull(typeof(ILabRumbleWorker).GetMethod(nameof(ILabRumbleWorker.Original))!
            .GetCustomAttribute<LabWorkerSnapshotAttribute>());
        Assert.NotNull(typeof(ILabRumbleWorker).GetMethod(nameof(ILabRumbleWorker.Write))!
            .GetCustomAttribute<LabWorkerWriteAttribute>());
        Assert.NotNull(typeof(ILabRumbleWorker).GetMethod(nameof(ILabRumbleWorker.SetIntensity))!
            .GetCustomAttribute<LabWorkerStreamAttribute>());
        Assert.NotNull(typeof(ILabRumbleWorker).GetMethod(nameof(ILabRumbleWorker.Zero))!
            .GetCustomAttribute<LabWorkerZeroAttribute>());
        Assert.NotNull(typeof(ILabCuratedInitWorker).GetMethod(nameof(ILabCuratedInitWorker.Original))!
            .GetCustomAttribute<LabWorkerSnapshotAttribute>());
        Assert.NotNull(typeof(ILabCuratedInitWorker).GetMethod(nameof(ILabCuratedInitWorker.Send))!
            .GetCustomAttribute<LabWorkerWriteAttribute>());
        Assert.NotNull(typeof(ILabCuratedInitWorker).GetMethod(nameof(ILabCuratedInitWorker.RecoverControllerMode))!
            .GetCustomAttribute<LabWorkerWriteAttribute>());

        // The re-enumeration wait stays cancellable through the worker.
        Assert.Equal(typeof(CancellationToken), typeof(ILabCuratedInitWorker)
            .GetMethod(nameof(ILabCuratedInitWorker.Send))!.GetParameters().Single().ParameterType);
        Assert.Equal(typeof(CancellationToken), typeof(ILabCuratedInitWorker)
            .GetMethod(nameof(ILabCuratedInitWorker.RecoverControllerMode))!.GetParameters().Single().ParameterType);
    }

    private (LabWorkerSession Session, FakeService Service) Open()
    {
        FakeService service = new();
        LabWorkerService registration = new("fake", typeof(IFakeService), (_, _) => service);
        return (new LabWorkerSession(registration, service, new LabPowerLog(), () => _now), service);
    }

    private static JsonElement Json(int value)
    {
        return JsonSerializer.SerializeToElement(value);
    }

    internal interface IFakeService : IDisposable
    {
        [LabWorkerSnapshot]
        int Read();

        [LabWorkerWrite]
        void Write(int value);

        [LabWorkerStream]
        void Stream(int value);

        [LabWorkerZero]
        void Zero();

        bool Wait(int value, CancellationToken cancellationToken);
    }

    internal interface IFakeNullService : IDisposable
    {
        [LabWorkerSnapshot]
        int? Original();
    }

    internal sealed class FakeNullService : IFakeNullService
    {
        public int? Original()
        {
            return null;
        }

        public void Dispose()
        {
        }
    }

    internal sealed class FakeService : IFakeService
    {
        public List<int> Written { get; } = [];

        public int Read()
        {
            return 7;
        }

        public bool Wait(int value, CancellationToken cancellationToken)
        {
            return cancellationToken.IsCancellationRequested;
        }

        public void Write(int value)
        {
            Written.Add(value);
        }

        public void Stream(int value)
        {
            if (value == 9)
            {
                throw new InvalidOperationException("The motor write failed.");
            }

            Written.Add(value);
        }

        public void Zero()
        {
            Written.Add(0);
        }

        public void Dispose()
        {
        }

        public void NotOnTheInterface()
        {
        }
    }
}
