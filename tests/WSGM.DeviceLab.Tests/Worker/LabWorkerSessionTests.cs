using System.Text.Json;
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
    }

    internal sealed class FakeService : IFakeService
    {
        public List<int> Written { get; } = [];

        public int Read()
        {
            return 7;
        }

        public void Write(int value)
        {
            Written.Add(value);
        }

        public void Dispose()
        {
        }

        public void NotOnTheInterface()
        {
        }
    }
}
