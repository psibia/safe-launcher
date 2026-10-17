using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace SafeLauncher.Service;

/// <summary>Файлы при обновлении: резервная копия папки и возврат папки к ней при откате (см. <see cref="UpdateRunner"/>).</summary>
internal static class UpdateFiles
{
    private const int Attempts = 10;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>Скопировать папку со всеми подпапками, кроме подпапки <paramref name="skip"/> верхнего уровня. Возвращает объём в байтах.</summary>
    public static long CopyTree(string from, string to, string? skip = null)
    {
        long total = 0;
        Directory.CreateDirectory(to);
        foreach (var file in Files(from, skip))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Retry(() => File.Copy(file, target, overwrite: true));
            total += new FileInfo(target).Length;
        }

        return total;
    }

    /// <summary>
    /// Сделать папку <paramref name="to"/> такой же, как <paramref name="from"/>: файлы, которых нет в <paramref name="from"/>, удалить,
    /// остальные заменить. Подпапку <paramref name="keep"/> верхнего уровня не трогать.
    /// </summary>
    public static void Mirror(string from, string to, string? keep = null)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Files(to, keep))
        {
            if (!File.Exists(Path.Combine(from, Path.GetRelativePath(to, file))))
                Retry(() => DeleteFile(file));
        }

        // Пустые лишние папки: сначала самые глубокие.
        foreach (var dir in Directories(to, keep).OrderByDescending(d => d.Length))
        {
            if (!Directory.Exists(Path.Combine(from, Path.GetRelativePath(to, dir))) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Retry(() => Directory.Delete(dir));
        }

        CopyTree(from, to);
    }

    /// <summary>Объём файлов папки в байтах, без подпапки <paramref name="skip"/> верхнего уровня; папки нет — 0.</summary>
    public static long Size(string dir, string? skip = null) =>
        Directory.Exists(dir) ? Files(dir, skip).Sum(f => new FileInfo(f).Length) : 0;

    /// <summary>Удалить папку со всем содержимым, если она есть.</summary>
    public static void DeleteDirectory(string dir)
    {
        if (!Directory.Exists(dir))
            return;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Retry(() => Directory.Delete(dir, recursive: true));
    }

    /// <summary>Действие с файлом, который может быть ещё занят (процесс только что закрылся, антивирус проверяет): до 10 попыток.</summary>
    public static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < Attempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(RetryDelay);
            }
        }
    }

    private static void DeleteFile(string file)
    {
        if (!File.Exists(file))
            return;
        File.SetAttributes(file, FileAttributes.Normal);
        File.Delete(file);
    }

    private static System.Collections.Generic.IEnumerable<string> Files(string root, string? skip) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(f => !Inside(root, f, skip)).ToList();

    private static System.Collections.Generic.IEnumerable<string> Directories(string root, string? skip) =>
        Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Where(d => !Inside(root, d, skip)).ToList();

    /// <summary>Путь лежит в подпапке <paramref name="skip"/> верхнего уровня (или это она сама).</summary>
    private static bool Inside(string root, string path, string? skip)
    {
        if (skip is null)
            return false;
        var relative = Path.GetRelativePath(root, path);
        var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return string.Equals(first, skip, StringComparison.OrdinalIgnoreCase) && (relative.Length > first.Length || Directory.Exists(path));
    }
}
