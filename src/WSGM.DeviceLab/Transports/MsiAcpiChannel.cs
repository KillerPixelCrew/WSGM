using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading.Tasks;

namespace WSGM.DeviceLab.Transports;

/// <summary>
///     The one MSI_ACPI call path, shared by the worker transport (<see cref="LabMsiWmi" />) and the
///     compiled read probes. It follows the Claw plugin's <c>MsiWmiPlatform</c>, including methods
///     that declare no input package.
/// </summary>
internal sealed class MsiAcpiChannel : ILabMsiWmiChannel
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);
    private readonly ManagementObject _instance;
    private readonly ManagementClass _packageClass;
    private bool _stuck;

    private MsiAcpiChannel(ManagementObject instance, ManagementClass packageClass)
    {
        _instance = instance;
        _packageClass = packageClass;
    }

    /// <inheritdoc />
    public byte[] Get(string method, byte selector)
    {
        if (!method.StartsWith("Get_", StringComparison.Ordinal))
        {
            throw new ArgumentException("Only Get_* methods read.", nameof(method));
        }

        var package = new byte[LabMsiWmi.PackageLength];
        package[0] = selector;
        return Invoke(method, package);
    }

    /// <inheritdoc />
    public void Set(string method, byte[] package)
    {
        if (!method.StartsWith("Set_", StringComparison.Ordinal) || package.Length != LabMsiWmi.PackageLength)
        {
            throw new ArgumentException("Only 32-byte Set_* calls write.", nameof(method));
        }

        _ = Invoke(method, [.. package]);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_stuck)
        {
            // A call is still inside WMI; it keeps the objects alive and they are collected later.
            return;
        }

        _packageClass.Dispose();
        _instance.Dispose();
    }

    /// <summary>Opens the one active MSI_ACPI instance after checking the class has <c>Get_WMI</c>.</summary>
    /// <returns>The channel.</returns>
    public static MsiAcpiChannel Open()
    {
        using (ManagementClass definition = new(LabMsiWmi.Namespace, LabMsiWmi.ClassName, null))
        {
            if (definition.Methods.Cast<MethodData>().All(method => method.Name != "Get_WMI"))
            {
                throw new FileNotFoundException("MSI_ACPI has no Get_WMI method.");
            }
        }

        ManagementObject? found = null;
        using (ManagementObjectSearcher searcher = new(
                   LabMsiWmi.Namespace,
                   $"SELECT * FROM {LabMsiWmi.ClassName} WHERE Active = TRUE"))
        using (var candidates = searcher.Get())
        {
            foreach (var candidate in candidates)
            {
                if (found is not null)
                {
                    candidate.Dispose();
                    found.Dispose();
                    throw new InvalidDataException("More than one active MSI_ACPI instance.");
                }

                found = (ManagementObject)candidate;
            }
        }

        if (found is null)
        {
            throw new FileNotFoundException("No active MSI_ACPI instance.");
        }

        try
        {
            return new MsiAcpiChannel(found, new ManagementClass(LabMsiWmi.Namespace, "Package_32", null));
        }
        catch
        {
            found.Dispose();
            throw;
        }
    }

    // A call that does not return in time may still reach the firmware, so the channel refuses
    // every later call rather than stacking another on top of an unknown one.
    private byte[] Invoke(string method, byte[] request)
    {
        if (_stuck)
        {
            throw new IOException("An earlier MSI call did not finish, so no further call is made.");
        }

        var call = Task.Run(() => InvokeCore(method, request));
        if (Task.WhenAny(call, Task.Delay(CallTimeout)).GetAwaiter().GetResult() != call)
        {
            _stuck = true;
            throw new TimeoutException($"{method} did not answer within three seconds; its effect is unknown.");
        }

        return call.GetAwaiter().GetResult();
    }

    private byte[] InvokeCore(string method, byte[] request)
    {
        // Get_WMI and Get_EC take no input package on the A2VM; every addressed accessor does (see MsiWmiPlatform).
        using var input = _instance.GetMethodParameters(method);
        using var package = input is null ? null : _packageClass.CreateInstance();
        if (input is not null)
        {
            if (package is null)
            {
                throw new IOException("The Package_32 request could not be created.");
            }

            package["Bytes"] = request;
            input["Data"] = package;
        }

        using var output = _instance.InvokeMethod(method, input, null)
                           ?? throw new IOException($"{method} returned no response.");
        if (output["Data"] is not ManagementBaseObject returned)
        {
            throw new InvalidDataException($"{method} returned no Package_32 response.");
        }

        using (returned)
        {
            if (returned["Bytes"] is not byte[] { Length: LabMsiWmi.PackageLength } response)
            {
                throw new InvalidDataException($"{method} returned an invalid response.");
            }

            return response[0] == 0x01
                ? response
                : throw new InvalidDataException($"{method} returned status 0x{response[0]:X2}.");
        }
    }
}
