using System.ComponentModel;
using System.Text;
using WSGM.Core;
using WSGM.Interop;

namespace WSGM.Tests;

public sealed class PowerSchemesTests
{
    private static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid Custom = new("18cd759e-f610-4cb9-8458-22238e028503");

    [Fact]
    public void EnumeratesCustomSchemesAndDuplicateLocalizedNamesByGuid()
    {
        FakeApi api = new();
        api.Schemes.Add(new(Balanced, "Ausbalanciert"));
        api.Schemes.Add(new(Custom, "Ausbalanciert"));

        var schemes = new PowerSchemes(api).Enumerate();

        Assert.Equal([Balanced, Custom], schemes.Select(scheme => scheme.Id));
        Assert.All(schemes, scheme => Assert.Equal("Ausbalanciert", scheme.Name));
        Assert.Equal(0, api.Writes);
    }

    [Fact]
    public void EmptyEnumerationIsNotAnInventedDefault()
        => Assert.Empty(new PowerSchemes(new FakeApi()).Enumerate());

    [Fact]
    public void EnumerationFailureDoesNotReturnAPartialList()
    {
        FakeApi api = new() { EnumerationFailureIndex = 1 };
        api.Schemes.Add(new(Balanced, "Balanced"));

        var error = Assert.Throws<Win32Exception>(() => new PowerSchemes(api).Enumerate());

        Assert.Equal(5, error.NativeErrorCode);
    }

    [Fact]
    public void ReadsWindowsAgainAfterAnExternalSelection()
    {
        FakeApi api = new() { Active = Balanced };
        PowerSchemes schemes = new(api);
        Assert.Equal(Balanced, schemes.ReadActive());
        api.Active = Custom;
        Assert.Equal(Custom, schemes.ReadActive());
        Assert.Equal(0, api.Writes);
    }

    [Fact]
    public void SelectionWritesOnceThenVerifiesWindows()
    {
        FakeApi api = new() { Active = Balanced };

        new PowerSchemes(api).Select(Custom);

        Assert.Equal(Custom, api.Active);
        Assert.Equal(["write", "read"], api.Calls);
        Assert.Equal(1, api.Writes);
    }

    [Fact]
    public void EmptyGuidIsRejectedBeforeNativeAccess()
    {
        FakeApi api = new();
        Assert.Throws<ArgumentException>(() => new PowerSchemes(api).Select(Guid.Empty));
        Assert.Empty(api.Calls);
    }

    [Fact]
    public void CancelledSelectionDoesNotStartANativeWrite()
    {
        FakeApi api = new();
        Assert.Throws<OperationCanceledException>(() =>
            new PowerSchemes(api).Select(Custom, new CancellationToken(canceled: true)));
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task SelectionWaitsUntilTheTimeoutMutationReleasesTheSharedGate()
    {
        FakeApi api = new() { Active = Balanced };
        using ManualResetEventSlim releaseTimeout = new(false);
        TaskCompletionSource timeoutEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource selectionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task timeout = Task.Run(() =>
        {
            lock (PowerSchemes.MutationGate)
            {
                timeoutEntered.SetResult();
                if (!releaseTimeout.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException();
                }
                api.Active = Balanced;
            }
        });
        Task? selection = null;
        try
        {
            await timeoutEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            selection = Task.Run(() =>
            {
                selectionStarted.SetResult();
                new PowerSchemes(api).Select(Custom);
            });
            await selectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, api.Writes);
        }
        finally { releaseTimeout.Set(); }
        await timeout;
        if (selection is not null) { await selection; }
        Assert.Equal(Custom, api.Active);
        Assert.Equal(1, api.Writes);
    }

    [Fact]
    public void RejectedWriteKeepsItsNativeErrorAndIsNeverRetried()
    {
        FakeApi api = new() { WriteFailure = new Win32Exception(5), Active = Balanced };

        var error = Assert.Throws<Win32Exception>(() => new PowerSchemes(api).Select(Custom));

        Assert.Equal(5, error.NativeErrorCode);
        Assert.Equal(Balanced, api.Active);
        Assert.Equal(["write"], api.Calls);
        Assert.Equal(1, api.Writes);
    }

    [Fact]
    public void FailedReadbackDoesNotRepeatOrUndoThePossiblySuccessfulWrite()
    {
        FakeApi api = new() { ReadFailure = new Win32Exception(2), Active = Balanced };

        var error = Assert.Throws<Win32Exception>(() => new PowerSchemes(api).Select(Custom));

        Assert.Equal(2, error.NativeErrorCode);
        Assert.Equal(Custom, api.Active);
        Assert.Equal(1, api.Writes);
    }

    [Fact]
    public void ConflictingReadbackIsUnconfirmedAndIsNeverForcedBack()
    {
        FakeApi api = new() { IgnoreWrite = true, Active = Balanced };

        var error = Assert.Throws<InvalidOperationException>(() => new PowerSchemes(api).Select(Custom));

        Assert.Contains(Custom.ToString("D"), error.Message, StringComparison.Ordinal);
        Assert.Contains(Balanced.ToString("D"), error.Message, StringComparison.Ordinal);
        Assert.Equal(Balanced, api.Active);
        Assert.Equal(1, api.Writes);
    }

    [Theory]
    [InlineData("Höchstleistung")]
    [InlineData("省電力")]
    public void DecodesLocalizedUtf16NamesUsingTheReturnedByteCount(string name)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(name + "\0ignored padding");
        Assert.Equal(name, WindowsPowerSchemeApi.DecodeName(bytes, (uint)(name.Length + 1) * 2, Custom));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(10)]
    public void RejectsMalformedNameLengthsOrMissingTerminators(uint size)
    {
        byte[] bytes = Encoding.Unicode.GetBytes("abc\0");
        var error = Assert.Throws<Win32Exception>(() => WindowsPowerSchemeApi.DecodeName(bytes, size, Custom));
        Assert.Equal(13, error.NativeErrorCode);
    }

    [Fact]
    public void EmptyNameFallsBackToStableGuid()
        => Assert.Equal(Custom.ToString("D"), WindowsPowerSchemeApi.DecodeName([0, 0], 2, Custom));

    private sealed class FakeApi : IPowerSchemeApi
    {
        internal List<PowerScheme> Schemes { get; } = [];
        internal List<string> Calls { get; } = [];
        internal Guid Active { get; set; }
        internal bool IgnoreWrite { get; init; }
        internal uint? EnumerationFailureIndex { get; init; }
        internal Win32Exception? WriteFailure { get; init; }
        internal Win32Exception? ReadFailure { get; init; }
        internal int Writes { get; private set; }

        public Guid? Enumerate(uint index)
        {
            if (index == EnumerationFailureIndex)
            {
                throw new Win32Exception(5);
            }
            return index < Schemes.Count ? Schemes[(int)index].Id : null;
        }

        public string ReadName(Guid id) => Schemes.Single(scheme => scheme.Id == id).Name;

        public Guid ReadActive()
        {
            Calls.Add("read");
            if (ReadFailure is not null)
            {
                throw ReadFailure;
            }
            return Active;
        }

        public void SetActive(Guid id)
        {
            Calls.Add("write");
            Writes++;
            if (WriteFailure is not null)
            {
                throw WriteFailure;
            }
            if (!IgnoreWrite)
            {
                Active = id;
            }
        }
    }
}
