using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DiskCleaner.Core.Cleaning;

/// <summary>Операции с Корзиной через штатную оболочку Windows.</summary>
public interface IRecycleBin
{
    /// <summary>Перемещает путь в Корзину. Бросает исключение при ошибке.</summary>
    void MoveToRecycleBin(string path);

    /// <summary>
    /// Очищает Корзину диска (FR-5.2): <paramref name="driveRoot"/> — корень диска
    /// (например, <c>C:\</c>) или пустая строка для всех дисков. Бросает исключение при ошибке.
    /// </summary>
    void Empty(string driveRoot);
}

/// <summary>Реализация Корзины через штатную оболочку Windows (SHFileOperation, FOF_ALLOWUNDO; SHEmptyRecycleBin).</summary>
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

    public void Empty(string driveRoot)
    {
        var root = driveRoot?.Trim() ?? string.Empty;
        if (root.Length > 0 &&
            !root.EndsWith(Path.DirectorySeparatorChar) &&
            !root.EndsWith(Path.AltDirectorySeparatorChar))
        {
            root += Path.DirectorySeparatorChar;
        }

        var result = SHEmptyRecycleBin(IntPtr.Zero, root.Length == 0 ? null : root, EmptyBinNoConfirmation | EmptyBinNoProgressUi | EmptyBinNoSound);
        if (result != 0)
        {
            var description = DescribeResult(result);
            var target = root.Length == 0 ? "всех дисков" : $"диска '{root}'";
            throw new IOException($"Не удалось очистить Корзину {target}: {description} (код 0x{result:X8}).");
        }
    }

    /// <summary>
    /// SHEmptyRecycleBin возвращает HRESULT; ошибки оболочки (0x80070005 — E_ACCESSDENIED и т.п.)
    /// переводятся в читаемый текст через Win32-код в младших 16 битах.
    /// </summary>
    private static string DescribeResult(int result)
    {
        try
        {
            var win32Code = result & 0x0000FFFF;
            if (win32Code != 0)
            {
                return new Win32Exception(win32Code).Message;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
        }

        return "операция оболочки не выполнена";
    }

    private const uint DeleteOperation = 0x0003;

    private const ushort Silent = 0x0004;
    private const ushort NoConfirmation = 0x0010;
    private const ushort AllowUndo = 0x0040;
    private const ushort NoErrorUi = 0x0400;

    /// <summary>SHERB_NOCONFIRMATION: не показывать диалог подтверждения (FR-5.2 — очистка без UI).</summary>
    private const uint EmptyBinNoConfirmation = 0x00000001;

    /// <summary>SHERB_NOPROGRESSUI: не показывать окно прогресса.</summary>
    private const uint EmptyBinNoProgressUi = 0x00000002;

    /// <summary>SHERB_NOSOUND: без звука.</summary>
    private const uint EmptyBinNoSound = 0x00000004;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int SHFileOperation(ref ShFileOperation operation);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int SHEmptyRecycleBin(IntPtr windowHandle, string rootPath, uint flags);

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
