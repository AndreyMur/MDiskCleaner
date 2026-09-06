using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DiskCleaner.Core.Cleaning;

/// <summary>Перенос файлов/каталогов в Корзину (операция с возможностью отмены пользователем).</summary>
public interface IRecycleBin
{
    /// <summary>Перемещает путь в Корзину. Бросает исключение при ошибке.</summary>
    void MoveToRecycleBin(string path);
}

/// <summary>Реализация Корзины через штатную оболочку Windows (SHFileOperation, FOF_ALLOWUNDO).</summary>
public sealed class RecycleBinService : IRecycleBin
{
    public void MoveToRecycleBin(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Не задан путь для перемещения в Корзину.", nameof(path));
        }

        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full) && !File.Exists(full))
        {
            throw new FileNotFoundException($"Путь не найден: {full}");
        }

        var operation = new ShFileOperation
        {
            Function = DeleteOperation,
            From = full + "\0",
            Flags = AllowUndo | NoConfirmation | Silent | NoErrorUi
        };

        var result = SHFileOperation(ref operation);
        if (result != 0)
        {
            throw new IOException(
                $"Не удалось переместить '{full}' в Корзину: {new Win32Exception(result).Message} (код {result}).");
        }

        if (operation.AnyOperationsAborted)
        {
            throw new IOException($"Перемещение '{full}' в Корзину не завершено (операция отменена).");
        }
    }

    private const uint DeleteOperation = 0x0003;

    private const ushort Silent = 0x0004;
    private const ushort NoConfirmation = 0x0010;
    private const ushort AllowUndo = 0x0040;
    private const ushort NoErrorUi = 0x0400;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int SHFileOperation(ref ShFileOperation operation);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOperation
    {
        public IntPtr WindowHandle;
        public uint Function;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string From;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? To;

        public ushort Flags;

        [MarshalAs(UnmanagedType.Bool)]
        public bool AnyOperationsAborted;

        public IntPtr NameMappings;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ProgressTitle;
    }
}
