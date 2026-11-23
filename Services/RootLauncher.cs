using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace DiskJanitor.Services;

/// <summary>
/// Restarts the app as root through pkexec, passing along the display and the real user so the
/// elevated copy still cleans this user's home. The elevated copy deletes a "handoff" file once
/// its window is open; that is how we know it is safe to close this one.
/// </summary>
public static class RootLauncher
{
    public const string HandoffArg = "--handoff";

    /// <returns>null when the elevated instance is up, otherwise a message explaining what went wrong.</returns>
    public static async Task<string?> RelaunchAsync()
    {
        var pkexec = Shell.Which("pkexec");
        if (pkexec is null)
            return "pkexec is not installed. Start Disk Janitor with run-as-root.sh from a terminal instead.";

        // Allow root to draw on this user's X/XWayland display.
        if (Shell.Which("xhost") is { } xhost)
            await Shell.RunAsync(xhost, ["+SI:localuser:root"]);

        var handoff = Path.Combine(Path.GetTempPath(), $"diskjanitor-{Guid.NewGuid():N}.handoff");
        await File.WriteAllTextAsync(handoff, "");

        var args = new List<string> { "env" };
        foreach (var name in new[] { "DISPLAY", "XAUTHORITY" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                args.Add($"{name}={value}");
        if (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 } wayland)
        {
            var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? $"/run/user/{UserContext.Uid}";
            args.Add($"WAYLAND_DISPLAY={(Path.IsPathRooted(wayland) ? wayland : Path.Combine(runtimeDir, wayland))}");
        }
        args.Add($"DISKJANITOR_USER={UserContext.UserName}");
        if (DotnetRoot() is { } dotnetRoot)
            args.Add($"DOTNET_ROOT={dotnetRoot}");
        args.Add("NO_AT_BRIDGE=1");

        var (exe, exeArgs) = CurrentCommand();
        args.Add(exe);
        args.AddRange(exeArgs);
        args.Add(HandoffArg);
        args.Add(handoff);

        var psi = new ProcessStartInfo(pkexec) { UseShellExecute = false };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            TryDelete(handoff);
            return $"Could not start pkexec: {ex.Message}";
        }
        if (process is null)
            return "Could not start pkexec.";

        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromMinutes(3))
        {
            if (!File.Exists(handoff))
                return null;
            if (process.HasExited)
            {
                TryDelete(handoff);
                return process.ExitCode is 126 or 127
                    ? "Authentication was cancelled."
                    : $"The root instance exited with code {process.ExitCode}. Try run-as-root.sh from a terminal to see why.";
            }
            await Task.Delay(300);
        }
        return "Timed out waiting for the root instance to start.";
    }

    /// <summary>Called by the elevated instance once its window is visible.</summary>
    public static void CompleteHandoff(string[] args)
    {
        var index = Array.IndexOf(args, HandoffArg);
        if (index >= 0 && index + 1 < args.Length)
            TryDelete(args[index + 1]);
    }

    static (string Exe, string[] Args) CurrentCommand()
    {
        // Inside an AppImage the binary lives on a FUSE mount that root cannot read; relaunch the
        // .AppImage file itself instead.
        if (Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage && File.Exists(appImage))
            return (appImage, []);
        var exe = Environment.ProcessPath ?? "dotnet";
        return Path.GetFileNameWithoutExtension(exe) == "dotnet"
            ? (exe, [Assembly.GetEntryAssembly()!.Location])
            : (exe, []);
    }

    /// <returns>null for a self-contained single-file build, which carries its own runtime.</returns>
    static string? DotnetRoot()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root)
            return root;
        // .../dotnet/shared/Microsoft.NETCore.App/<version>/System.Private.CoreLib.dll
        var coreLib = typeof(object).Assembly.Location;
        if (coreLib.Length == 0)
            return null;
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(coreLib)!, "..", "..", ".."));
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
