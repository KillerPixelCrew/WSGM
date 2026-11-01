namespace WSGM.Shell;

/// <summary>The manager's running work, published apart from the page projection.</summary>
/// <param name="Busy">Whether an operation owns the manager.</param>
/// <param name="Status">The operation's line, or the last result.</param>
internal sealed record EmulatorProgressState(bool Busy, string Status)
{
    /// <summary><c>install</c>, <c>update</c>, <c>repair</c>, <c>updateAll</c>, <c>refresh</c> or empty.</summary>
    public string Operation { get; init; } = "";

    /// <summary>
    ///     The definition an install works on, or the installation an update or repair works on; during Update all,
    ///     the installation updating now.
    /// </summary>
    public string TargetId { get; init; } = "";

    /// <summary>The install transaction's step: <c>find</c>, <c>download</c>, <c>extract</c>, <c>check</c> or empty.</summary>
    public string Phase { get; init; } = "";

    /// <summary>Bytes of the package downloaded so far during the download step.</summary>
    public long Bytes { get; init; }

    /// <summary>The package's published size, or zero when unknown, which leaves the meter indeterminate.</summary>
    public long Total { get; init; }
}
