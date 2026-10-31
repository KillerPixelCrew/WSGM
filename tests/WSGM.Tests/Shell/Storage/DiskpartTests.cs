using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class DiskpartTests
{
    [Fact]
    public void PartitionScriptCleansAndCreatesOnePrimaryPartition()
    {
        Assert.Equal(
            "select disk 3\r\n"
            + "clean\r\n"
            + "create partition primary\r\n",
            Diskpart.BuildPartitionScript(3));
    }

    [Fact]
    public void PartitionScriptNeverFormats()
    {
        var script = Diskpart.BuildPartitionScript(3);

        Assert.DoesNotContain("format", script);
        Assert.DoesNotContain("assign", script);
    }

    [Fact]
    public void FormatScriptSelectsTheNewPartitionAndFormatsOnly()
    {
        Assert.Equal(
            "select disk 3\r\n"
            + "select partition 1\r\n"
            + "format fs=ntfs quick unit=128k label=\"Games\"\r\n",
            Diskpart.BuildFormatScript(3));
    }

    [Fact]
    public void FormatScriptNeverCleansOrAssigns()
    {
        var script = Diskpart.BuildFormatScript(3);

        Assert.DoesNotContain("clean", script);
        Assert.DoesNotContain("assign", script);
    }

    [Fact]
    public void DiskpartScriptQuotesTheGivenLabel()
    {
        Assert.Contains(
            "label=\"My Games\"\r\n",
            Diskpart.BuildFormatScript(1, "My Games"));
    }

    [Fact]
    public void AssignScriptPreservesTheCardsDriveLetter()
    {
        Assert.Equal(
            "select disk 3\r\n"
            + "select partition 1\r\n"
            + "assign letter=E\r\n",
            Diskpart.BuildAssignScript(3, 'E'));
    }

    [Fact]
    public void AssignScriptNeverCleansOrFormats()
    {
        var script = Diskpart.BuildAssignScript(3, 'E');

        Assert.DoesNotContain("clean", script);
        Assert.DoesNotContain("format", script);
    }

    [Fact]
    public void ALetterlessCardGetsABareAssign()
    {
        var script = Diskpart.BuildAssignScript(3, '\0');

        Assert.EndsWith("assign\r\n", script);
        Assert.DoesNotContain("assign letter=", script);
    }

    [Fact]
    public void DiskpartScriptNeverIssuesCleanAll()
    {
        Assert.DoesNotContain("clean all", Diskpart.BuildPartitionScript(0));
    }
}
