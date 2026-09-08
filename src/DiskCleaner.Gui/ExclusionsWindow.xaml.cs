using System.Windows;
using DiskCleaner.Gui.Services;

namespace DiskCleaner.Gui;

/// <summary>
/// Окно управления исключениями остатков (FR-3.7): просмотр, ручное добавление (путь или
/// бренд) и удаление записей <c>exclusions.json</c>. Изменения применяются сразу; окно
/// возвращает <c>true</c>, если записи менялись — владелец перестраивает план остатков.
/// </summary>
public partial class ExclusionsWindow : Window
{
    private readonly ILeftoverExclusionsService _exclusions;
    private bool _changed;

    public ExclusionsWindow(ILeftoverExclusionsService exclusions)
    {
        InitializeComponent();
        _exclusions = exclusions;
        StorePathText.Text = "Хранилище: " + _exclusions.FilePath;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshList();
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var entry = EntryTextBox.Text?.Trim() ?? string.Empty;
        if (entry.Length == 0)
        {
            ErrorText.Text = "Введите путь или имя/бренд каталога.";
            return;
        }

        try
        {
            _exclusions.AddEntry(entry);
            EntryTextBox.Clear();
            ErrorText.Text = string.Empty;
            _changed = true;
            RefreshList();
        }
        catch (Exception ex)
        {
            ErrorText.Text = "Не удалось сохранить исключение: " + ex.Message;
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (EntriesList.SelectedItem is not ExclusionEntryItem entry)
        {
            ErrorText.Text = "Выберите запись для удаления.";
            return;
        }

        try
        {
            _exclusions.Remove(entry.Value);
            ErrorText.Text = string.Empty;
            _changed = true;
            RefreshList();
        }
        catch (Exception ex)
        {
            ErrorText.Text = "Не удалось удалить исключение: " + ex.Message;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = _changed;
        Close();
    }

    private void RefreshList()
    {
        EntriesList.ItemsSource = _exclusions
            .Load()
            .OrderByDescending(_exclusions.IsPathEntry)
            .ThenBy(e => e, StringComparer.OrdinalIgnoreCase)
            .Select(e => new ExclusionEntryItem(_exclusions.IsPathEntry(e) ? "Путь" : "Бренд", e))
            .ToList();
    }

    private sealed record ExclusionEntryItem(string Kind, string Value);
}
