namespace WSGM.Core;

/// <summary>One console invocation, including uncertain outcomes after process start.</summary>
/// <param name="Outcome">Whether start was avoided, a known exit occurred, or effects remain uncertain.</param>
/// <param name="ExitCode">Observed exit status, or null when no exit status could be established.</param>
/// <param name="Output">Combined stdout and stderr, or empty when capture could not finish within the budget.</param>
internal sealed record ConsoleToolResult(ConsoleToolRunOutcome Outcome, int? ExitCode, string Output);
