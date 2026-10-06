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
    public void FailedSafetyZeroRemainsPendingUntilItSucceeds()
    {
        var (session, service) = Open();
        var (token, _) = session.Checkpoint();
        session.Acknowledge(token);
        session.Stream(nameof(IFakeService.Stream), [Json(7)]);
        service.FailingZeros = 1;
        _now += LabWorkerHost.StreamTimeout + TimeSpan.FromMilliseconds(1);

        session.ZeroIfStale();
        Assert.Equal(1, service.ZeroAttempts);
        Assert.Equal([7], service.Written);
        Assert.Equal("The safety zero failed.", session.StreamError);

        session.ZeroIfStale();
        session.ZeroIfStale();
        _now += LabWorkerHost.StreamTimeout;
        session.ZeroIfStale();
        Assert.Equal(2, service.ZeroAttempts);
        Assert.Equal([7, 0], service.Written);
        Assert.Equal("The safety zero failed.", session.StreamError);

        session.Release(token);
        var (next, _) = session.Checkpoint();
        session.Acknowledge(next);
        Assert.Null(session.StreamError);
    }

    [Fact]
    public void SafetyZeroLogsOnlyChangedFailureMessagesAndPreservesTheFirstError()
    {
        var (session, service) = Open();
        var (token, _) = session.Checkpoint();
        session.Acknowledge(token);
        session.Stream(nameof(IFakeService.Stream), [Json(7)]);
        service.FailingZeros = 3;
        service.ZeroFailure = new NotSupportedException("first");
        _now += LabWorkerHost.StreamTimeout + TimeSpan.FromMilliseconds(1);

        session.ZeroIfStale();
        session.ZeroIfStale();
        service.ZeroFailure = new ArgumentException("second");
        session.ZeroIfStale();

        var entries = session.TakeLog().Where(entry => entry.Kind == "zero-failed").ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal("first", session.StreamError);
        session.ZeroIfStale();
        session.ZeroIfStale();
        Assert.Equal(4, service.ZeroAttempts);
        Assert.Equal([7, 0], service.Written);
        Assert.Equal("first", session.StreamError);
    }

    [Fact]
    public void FailedFrameIsNeverReplayedWhenItsSafetyZeroFails()
    {
        var (session, service) = Open();
        var (token, _) = session.Checkpoint();
        session.Acknowledge(token);
        service.FailingZeros = 1;

        session.Stream(nameof(IFakeService.Stream), [Json(9)]);
        session.Stream(nameof(IFakeService.Stream), [Json(7)]);
        Assert.Equal("The motor write failed.", session.StreamError);
        _now += LabWorkerHost.StreamTimeout + TimeSpan.FromMilliseconds(1);
        session.ZeroIfStale();

        Assert.Equal(2, service.ZeroAttempts);
        Assert.Equal([0], service.Written);
        Assert.Equal("The motor write failed.", session.StreamError);
    }

    [Fact]
    public void SafetyZeroDoesNotSwallowOutOfMemory()
    {
        var (session, service) = Open();
        service.FailingZeros = 1;
        service.ZeroFailure = new OutOfMemoryException();

        var failure = Assert.Throws<TargetInvocationException>(session.ZeroQuietly);

        Assert.IsType<OutOfMemoryException>(failure.InnerException);
        Assert.Empty(session.TakeLog());
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
    public void EveryRegisteredServiceResolvesItsMethodsOnce()
    {
        foreach (var service in LabWorkerServices.All.Values)
        {
            Assert.NotEmpty(service.Methods);
            Assert.DoesNotContain(nameof(IDisposable.Dispose), service.Methods.Keys);
        }

        var init = LabWorkerServices.All[LabCuratedInitWorker.Service.Name];
        Assert.NotNull(init.Snapshot);
        Assert.True(init.Methods[nameof(ILabCuratedInitWorker.Send)].Write);
        Assert.True(init.Methods[nameof(ILabCuratedInitWorker.RecoverControllerMode)].Write);
        Assert.NotNull(LabWorkerServices.All[LabRumbleWorker.Service.Name].Zero);
    }

    [Fact]
    public void AnOverloadedServiceMethodIsRefusedAtRegistration()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new LabWorkerService("overloaded", typeof(IOverloadedService), (_, _) => new object()));
    }

    [Fact]
    public void Release_RefusesATokenThatIsNotArmed()
    {
        var (session, _) = Open();
        var (token, _) = session.Checkpoint();
        session.Acknowledge(token);

        Assert.Throws<InvalidOperationException>(() => session.Release("wrong"));
        Assert.True(session.Armed);

        session.Release(token);
        Assert.Throws<InvalidOperationException>(() => session.Release(token));
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

    internal interface IOverloadedService : IDisposable
    {
        int Read();

        int Read(int value);
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
        internal int FailingZeros;
        internal int ZeroAttempts;
        internal Exception ZeroFailure = new InvalidOperationException("The safety zero failed.");
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
            ZeroAttempts++;
            if (FailingZeros > 0)
            {
                FailingZeros--;
                throw ZeroFailure;
            }

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
