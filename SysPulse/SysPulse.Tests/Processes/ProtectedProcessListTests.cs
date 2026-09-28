using SysPulse.Core.Processes;
using SysPulse.Core.Settings;

namespace SysPulse.Tests.Processes;

/// <summary>Covers spec §8 test item 8: protected list case-insensitivity, .exe suffix, extra names, own PID.</summary>
public class ProtectedProcessListTests
{
    private const int OwnPid = 4242;

    private static ProtectedProcessList Create(SysPulseOptions? options = null) =>
        ProtectedProcessList.Create(options ?? new SysPulseOptions(), OwnPid);

    [Theory]
    [InlineData("svchost")]
    [InlineData("svchost.exe")]
    [InlineData("SVCHOST")]
    [InlineData("SVCHOST.EXE")]
    [InlineData("SvcHost.Exe")]
    public void BuiltInName_IsProtected_CaseInsensitiveWithOrWithoutExeSuffix(string queried)
    {
        var list = Create();

        Assert.True(list.IsProtected(queried, pid: 999));
    }

    [Theory]
    [InlineData("System")]
    [InlineData("Idle")]
    [InlineData("Registry")]
    [InlineData("smss")]
    [InlineData("csrss")]
    [InlineData("wininit")]
    [InlineData("winlogon")]
    [InlineData("services")]
    [InlineData("lsass")]
    [InlineData("dwm")]
    [InlineData("fontdrvhost")]
    [InlineData("Memory Compression")]
    [InlineData("MsMpEng")]
    [InlineData("Microsoft.CmdPal.UI")]
    [InlineData("PowerToys")]
    [InlineData("explorer")]
    [InlineData("SysPulse")]
    public void EverySpecMandatedBuiltInName_IsProtected(string name)
    {
        var list = Create();

        Assert.True(list.IsProtected(name, pid: 999));
        Assert.True(list.IsProtected(name + ".exe", pid: 999));
    }

    [Fact]
    public void UnrelatedName_IsNotProtected()
    {
        var list = Create();

        Assert.False(list.IsProtected("chrome.exe", pid: 999));
    }

    [Fact]
    public void OwnProcessId_IsProtected_EvenWithUnrelatedName()
    {
        var list = Create();

        Assert.True(list.IsProtected("some-unrelated-name.exe", OwnPid));
    }

    [Fact]
    public void ExtraNameFromOptions_IsProtected_CaseInsensitiveWithOrWithoutExeSuffix()
    {
        var options = new SysPulseOptions { ProtectedProcesses = "MyBackupTool.exe, othertool" };
        var list = Create(options);

        Assert.True(list.IsProtected("mybackuptool", pid: 1));
        Assert.True(list.IsProtected("MYBACKUPTOOL.EXE", pid: 1));
        Assert.True(list.IsProtected("OtherTool.exe", pid: 1));
    }

    [Fact]
    public void ExtraNameFromOptions_DoesNotAffectUnrelatedNames()
    {
        var options = new SysPulseOptions { ProtectedProcesses = "MyBackupTool" };
        var list = Create(options);

        Assert.False(list.IsProtected("chrome.exe", pid: 1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankName_IsNotProtected_UnlessPidMatches(string? name)
    {
        var list = Create();

        Assert.False(list.IsProtected(name!, pid: 999));
        Assert.True(list.IsProtected(name!, OwnPid));
    }
}
