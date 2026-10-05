using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Worker;

/// <summary>One callable interface method and what its attributes say about it.</summary>
/// <param name="Info">The method.</param>
/// <param name="Write">Whether it changes hardware and needs an acknowledged checkpoint.</param>
/// <param name="Stream">Whether it is a streamed frame without a reply.</param>
/// <param name="Sampled">Whether it is polled at a steady rate and is not traced per call.</param>
internal sealed record LabWorkerMethod(MethodInfo Info, bool Write, bool Stream, bool Sampled);

/// <summary>A hardware service the worker may host: its interface, which is the call allowlist, and how to open it.</summary>
/// <remarks>
///     The interface is resolved once, here: an overloaded method name or more than one snapshot or zero
///     method throws at construction, so a bad registration fails when the worker starts, not when a call
///     arrives. <see cref="IDisposable.Dispose" /> is inherited, not declared, so it is never callable.
/// </remarks>
/// <param name="Name">Service name used by <c>open</c>.</param>
/// <param name="Interface">Interface whose methods are the only ones callable.</param>
/// <param name="Open">Opens the service from its arguments, logging into the given log.</param>
internal sealed record LabWorkerService(
    string Name,
    Type Interface,
    Func<IReadOnlyList<JsonElement>, LabPowerLog, object> Open)
{
    /// <summary>The callable methods by name.</summary>
    public IReadOnlyDictionary<string, LabWorkerMethod> Methods { get; } = Resolve(Name, Interface);

    /// <summary>The parameterless method that captures the original state, or null.</summary>
    public MethodInfo? Snapshot { get; } = Single<LabWorkerSnapshotAttribute>(Name, Interface);

    /// <summary>The parameterless method that puts a streamed output at rest, or null.</summary>
    public MethodInfo? Zero { get; } = Single<LabWorkerZeroAttribute>(Name, Interface);

    /// <summary>Reads one open argument as its declared type.</summary>
    /// <typeparam name="T">The argument type.</typeparam>
    /// <param name="args">The open arguments.</param>
    /// <param name="index">Which one.</param>
    /// <returns>The argument.</returns>
    public static T Arg<T>(IReadOnlyList<JsonElement> args, int index)
    {
        return index < args.Count
            ? args[index].Deserialize<T>(LabProject.JsonOptions)!
            : throw new ArgumentException($"The service needs open argument {index}.");
    }

    private static Dictionary<string, LabWorkerMethod> Resolve(string name, Type contract)
    {
        Dictionary<string, LabWorkerMethod> methods = [];
        foreach (var method in contract.GetMethods())
        {
            if (!methods.TryAdd(method.Name, new LabWorkerMethod(method,
                    method.GetCustomAttribute<LabWorkerWriteAttribute>() is not null,
                    method.GetCustomAttribute<LabWorkerStreamAttribute>() is not null,
                    method.GetCustomAttribute<LabWorkerSampledAttribute>() is not null)))
            {
                throw new InvalidOperationException(
                    $"{name}.{method.Name} is overloaded; worker methods are called by name only.");
            }
        }

        return methods;
    }

    private static MethodInfo? Single<TAttribute>(string name, Type contract)
        where TAttribute : Attribute
    {
        var marked = contract.GetMethods().Where(method => method.GetCustomAttribute<TAttribute>() is not null)
            .ToArray();
        return marked.Length <= 1
            ? marked.FirstOrDefault()
            : throw new InvalidOperationException($"{name} has more than one [{typeof(TAttribute).Name}] method.");
    }
}
