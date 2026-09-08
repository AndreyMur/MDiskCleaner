using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace DiskCleaner.Core.Scanning;

internal static class NativeDirectory
{
    private const int MaxPathForPrefix = 248;

    private const string ExtendedPrefix = @"\\?\";

    internal readonly record struct Entry(string Name, bool IsDirectory, bool IsReparsePoint, long Size);

    public static List<Entry> Enumerate(string directoryPath)
    {
        var patternPath = ToPatternPath(directoryPath);
        var entries = new List<Entry>();

        var findData = new Win32FindData();
        var handle = FindFirstFile(patternPath, ref findData);
        if (handle == InvalidHandleValue)
        {
            ThrowForLastError(directoryPath);
        }

        try
        {
            do
            {
                if (findData.FileName is "." or "..")
                {
                    continue;
                }

                var isDirectory = (findData.FileAttributes & FileAttributes.Directory) != 0;
                var isReparsePoint = (findData.FileAttributes & ReparsePointFlag) != 0;
                var size = isDirectory ? 0L : CombineSize(findData.SizeHigh, findData.SizeLow);

                entries.Add(new Entry(findData.FileName, isDirectory, isReparsePoint, size));
            }
            while (FindNextFile(handle, ref findData));
        }
        finally
        {
            FindClose(handle);
        }

        return entries;
    }

    public static (bool Exists, bool IsDirectory) Probe(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            var attributes = File.GetAttributes(full);
            return (true, (attributes & FileAttributes.Directory) != 0);
        }
        catch (DirectoryNotFoundException)
        {
            return (false, false);
        }
        catch (FileNotFoundException)
        {
            return (false, false);
        }
        catch (PathTooLongException)
        {
            var attributes = File.GetAttributes(ExtendedPrefix + full);
            return (true, (attributes & FileAttributes.Directory) != 0);
        }
        catch (UnauthorizedAccessException)
        {
            // Объект существует, но недоступен текущему контексту (Win32 error 5 / Access Denied,
            // FR-5.12). Считаем его каталогом, чтобы удаление пошло через DirectoryDeleter и
            // вернуло AccessDenied (перенос в админ-пачку), а не упало на проверке существования.
            return (true, true);
        }
    }

    public static long GetFileSize(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return stream.Length;
    }

    private static string ToPatternPath(string directoryPath)
    {
        var normalized = directoryPath.TrimEnd('\\');
        if (normalized.Length == 2 && normalized[1] == ':')
        {
            normalized += "\\";
        }

        var full = Path.GetFullPath(normalized);
        var query = full.Length >= MaxPathForPrefix ? ExtendedPrefix + full : full;
        return query.EndsWith('\\') ? query + "*" : query + "\\*";
    }

    private static void ThrowForLastError(string path)
    {
        var error = Marshal.GetLastWin32Error();
        var message = $"Не удалось открыть каталог '{path}': {new Win32Exception(error).Message} (Win32 error {error})";
        if (error == 5)
        {
            // Win32 error 5 / Access Denied — различаем «нет прав» (FR-5.12: шаг → «требует админа»)
            // от «файл занят» (sharing violation, Win32 error 32): для первого исполнитель
            // переносит объект в админ-пачку, для второго — пропускает как заблокированный.
            throw new UnauthorizedAccessException(message);
        }

        throw new IOException(message);
    }

    private static long CombineSize(uint high, uint low) =>
        ((long)high << 32) | low;

    private const FileAttributes ReparsePointFlag = (FileAttributes)0x400;

    private static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFile(string lpFileName, ref Win32FindData lpFindFileData);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFile(IntPtr hFindFile, ref Win32FindData lpFindFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr hFindFile);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindData
    {
        public FileAttributes FileAttributes;
        public uint CreationLow;
        public uint CreationHigh;
        public uint LastAccessLow;
        public uint LastAccessHigh;
        public uint LastWriteLow;
        public uint LastWriteHigh;
        public uint SizeHigh;
        public uint SizeLow;
        public uint Reserved0;
        public uint Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string FileName;
    }
}
