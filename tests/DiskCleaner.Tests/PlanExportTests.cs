using System.Text;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Reports;

namespace DiskCleaner.Tests;

/// <summary>
/// Golden-проверки экспорта плана (FR-1.12): валидность и корректность кириллицы
/// в Markdown/JSON/CSV; файлы вывода — UTF-8 (NFR). Снапшот и дельта — в PlanSnapshotTests.
/// </summary>
public class PlanExportTests
{
    [Fact]
    public void ToMarkdown_ContainsSectionsCategoriesAndCyrillic()
    {
        var doc = GoldenPlan();

        var markdown = PlanExporter.ToMarkdown(doc);

        Assert.Contains("# План очистки — DiskCleaner", markdown);
        Assert.Contains("## Сводка по категориям", markdown);
        Assert.Contains("| Категория | Объектов | Размер |", markdown);
        Assert.Contains("## Кэши", markdown);
        Assert.Contains("Менеджеры пакетов — npm cache", markdown);
        Assert.Contains(CleanReportFormatter.FormatBytes(15_569_282_048L), markdown);
        Assert.Contains("Кэш-группа", markdown);
    }

    [Fact]
    public void ToMarkdown_EscapesPipeInObjectName()
    {
        var doc = new PlanDocument
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Summary = new PlanSummaryJson { TotalItems = 1, TotalBytes = 1, TotalText = "1 Б" },
            Categories = [new PlanCategoryJson { Category = "Cache", CategoryText = "Кэши", Bytes = 1, Items = 1 }],
            Items =
            [
                new PlanItemJson
                {
                    Key = "k1",
                    Name = "A | B",
                    CategoryText = "Кэши",
                    RiskText = "Низкий",
                    DefaultActionText = "Очистить",
                    SizeBytes = 1,
                    FileCount = 1
                }
            ]
        };

        var markdown = PlanExporter.ToMarkdown(doc);

        Assert.Contains("A \\| B", markdown);
    }

    [Fact]
    public void Json_IsValidUtf8_NoEscapedCyrillic_RoundTrips()
    {
        var doc = GoldenPlan();
        var path = Path.Combine(Path.GetTempPath(), "DiskCleaner.Tests", Guid.NewGuid().ToString("N") + ".json");
        try
        {
            PlanExporter.WriteFile(path, doc, PlanExportFormat.Json);

            var text = File.ReadAllText(path, Encoding.UTF8);
            Assert.Contains("npm cache", text);
            Assert.Contains("Кэш-группа", text);
            Assert.DoesNotContain("\\u043", text);

            var loaded = PlanJson.ReadFile(path);
            Assert.Equal(doc.Summary.TotalItems, loaded.Summary.TotalItems);
            Assert.Equal(doc.Items.Count, loaded.Items.Count);
            var npm = loaded.Items.Single(i => i.Name == "npm cache");
            Assert.Equal("Менеджеры пакетов", npm.Group);
            Assert.Equal("Кэши", loaded.Categories.Single().CategoryText);
            Assert.Equal(doc.GeneratedAtUtc, loaded.GeneratedAtUtc);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void Csv_UsesSemicolon_QuotesCyrillicAndRoundTripsRows()
    {
        var doc = GoldenPlan();
        var path = Path.Combine(Path.GetTempPath(), "DiskCleaner.Tests", Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            PlanExporter.WriteFile(path, doc, PlanExportFormat.Csv);

            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "CSV записывается с UTF-8 BOM для корректного открытия в Excel.");

            var rows = ParseCsv(File.ReadAllText(path, Encoding.UTF8));
            var expectedHeader = "Категория;Группа;Объект;Путь;Размер байт;Размер;Файлов;Риск;Действие;Требует админа;IN_USE;Review;Команда";
            Assert.Equal(expectedHeader.Split(';'), rows[0]);

            var dataRows = rows.Skip(1).ToList();
            Assert.Equal(doc.Items.Count, dataRows.Count);
            var npm = dataRows.Single(r => r[2] == "npm cache");
            Assert.Equal("Кэши", npm[0]);
            Assert.Equal("Менеджеры пакетов", npm[1]);
            Assert.Contains("npm-cache", npm[3]);
            Assert.Equal(CleanReportFormatter.FormatBytes(15_569_282_048L), npm[5]);
            Assert.Equal("Очистить", npm[8]);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void Csv_QuotesFieldContainingSeparator()
    {
        var doc = new PlanDocument
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Summary = new PlanSummaryJson { TotalItems = 1, TotalBytes = 2, TotalText = "2 Б" },
            Items =
            [
                new PlanItemJson
                {
                    Key = "k1",
                    Name = "папка; с точкой с запятой",
                    CategoryText = "Прочее",
                    RiskText = "Низкий",
                    DefaultActionText = "Очистить",
                    SizeBytes = 2
                }
            ]
        };

        var csv = PlanExporter.ToCsv(doc);

        var rows = ParseCsv(csv);
        Assert.Equal(2, rows.Count);
        var row = rows[1];
        Assert.Equal("папка; с точкой с запятой", row[2]);
    }

    private static PlanDocument GoldenPlan()
    {
        var items = new[]
        {
            new PlanItemJson
            {
                Key = "k1",
                Name = "npm cache",
                Group = "Менеджеры пакетов",
                Category = "Cache",
                CategoryText = "Кэши",
                Risk = "Low",
                RiskText = "Низкий",
                Target = "Directory",
                Path = @"C:\Users\тест\AppData\Local\npm-cache",
                RequiresAdmin = false,
                InUse = false,
                DefaultAction = "Clean",
                DefaultActionText = "Очистить",
                SizeBytes = 15_569_282_048L,
                FileCount = 123_456,
                Warning = null
            },
            new PlanItemJson
            {
                Key = "k2",
                Name = "VS Code Cache",
                Group = "Кэш-группа",
                Category = "Cache",
                CategoryText = "Кэши",
                Risk = "Low",
                RiskText = "Низкий",
                Target = "Directory",
                Path = @"C:\Users\тест\AppData\Roaming\Code\Cache",
                InUse = true,
                DefaultAction = "Keep",
                DefaultActionText = "Не трогать",
                SizeBytes = 3_000_000_000L,
                FileCount = 5,
                Warning = "Используется"
            }
        };

        return new PlanDocument
        {
            GeneratedAtUtc = new DateTime(2026, 9, 6, 10, 30, 0, DateTimeKind.Utc),
            ElapsedSeconds = 12.5,
            Summary = new PlanSummaryJson
            {
                TotalBytes = items.Sum(i => i.SizeBytes!.Value),
                TotalText = CleanReportFormatter.FormatBytes(items.Sum(i => i.SizeBytes!.Value)),
                TotalItems = items.Length,
                InUseItems = 1,
                RequiresAdminItems = 0,
                ReviewItems = 0
            },
            Categories =
            [
                new PlanCategoryJson
                {
                    Category = "Cache",
                    CategoryText = "Кэши",
                    Bytes = items.Sum(i => i.SizeBytes!.Value),
                    Items = items.Length
                }
            ],
            Items = items
        };
    }

    internal static List<string[]> ParseCsv(string text)
    {
        if (text.StartsWith('\uFEFF'))
        {
            text = text.Substring(1);
        }

        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ';':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row.ToArray());
                    row.Clear();
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row.ToArray());
        }

        return rows;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
