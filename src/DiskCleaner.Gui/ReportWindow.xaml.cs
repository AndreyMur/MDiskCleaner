using System.IO;
using System.Text;
using System.Windows;
using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Reports;
using Microsoft.Win32;

namespace DiskCleaner.Gui;

public partial class ReportWindow : Window
{
    private string _markdown = string.Empty;
    private string _json = string.Empty;

    public ReportWindow()
    {
        InitializeComponent();
    }

    public static void Show(Window? owner, CleanReport report)
    {
        var window = new ReportWindow
        {
            Owner = owner,
            _markdown = CleanReportFormatter.ToMarkdown(report),
            _json = CleanReportFormatter.ToJson(report)
        };

        window.ReportText.Text = window._markdown;
        window.ShowDialog();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить отчёт",
            Filter = "Markdown (*.md)|*.md|JSON (*.json)|*.json|Текстовый файл (*.txt)|*.txt",
            DefaultExt = ".md",
            FileName = "diskcleaner-report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var content = (dialog.FilterIndex switch
        {
            2 => _json,
            3 => _markdown,
            _ => _markdown
        }) + Environment.NewLine;

        File.WriteAllText(dialog.FileName, content, new UTF8Encoding(false));
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
