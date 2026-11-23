using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DiskJanitor.Services;

public sealed record ShellResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;

    /// <summary>The most useful single line to show the user when a command fails.</summary>
    public string Error =>
        (StdErr.Trim().Length > 0 ? StdErr : StdOut).Trim().Split('\n').LastOrDefault()?.Trim() is { Length: > 0 } line
            ? line
            : $"exit code {ExitCode}";
}

public static class Shell
{
    // pkexec gives root a minimal PATH, so also look in the usual places.
    static readonly string[] ExtraPaths = ["/usr/local/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin", "/snap/bin"];

    public static string? Which(string command)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Concat(ExtraPaths);
        return dirs.Select(d => Path.Combine(d, command)).FirstOrDefault(File.Exists);
    }

    public static async Task<ShellResult> RunAsync(string file, IEnumerable<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        psi.Environment["LC_ALL"] = "C";

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new ShellResult(-1, "", ex.Message);
        }

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }
        return new ShellResult(process.ExitCode, await stdout, await stderr);
    }
}
