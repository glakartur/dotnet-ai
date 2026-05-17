using System.Diagnostics;
using DotnetAICraft.Daemon;
using DotnetAICraft.Diagnostics;
using Xunit;

namespace DotnetAICraft.Tests.Daemon;

public sealed class DaemonClientProcessStartTests
{
    [Fact]
    public void ApplySpawnedDaemonEnvironment_DoesNotSetDotnetAicraftDebug_EvenWhenGlobalVerboseEnabled()
    {
        var wasEnabled = DebugLog.IsEnabled;
        DebugLog.Configure(true);
        try
        {
            var startInfo = DaemonClient.CreateDaemonStartInfo("dotnet-aicraft", ["server", "daemon"]);
            startInfo.EnvironmentVariables["DOTNET_AICRAFT_DEBUG"] = "1";

            DaemonClient.ApplySpawnedDaemonEnvironment(startInfo);

            Assert.False(startInfo.EnvironmentVariables.ContainsKey("DOTNET_AICRAFT_DEBUG"));
        }
        finally
        {
            DebugLog.Configure(wasEnabled);
        }
    }

    [Fact]
    public void ApplySpawnedDaemonEnvironment_StripsInheritedDotnetAicraftDebug()
    {
        var startInfo = DaemonClient.CreateDaemonStartInfo("dotnet-aicraft", ["server", "daemon"]);
        startInfo.EnvironmentVariables["DOTNET_AICRAFT_DEBUG"] = "1";

        DaemonClient.ApplySpawnedDaemonEnvironment(startInfo);

        Assert.False(startInfo.EnvironmentVariables.ContainsKey("DOTNET_AICRAFT_DEBUG"));
    }

    [Fact]
    public void CreateDaemonStartInfo_ConfiguresSafeNonInheritingDefaults()
    {
        var startInfo = DaemonClient.CreateDaemonStartInfo("dotnet-aicraft", ["server", "daemon"]);

        Assert.Equal("dotnet-aicraft", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
    }

    [Fact]
    public void CreateDaemonStartInfo_PreservesArgumentOrder()
    {
        var args = new[] { "server", "daemon", "--solution", "/tmp/sample.sln", "--idle-timeout", "off" };
        var startInfo = DaemonClient.CreateDaemonStartInfo("dotnet-aicraft", args);

        Assert.Equal(args.Length, startInfo.ArgumentList.Count);
        for (var i = 0; i < args.Length; i++)
            Assert.Equal(args[i], startInfo.ArgumentList[i]);
    }

    [Fact]
    public async Task DrainProcessPipeAsync_CompletesAtEndOfStream()
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("echo drained");
        }
        else
        {
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("printf 'drained\\n'");
        }

        using var process = Process.Start(psi);
        Assert.NotNull(process);

        var drainTask = DaemonClient.DrainProcessPipeAsync(process!.StandardOutput);
        await process.WaitForExitAsync();
        await drainTask;

        Assert.True(drainTask.IsCompletedSuccessfully);
    }
}
