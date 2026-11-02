using System;
using System.IO;

namespace Reacher;

internal static class Log
{
    private static readonly object Gate = new();

    public static string Directory { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reacher");

    public static string Path { get; } = System.IO.Path.Combine(Directory, "reacher.log");

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var info = new FileInfo(Path);
                if (info.Exists && info.Length > 1_000_000) File.Move(Path, Path + ".old", overwrite: true);
                File.AppendAllText(Path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never take the app down
        }
    }
}
