using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Localization;

public static class LocalizedNames
{
    public static string Category(CleanupCategory category) => category switch
    {
        CleanupCategory.Cache => "Кэши",
        CleanupCategory.Leftover => "Остатки удалённых программ",
        CleanupCategory.DevToolchain => "Тулчейны и SDK",
        CleanupCategory.InstalledApp => "Установленные программы",
        CleanupCategory.SystemFile => "Системные файлы",
        CleanupCategory.Temp => "Временные файлы",
        CleanupCategory.RecycleBin => "Корзина",
        CleanupCategory.UserData => "Пользовательские данные",
        _ => "Прочее"
    };

    public static string Risk(CleanupRisk risk) => risk switch
    {
        CleanupRisk.Low => "Низкий",
        CleanupRisk.Medium => "Средний",
        _ => "Высокий"
    };

    /// <summary>Локализованное действие по умолчанию для таблицы плана (FR-1.11).</summary>
    public static string DefaultAction(CleanupDefaultAction action) => action switch
    {
        CleanupDefaultAction.Clean => "Очистить",
        CleanupDefaultAction.Ask => "Спросить",
        _ => "Не трогать"
    };
}
