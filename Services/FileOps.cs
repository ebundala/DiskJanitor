using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Threading;
using DiskJanitor.Models;

namespace DiskJanitor.Services;

public static class FileOps
{
    static readonly EnumerationOptions Shallow = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Total size of all files below <paramref name="path"/>. Symlinks are never followed.</summary>
    public static long Measure(string path, CancellationToken ct, Func<string, bool>? fileFilter = null)
    {
        if (!Directory.Exists(path))
            return File.Exists(path) ? new FileInfo(path).Length : 0;

        long total = 0;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            try
            {
                var entries = new FileSystemEnumerable<(string? SubDir, long Length, string? Name)>(
                    dir,
                    (ref FileSystemEntry e) =>
                    {
                        if ((e.Attributes & FileAttributes.ReparsePoint) != 0)
                            return (null, 0, null);
                        if (e.IsDirectory)
                            return (e.ToFullPath(), 0, null);
                        return (null, e.Length, fileFilter is null ? null : e.FileName.ToString());
                    },
                    Shallow);
                foreach (var (subDir, length, name) in entries)
                {
                    if (subDir is not null)
                        pending.Push(subDir);
                    else if (fileFilter is null || (name is not null && fileFilter(name)))
                        total += length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable directory: count what we can.
            }
        }
        return total;
    }

    /// <summary>Deletes a file or folder (or only what is inside the folder) and reports what could not be removed.</summary>
    public static DeleteResult Delete(string path, bool contentsOnly = false)
    {
        var failed = 0;
        string? firstError = null;

        void Fail(Exception ex)
        {
            failed++;
            firstError ??= ex.Message;
        }

        void DeleteDirectory(string dir, bool removeSelf)
        {
            MakeTraversable(dir);
            string[] children;
            try
            {
                children = Directory.GetFileSystemEntries(dir);
            }
            catch (Exception ex)
            {
                Fail(ex);
                return;
            }

            foreach (var child in children)
            {
                try
                {
                    var attributes = File.GetAttributes(child);
                    var isRealDirectory = (attributes & FileAttributes.Directory) != 0
                                          && (attributes & FileAttributes.ReparsePoint) == 0;
                    if (isRealDirectory)
                        DeleteDirectory(child, removeSelf: true);
                    else
                        File.Delete(child); // also unlinks symlinks without touching their target
                }
                catch (Exception ex)
                {
                    Fail(ex);
                }
            }

            if (!removeSelf)
                return;
            try
            {
                Directory.Delete(dir, recursive: false);
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                File.Delete(path);
            else
                DeleteDirectory(path, removeSelf: !contentsOnly);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex)
        {
            Fail(ex);
        }

        if (failed == 0)
            return DeleteResult.Ok();
        var hint = UserContext.IsRoot ? "" : " — some files may be owned by root; try “Relaunch as root”.";
        return DeleteResult.Fail($"{failed} entries could not be removed ({firstError}){hint}");
    }

    /// <summary>Read-only folders (e.g. Go's module cache) can't have their children removed until they are writable.</summary>
    static void MakeTraversable(string dir)
    {
        try
        {
            const UnixFileMode needed = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            var mode = File.GetUnixFileMode(dir);
            if ((mode & needed) != needed)
                File.SetUnixFileMode(dir, mode | needed);
        }
        catch
        {
            // Not ours to change; the delete will report it.
        }
    }
}
