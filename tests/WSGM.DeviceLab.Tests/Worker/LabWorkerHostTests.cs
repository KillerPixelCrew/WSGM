using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using WSGM.DeviceLab.Worker;

namespace WSGM.DeviceLab.Tests.Worker;

public sealed class LabWorkerHostTests
{
    [Fact]
    public async Task Eof_DropsQueuedWritesAndOpens_ThenZeroesAndDisposesTheActiveSession()
    {
        using RequestReader input = new();
        using ResponseWriter output = new();
        using BlockingService service = new();
        using var entered = service.Entered;
        using var cancelled = service.Cancelled;
        using var unblock = service.Unblock;
        var opens = 0;
        var registration = new LabWorkerService("test", typeof(IBlockingService), (_, _) =>
        {
            opens++;
            return service;
        });
        var host = new LabWorkerHost(input, output,
            new Dictionary<string, LabWorkerService> { [registration.Name] = registration }, TimeProvider.System);
        var serving = Task.Run(host.Serve);
        Assert.Equal(0, output.Next().Id);
        input.Add(new LabWorkerRequest { Id = 1, Op = "open", Service = "test" });
        var session = output.Next().Session;
        input.Add(new LabWorkerRequest { Id = 2, Op = "checkpoint", Session = session });
        var token = output.Next().Token;
        input.Add(new LabWorkerRequest { Id = 3, Op = "ack", Session = session, Token = token });
        Assert.True(output.Next().Ok);
        input.Add(new LabWorkerRequest
            { Id = 4, Op = "call", Session = session, Method = nameof(IBlockingService.Block) });

        try
        {
            Assert.True(service.Entered.Wait(TimeSpan.FromSeconds(5)));
            var tick = Task.Run(host.ZeroStale);
            await tick.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(0, service.Zeros);
            input.Add(new LabWorkerRequest
                { Id = 5, Op = "call", Session = session, Method = nameof(IBlockingService.Write) });
            input.Add(new LabWorkerRequest { Id = 6, Op = "open", Service = "test" });
            input.Complete();
            Assert.True(service.Cancelled.Wait(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            input.Complete();
            service.Unblock.Set();
            await serving.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(0, await serving);
        Assert.Equal(0, service.Writes);
        Assert.Equal(1, opens);
        Assert.Equal(1, service.Zeros);
        Assert.True(service.Disposed);
    }

    internal interface IBlockingService : IDisposable
    {
        [LabWorkerSnapshot]
        int Original();

        void Block(CancellationToken cancellationToken);

        [LabWorkerWrite]
        void Write();

        [LabWorkerZero]
        void Zero();
    }

    private sealed class BlockingService : IBlockingService
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Cancelled { get; } = new();
        public ManualResetEventSlim Unblock { get; } = new();
        public int Writes { get; private set; }
        public int Zeros { get; private set; }
        public bool Disposed { get; private set; }

        public int Original()
        {
            return 0;
        }

        public void Block(CancellationToken cancellationToken)
        {
            using var cancelled = cancellationToken.Register(() => Cancelled.Set());
            Entered.Set();
            if (!Unblock.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The test did not unblock its service.");
            }
        }

        public void Write()
        {
            Writes++;
        }

        public void Zero()
        {
            Zeros++;
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class RequestReader : TextReader
    {
        private readonly BlockingCollection<string> _lines = new();

        public void Add(LabWorkerRequest request)
        {
            _lines.Add(JsonSerializer.Serialize(request, LabWorkerHost.WireOptions));
        }

        public void Complete()
        {
            _lines.CompleteAdding();
        }

        public override string? ReadLine()
        {
            return _lines.TryTake(out var line, Timeout.Infinite) ? line : null;
        }

        protected override void Dispose(bool disposing)
        {
            _lines.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class ResponseWriter : TextWriter
    {
        private readonly BlockingCollection<LabWorkerResponse> _replies = new();
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? line)
        {
            _replies.Add(JsonSerializer.Deserialize<LabWorkerResponse>(line!, LabWorkerHost.WireOptions)!);
        }

        public LabWorkerResponse Next()
        {
            Assert.True(_replies.TryTake(out var reply, TimeSpan.FromSeconds(5)));
            return reply!;
        }

        protected override void Dispose(bool disposing)
        {
            _replies.Dispose();
            base.Dispose(disposing);
        }
    }
}
