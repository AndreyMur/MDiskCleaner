using Microsoft.Win32;

namespace DiskCleaner.Gui.Services;

/// <summary>Реализация диалога сохранения на WPF (<see cref="SaveFileDialog"/>, FR-1.12).</summary>
public sealed class WpfSaveFileDialogService : ISaveFileDialogService
{
    public string? Show(string title, string defaultFileName, string defaultExtension, string filter)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            FileName = defaultFileName,
            DefaultExt = defaultExtension,
            Filter = filter,
            AddExtension = true,
            OverwritePrompt = true
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
