namespace WSGM.Shell;

/// <summary>The running operation.</summary>
/// <param name="AppId">The game it runs for.</param>
/// <param name="Operation"><c>install</c>, <c>replaceExisting</c>, <c>update</c>, <c>repair</c>, <c>configure</c> or <c>remove</c>.</param>
/// <param name="Phase">The current phase text.</param>
/// <param name="Bytes">Bytes downloaded, zero outside a download.</param>
/// <param name="Total">The expected download size, zero when unknown.</param>
/// <param name="Cancellable">Whether <c>cancel</c> can still stop it.</param>
internal sealed record GameToolsBusy(uint AppId, string Operation, string Phase, long Bytes, long Total,
    bool Cancellable);
