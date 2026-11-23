using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DiskJanitor.Services;

/// <summary>
/// Works out whose disk we are cleaning. When started through sudo/pkexec we run as root,
/// but "home", caches and projects must still be those of the user who launched us.
/// </summary>
public static class UserContext
{
    sealed record PasswdEntry(string Name, int Uid, string Home);

    public static bool IsRoot { get; }
    public static string UserName { get; }
    public static string Home { get; }
    public static int Uid { get; }

    static UserContext()
    {
        IsRoot = ReadEffectiveUid() == 0;
        var passwd = ReadPasswd();

        string? name = null;
        if (IsRoot)
        {
            name = Env("DISKJANITOR_USER") ?? Env("SUDO_USER");
            if (name is null && int.TryParse(Env("PKEXEC_UID"), out var pkUid))
                name = passwd.FirstOrDefault(e => e.Uid == pkUid)?.Name;
        }
        name ??= Environment.UserName;

        var entry = passwd.FirstOrDefault(e => e.Name == name);
        UserName = name;
        Home = entry?.Home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Uid = entry?.Uid ?? ReadEffectiveUid();
    }

    /// <summary>Expands a leading "~" to the target user's home.</summary>
    public static string Expand(string path)
    {
        path = path.Trim();
        if (path == "~") return Home;
        return path.StartsWith("~/") ? Path.Combine(Home, path[2..]) : path;
    }

    /// <summary>Shortens a path under the target user's home to "~/...".</summary>
    public static string Pretty(string path) =>
        path.StartsWith(Home + "/") ? "~/" + path[(Home.Length + 1)..] : path;

    static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    static int ReadEffectiveUid()
    {
        try
        {
            var line = File.ReadLines("/proc/self/status").First(l => l.StartsWith("Uid:"));
            return int.Parse(line.Split('\t', StringSplitOptions.RemoveEmptyEntries)[2]);
        }
        catch
        {
            return Environment.UserName == "root" ? 0 : -1;
        }
    }

    static List<PasswdEntry> ReadPasswd()
    {
        try
        {
            return File.ReadLines("/etc/passwd")
                .Select(l => l.Split(':'))
                .Where(p => p.Length >= 7 && int.TryParse(p[2], out _))
                .Select(p => new PasswdEntry(p[0], int.Parse(p[2]), p[5]))
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
