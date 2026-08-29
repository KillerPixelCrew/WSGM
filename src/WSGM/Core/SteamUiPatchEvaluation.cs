using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>
/// The one place a patch turns an injected expression into a patch result.
/// </summary>
/// <remarks>
/// Every WSGM patch injects a self-contained expression that returns
/// <c>JSON.stringify({ok:…,error:…})</c>, so the parse is the same everywhere. It lived in three
/// copies that had already drifted: two returned only the caller's fallback text when the page
/// answered with a shape that had no <c>error</c> string, which is exactly the case a remote log
/// needs to see. Reading the page's own answer is the useful behaviour, so it is what all patches
/// now do.
/// </remarks>
internal static class SteamUiPatchEvaluation
{
    private const int MaxDiagnosticLength = 2048;

    /// <summary>Evaluates an expression and reads its <c>ok</c>/<c>error</c> outcome.</summary>
    /// <param name="context">The patch context to evaluate through.</param>
    /// <param name="role">Which Steam target to evaluate in.</param>
    /// <param name="expression">The self-contained expression to evaluate.</param>
    /// <param name="fallback">Diagnostic used when the page reported nothing usable.</param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    /// <returns>The patch operation result.</returns>
    internal static async Task<SteamUiPatchOperationResult> EvaluateOutcomeAsync(
        SteamUiPatchContext context,
        SteamUiTargetRole role,
        string expression,
        string fallback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        SteamUiEvaluationResult result = await context.EvaluateAsync(
            role,
            expression,
            cancellationToken).ConfigureAwait(false);
        if (!result.Reachable || result.Value is null)
        {
            return new SteamUiPatchOperationResult(false, result.Error ?? fallback);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.Value);
            JsonElement root = document.RootElement;
            bool succeeded = root.TryGetProperty("ok", out JsonElement ok)
                && ok.ValueKind == JsonValueKind.True;
            if (succeeded)
            {
                return new SteamUiPatchOperationResult(true, null);
            }

            string? reported = root.TryGetProperty("error", out JsonElement error)
                && error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;
            return new SteamUiPatchOperationResult(
                false,
                reported ?? Bounded(result.Value) ?? fallback);
        }
        catch (JsonException ex)
        {
            return new SteamUiPatchOperationResult(false, ex.Message);
        }
    }

    /// <summary>Whether an injected expression reported success.</summary>
    /// <param name="value">The raw JSON the expression returned.</param>
    /// <returns><see langword="true"/> when the page reported <c>ok</c>.</returns>
    /// <remarks>Unparseable output is a failure, never an optimistic success.</remarks>
    internal static bool IsSuccessful(string value)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            return document.RootElement.TryGetProperty("ok", out JsonElement ok)
                && ok.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Whether a probe counted exactly one match for a required structural token set.</summary>
    /// <param name="root">The parsed probe result.</param>
    /// <param name="property">The count property to check.</param>
    /// <returns><see langword="true"/> when the count is exactly one.</returns>
    /// <remarks>
    /// Exactly one, never "at least one": a second match means the Steam build has two candidate
    /// components and the patch cannot tell which one it would be modifying.
    /// </remarks>
    internal static bool IsOne(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int count)
        && count == 1;

    /// <summary>Truncates a page-supplied diagnostic to a bounded length.</summary>
    /// <param name="value">The raw diagnostic.</param>
    /// <returns>The bounded diagnostic, or null when there was nothing to report.</returns>
    internal static string? Bounded(string? value) => value switch
    {
        null or "" => null,
        { Length: <= MaxDiagnosticLength } => value,
        _ => value[..MaxDiagnosticLength] + "...",
    };
}
