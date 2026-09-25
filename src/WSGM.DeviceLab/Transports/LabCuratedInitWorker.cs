using System;
using System.Linq;
using System.Threading;
using WSGM.DeviceLab.Knowledge;
using WSGM.DeviceLab.Wizard;
using WSGM.DeviceLab.Worker;

namespace WSGM.DeviceLab.Transports;

/// <summary>The curated controller setup operations available through the worker.</summary>
internal interface ILabCuratedInitWorker : IDisposable
{
    /// <summary>Reads the mode before a change, or null for an unreadable button table.</summary>
    [LabWorkerSnapshot]
    int? Original();

    /// <summary>Sends the curated command once.</summary>
    [LabWorkerWrite]
    LabInitResult Send();

    /// <summary>Reads the current mode after sleep.</summary>
    int? CurrentMode();

    /// <summary>Restores a pending reversible mode change.</summary>
    [LabWorkerWrite]
    string? RecoverControllerMode();
}

/// <summary>Resolves a curated init from the worker's own reviewed knowledge base.</summary>
internal sealed class LabCuratedInitWorker : ILabCuratedInitWorker
{
    private readonly LabControllerInitPlan? _plan;

    private LabCuratedInitWorker(string? recordId)
    {
        if (recordId is null)
        {
            return;
        }

        var record = DeviceKnowledgeBase.Default.Records.FirstOrDefault(item => item.Id == recordId);
        _plan = LabControllerInit.For(record)
                ?? throw new InvalidOperationException("The confirmed record has no curated controller init.");
    }

    /// <summary>The worker service registration.</summary>
    public static LabWorkerService Service { get; } = new("curated-init", typeof(ILabCuratedInitWorker),
        (args, _) => new LabCuratedInitWorker(LabWorkerService.Arg<string?>(args, 0)));

    /// <inheritdoc />
    public void Dispose()
    {
    }

    /// <inheritdoc />
    public int? Original()
    {
        return _plan is null ? null : LabControllerInit.CurrentMode(_plan);
    }

    /// <inheritdoc />
    public LabInitResult Send()
    {
        return LabControllerInit.Send(Plan(), CancellationToken.None);
    }

    /// <inheritdoc />
    public int? CurrentMode()
    {
        return LabControllerInit.CurrentMode(Plan());
    }

    /// <inheritdoc />
    public string? RecoverControllerMode()
    {
        return LabControllerInit.RecoverControllerMode(
            CancellationToken.None);
    }

    private LabControllerInitPlan Plan()
    {
        return _plan
               ?? throw new InvalidOperationException("No curated init was opened.");
    }
}
