using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VmpControl.Core;

namespace VmpControl.Tests;

/// <summary>The supervision logic of the ssh tunnel, with a harmless stand-in program instead of ssh (the real ssh path cannot be tested here).</summary>
[TestClass]
public class TunnelManagerTests
{
    private static string PowerShell() => ProcessRunner.PowerShellPath();

    [TestMethod]
    public async Task AChildThatKeepsExiting_IsRestartedWithBackoff_AndStatusSaysSo()
    {
        using var tunnel = new TunnelManager(PowerShell(), new[] { "-NoProfile", "-Command", "exit 3" });
        tunnel.Start();

        await Task.Delay(TimeSpan.FromSeconds(6));

        Assert.IsTrue(tunnel.StartCount >= 2, "restarted at least once, StartCount=" + tunnel.StartCount);
        Assert.IsTrue(tunnel.StartCount <= 4, "but with a growing delay, not in a tight loop, StartCount=" + tunnel.StartCount);
        StringAssert.StartsWith(tunnel.Status, "down - retrying in ");
    }

    [TestMethod]
    public async Task ARunningChild_IsUp_AndIsKilledOnDispose()
    {
        var tunnel = new TunnelManager(PowerShell(), new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 120" });
        tunnel.Start();
        await Task.Delay(TimeSpan.FromSeconds(5));

        Assert.AreEqual("up", tunnel.Status);
        var pid = tunnel.ProcessId;
        Assert.IsNotNull(pid);
        using var child = Process.GetProcessById(pid.Value);
        Assert.IsFalse(child.HasExited);

        tunnel.Dispose();

        Assert.IsTrue(child.WaitForExit(5000), "the child process must be gone after Dispose");
    }

    [TestMethod]
    public async Task AProgramThatDoesNotExist_KeepsRetryingInsteadOfCrashing()
    {
        using var tunnel = new TunnelManager(@"C:\definitely\not\here.exe", new[] { "-N" });
        tunnel.Start();

        await Task.Delay(TimeSpan.FromSeconds(2));

        StringAssert.StartsWith(tunnel.Status, "down - retrying");
        Assert.IsFalse(string.IsNullOrEmpty(tunnel.LastError));
    }
}

/// <summary>Runs against a real server only when VMPCONTROL_LIVE_FOLDER names a server folder that is running with the admin API (e.g. the scratch server); otherwise skipped.</summary>
[TestClass]
public class LiveServerTests
{
    [TestMethod]
    public async Task HostModeSession_TalksToARealServer()
    {
        var folder = Environment.GetEnvironmentVariable("VMPCONTROL_LIVE_FOLDER");
        if (string.IsNullOrEmpty(folder)) Assert.Inconclusive("set VMPCONTROL_LIVE_FOLDER to a running server folder to run this");
        var port = int.Parse(Environment.GetEnvironmentVariable("VMPCONTROL_LIVE_PORT") ?? "18900");
        using var session = new LiveSession(new Profile { Name = "live", Mode = ProfileMode.Host, ServerFolder = folder, ApiPort = port });
        var ct = CancellationToken.None;

        var snapshot = await session.PollAsync(false, ct);
        Assert.AreEqual(ApiAvailability.Ok, snapshot.Api, snapshot.ApiDetail);
        Assert.IsTrue(snapshot.Status.VesselCount > 0);
        Assert.IsNotNull(snapshot.Status.Machine?.RamTotalBytes, "the new server build reports the machine block");
        Assert.IsTrue(snapshot.Status.Machine.DiskFreeBytes > 0);
        Assert.AreNotEqual(TrayState.Connecting, TrayStateMapper.Map(snapshot));

        Assert.IsTrue((await session.GetNetworkAsync(ct)).IsOk);
        var log = await session.GetLogAsync(50, ct);
        Assert.IsTrue(log.IsOk && log.Value.Lines.Count > 0);
        Assert.IsTrue((await session.GetReportsAsync(ct)).IsOk);
        Assert.IsTrue((await session.GetBackupsAsync(ct)).IsOk);

        var say = await session.SayAsync("VMP Control live test", ct);
        Assert.IsTrue(say.IsOk, say.Error);
        var kick = await session.KickAsync("nobody-with-this-name", "test", ct);
        Assert.AreEqual(ApiOutcome.Failed, kick.Outcome);
        Assert.AreEqual("player not found", kick.Error);

        var backup = await session.CreateBackupAsync(ct);
        Assert.IsTrue(backup.IsOk, backup.Error);
        Assert.IsTrue(backup.Value.RawBytes > backup.Value.CompressedBytes);
        Assert.IsTrue(backup.Value.Verified);
        var listed = await session.GetBackupsAsync(ct);
        Assert.IsTrue(listed.Value.Backups.Any(b => b.Name == backup.Value.FileName));
        Assert.IsFalse(session.Log.Snapshot().Any(e => (e.Action + e.Result).Contains(File.ReadAllText(Path.Combine(folder, "Config", "admin-token.txt")).Trim())));
    }

    [TestMethod]
    public async Task HostModeSession_WithNothingRunning_ReportsUnreachable_AndFallsBackToTheScript()
    {
        var empty = Path.Combine(Path.GetTempPath(), "vmpcontrol-nolive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(empty, "Config"));
        try
        {
            File.WriteAllText(Path.Combine(empty, "Config", "admin-token.txt"), "0123456789abcdef0123456789abcdef");
            // A stand-in Manage-VmpServer.ps1 that reports a stopped server in the JSON the real one prints.
            File.WriteAllText(Path.Combine(empty, "Manage-VmpServer.ps1"), "param([string]$Action)\r\n'{\"Running\":false,\"ProcessId\":null}'\r\n");
            using var session = new LiveSession(new Profile { Name = "nothing", Mode = ProfileMode.Host, ServerFolder = empty, ApiPort = 1 });

            var snapshot = await session.PollAsync(false, CancellationToken.None);

            Assert.AreEqual(ApiAvailability.Unreachable, snapshot.Api);
            Assert.AreEqual(false, snapshot.Script.Running);
            Assert.AreEqual(TrayState.Down, TrayStateMapper.Map(snapshot));
        }
        finally
        {
            Directory.Delete(empty, true);
        }
    }
}
