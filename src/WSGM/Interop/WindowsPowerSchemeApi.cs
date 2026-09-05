using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace WSGM.Interop;

internal interface IPowerSchemeApi
{
    Guid? Enumerate(uint index);
    string ReadName(Guid id);
    Guid ReadActive();
    void SetActive(Guid id);
}

/// <summary>Owns powrprof buffers and preserves native failures as Win32Exception codes.</summary>
internal sealed class WindowsPowerSchemeApi : IPowerSchemeApi
{
    private const uint ErrorNoMoreItems = 259;
    private const uint ErrorMoreData = 234;
    private const uint ErrorInvalidData = 13;
    private const uint AccessScheme = 16;
    private const uint MaximumNameBytes = 65536;

    public Guid? Enumerate(uint index)
    {
        uint size = 16;
        uint status = NativeMethods.PowerEnumerate(0, 0, 0, AccessScheme, index, out Guid id, ref size);
        if (status == ErrorNoMoreItems)
        {
            return null;
        }
        Check(status, "PowerEnumerate");
        if (size != 16 || id == Guid.Empty)
        {
            throw Failure(ErrorInvalidData, "PowerEnumerate");
        }
        return id;
    }

    public unsafe string ReadName(Guid id)
    {
        uint size = 0;
        uint status = NativeMethods.PowerReadFriendlyName(0, in id, 0, 0, 0, ref size);
        if (status != ErrorMoreData)
        {
            Check(status, "PowerReadFriendlyName");
        }
        // A rename can grow the buffer between reads. Retry reads only, with a bounded allocation.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (size < 2 || size > MaximumNameBytes || size % 2 != 0)
            {
                throw Failure(ErrorInvalidData, "PowerReadFriendlyName");
            }
            byte[] buffer = new byte[size];
            fixed (byte* pointer = buffer)
            {
                status = NativeMethods.PowerReadFriendlyName(0, in id, 0, 0, (nint)pointer, ref size);
            }
            if (status == ErrorMoreData)
            {
                continue;
            }
            Check(status, "PowerReadFriendlyName");
            return DecodeName(buffer, size, id);
        }
        throw Failure(ErrorMoreData, "PowerReadFriendlyName");
    }

    internal static string DecodeName(byte[] buffer, uint size, Guid id)
    {
        if (size < 2 || size > buffer.Length || size % 2 != 0
            || buffer[(int)size - 2] != 0 || buffer[(int)size - 1] != 0)
        {
            throw Failure(ErrorInvalidData, "PowerReadFriendlyName");
        }
        string name = Encoding.Unicode.GetString(buffer, 0, (int)size - 2);
        return string.IsNullOrWhiteSpace(name) ? id.ToString("D") : name;
    }

    public Guid ReadActive()
    {
        uint status = NativeMethods.PowerGetActiveScheme(0, out nint pointer);
        try
        {
            Check(status, "PowerGetActiveScheme");
            if (pointer == 0)
            {
                throw Failure(ErrorInvalidData, "PowerGetActiveScheme");
            }
            Guid id = Marshal.PtrToStructure<Guid>(pointer);
            if (id == Guid.Empty)
            {
                throw Failure(ErrorInvalidData, "PowerGetActiveScheme");
            }
            return id;
        }
        finally
        {
            if (pointer != 0)
            {
                NativeMethods.LocalFree(pointer);
            }
        }
    }

    public void SetActive(Guid id) => Check(NativeMethods.PowerSetActiveScheme(0, in id), "PowerSetActiveScheme");

    private static void Check(uint status, string operation)
    {
        if (status != 0)
        {
            throw Failure(status, operation);
        }
    }

    private static Win32Exception Failure(uint status, string operation)
        => new(unchecked((int)status), $"{operation} failed (status {status}).");
}
