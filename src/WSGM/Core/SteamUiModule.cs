using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>One state value a module publishes to the injected side.</summary>
/// <param name="PatchId">The patch the injected side receives this state as.</param>
/// <param name="Enabled">Whether the value may be published right now. Evaluated per publish, so a
/// module can stay registered while its backend is unavailable.</param>
/// <param name="Read">Produces the current value, or <see langword="null"/> to publish nothing this
/// round — which is how a reading that is momentarily unavailable stays distinct from a zero.</param>
public sealed record SteamUiStatePublication(
    string PatchId,
    Func<bool> Enabled,
    Func<ValueTask<JsonElement?>> Read);

/// <summary>The outcome of one semantic command.</summary>
/// <param name="Succeeded">Whether the command changed what it claimed to change.</param>
/// <param name="Error">Why it did not, when it did not. Never null on failure: an unexplained
/// refusal is the defect this contract exists to prevent, because the injected side has nowhere to
/// put a reason and the user sees only a control that did nothing.</param>
/// <param name="Payload">An optional answer for commands that read rather than write.</param>
public readonly record struct SteamUiCommandResult(
    bool Succeeded,
    string? Error,
    JsonElement? Payload = null)
{
    /// <summary>The command applied.</summary>
    public static SteamUiCommandResult Applied { get; } = new(true, null);

    /// <summary>The backing service is not active, so the command was not attempted.</summary>
    public static SteamUiCommandResult Refused { get; } = new(
        false,
        "The requested semantic service is not active.");
}

/// <summary>Answers one semantic command from the injected side.</summary>
/// <param name="request">The bridge request, already validated and generation-checked.</param>
/// <param name="cancellationToken">Cancels the command.</param>
/// <returns>The truthful outcome, including a reason when nothing happened.</returns>
public delegate Task<SteamUiCommandResult> SteamUiCommandDelegate(
    SteamUiBridgeRequest request,
    CancellationToken cancellationToken);

/// <summary>One command a module answers.</summary>
/// <param name="PatchId">The patch the injected side addresses.</param>
/// <param name="Command">The command name within that patch.</param>
/// <param name="Handle">The handler.</param>
public sealed record SteamUiCommandHandler(
    string PatchId,
    string Command,
    SteamUiCommandDelegate Handle);

/// <summary>
/// One Steam UI surface, declared in one place: the patches that install it, the state it publishes,
/// and the commands it answers.
/// </summary>
/// <remarks>
/// This exists because a surface used to be four scattered edits — a patch registration, a
/// publication row, a command row and an id constant — so adding or removing one meant finding all
/// four and getting them consistent. A module is the unit those four belong to.
/// <para>
/// Registration order does not matter: the patch manager sorts by patch id, and publications and
/// commands are keyed rather than ordered.
/// </para>
/// </remarks>
public interface ISteamUiModule
{
    /// <summary>Stable module identity, for diagnostics and duplicate detection.</summary>
    string Id { get; }

    /// <summary>The patches that install and remove this surface. May be empty for a module that
    /// only answers commands against a surface another module installs.</summary>
    IReadOnlyList<ISteamUiPatch> Patches { get; }

    /// <summary>State this module pushes to the injected side.</summary>
    IReadOnlyList<SteamUiStatePublication> Publications { get; }

    /// <summary>Commands this module answers.</summary>
    IReadOnlyList<SteamUiCommandHandler> Commands { get; }
}

/// <summary>A module declared inline at its call site.</summary>
public sealed class SteamUiModule : ISteamUiModule
{
    /// <summary>Declares one surface.</summary>
    /// <param name="id">Stable module identity.</param>
    /// <param name="patches">Patches that install and remove the surface.</param>
    /// <param name="publications">State pushed to the injected side.</param>
    /// <param name="commands">Commands answered from the injected side.</param>
    public SteamUiModule(
        string id,
        IReadOnlyList<ISteamUiPatch>? patches = null,
        IReadOnlyList<SteamUiStatePublication>? publications = null,
        IReadOnlyList<SteamUiCommandHandler>? commands = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        Patches = patches ?? [];
        Publications = publications ?? [];
        Commands = commands ?? [];
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public IReadOnlyList<ISteamUiPatch> Patches { get; }

    /// <inheritdoc />
    public IReadOnlyList<SteamUiStatePublication> Publications { get; }

    /// <inheritdoc />
    public IReadOnlyList<SteamUiCommandHandler> Commands { get; }
}

/// <summary>The registered modules, flattened into the three lookups the host drives.</summary>
/// <remarks>
/// Flattening happens once, at construction, so a conflict between two modules is a startup failure
/// with both names in it rather than whichever one happened to win at runtime.
/// </remarks>
public sealed class SteamUiModuleSet
{
    private readonly Dictionary<(string PatchId, string Command), SteamUiCommandDelegate> _commands;

    /// <summary>Flattens a module list, rejecting duplicate identity.</summary>
    /// <param name="modules">The declared modules.</param>
    /// <exception cref="InvalidOperationException">Two modules share an id, register the same patch
    /// id, or answer the same patch and command.</exception>
    public SteamUiModuleSet(IReadOnlyList<ISteamUiModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        Modules = modules;

        var seenModules = new HashSet<string>(StringComparer.Ordinal);
        var patches = new List<ISteamUiPatch>();
        var seenPatches = new HashSet<string>(StringComparer.Ordinal);
        var publications = new List<SteamUiStatePublication>();
        _commands = [];

        foreach (ISteamUiModule module in modules)
        {
            if (!seenModules.Add(module.Id))
            {
                throw new InvalidOperationException(
                    $"Steam UI module '{module.Id}' is declared twice.");
            }
            foreach (ISteamUiPatch patch in module.Patches)
            {
                if (!seenPatches.Add(patch.Id))
                {
                    throw new InvalidOperationException(
                        $"Steam UI patch '{patch.Id}' is registered by more than one module; "
                        + $"'{module.Id}' is the second.");
                }
                patches.Add(patch);
            }
            publications.AddRange(module.Publications);
            foreach (SteamUiCommandHandler command in module.Commands)
            {
                if (!_commands.TryAdd((command.PatchId, command.Command), command.Handle))
                {
                    throw new InvalidOperationException(
                        $"Steam UI command '{command.PatchId}/{command.Command}' is answered by "
                        + $"more than one module; '{module.Id}' is the second.");
                }
            }
        }

        Patches = patches;
        Publications = publications;
    }

    /// <summary>The declared modules, in declaration order.</summary>
    public IReadOnlyList<ISteamUiModule> Modules { get; }

    /// <summary>Every patch across every module.</summary>
    public IReadOnlyList<ISteamUiPatch> Patches { get; }

    /// <summary>Every publication across every module.</summary>
    public IReadOnlyList<SteamUiStatePublication> Publications { get; }

    /// <summary>Finds the handler for one addressed command.</summary>
    /// <param name="patchId">The addressed patch.</param>
    /// <param name="command">The command name.</param>
    /// <param name="handler">The handler, when one is registered.</param>
    /// <returns><see langword="true"/> when a module answers this command.</returns>
    public bool TryGetCommand(
        string patchId,
        string command,
        out SteamUiCommandDelegate? handler)
        => _commands.TryGetValue((patchId, command), out handler);

    /// <summary>Registers every module's patches with the patch manager.</summary>
    /// <param name="patches">The manager that owns patch lifecycle.</param>
    public void RegisterPatches(SteamUiPatchManager patches)
    {
        ArgumentNullException.ThrowIfNull(patches);
        foreach (ISteamUiPatch patch in Patches)
        {
            patches.Register(patch);
        }
    }
}
