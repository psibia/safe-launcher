using Xunit;
using Svc = SafeLauncher.Service;

namespace KillSwitchLauncher.Tests;

/// <summary>Служба: какие программы считаются программами служб (их сеть лаунчер не ограничивает).</summary>
public class NetworkGuardTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\AnyDesk\\AnyDesk.exe\" --service", "C:\\Program Files\\AnyDesk\\AnyDesk.exe")]
    [InlineData("C:\\Program Files\\Vendor App\\agent.exe /svc", "C:\\Program Files\\Vendor App\\agent.exe")]
    [InlineData("\\??\\C:\\Tools\\svc.exe", "C:\\Tools\\svc.exe")]
    [InlineData("C:\\Tools\\a.exe.d\\svc.exe -k run", "C:\\Tools\\a.exe.d\\svc.exe")]
    [InlineData("C:\\Tools\\..\\Tools\\svc.EXE", "C:\\Tools\\svc.EXE")]
    [InlineData("system32\\svchost.exe -k netsvcs", null)] // программы Windows и так под защитой
    [InlineData("%SystemRoot%\\system32\\svchost.exe -k netsvcs", null)]
    [InlineData("\\SystemRoot\\System32\\drivers\\tcpip.sys", null)]
    [InlineData("\\\\server\\share\\svc.exe", null)]
    [InlineData("\"C:\\Broken\\svc.exe", null)]
    [InlineData("", null)]
    public void ServiceImage_ParsedFromRegistryValue(string imagePath, string? expected)
    {
        Assert.Equal(expected, Svc.NetworkGuard.ServiceImage(imagePath));
    }
}
