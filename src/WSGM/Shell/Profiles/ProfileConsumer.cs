using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>One consumer of profile changes, applied in the order it was registered.</summary>
/// <param name="Name">Name for the log.</param>
/// <param name="ApplyAsync">Applies the resolved values of a snapshot.</param>
internal sealed record ProfileConsumer(string Name, Func<ProfileSnapshot, CancellationToken, Task> ApplyAsync);
