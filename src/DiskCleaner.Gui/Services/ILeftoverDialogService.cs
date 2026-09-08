using DiskCleaner.Core.Leftovers;

namespace DiskCleaner.Gui.Services;

/// <summary>Что пользователь выбрал при добавлении остатка в исключения (FR-3.7).</summary>
public enum LeftoverExclusionChoice
{
    /// <summary>Ничего не добавлять (диалог закрыт без выбора).</summary>
    None,

    /// <summary>Добавить по полному пути каталога.</summary>
    Path,

    /// <summary>Добавить по бренду (имени каталога верхнего уровня).</summary>
    Brand
}

/// <summary>
/// Пользовательские диалоги экрана «Остатки»: подтверждение пообъектное (FR-3.4, §5) и перед
/// выполнением (с отображением админ-шагов и UAC), выбор способа добавления в исключения (FR-3.7)
/// и показ отчёта (dry-run-предпросмотр / результат). Абстракция позволяет unit-тестам ViewModel
/// подменять MessageBox/окна.
/// </summary>
public interface ILeftoverDialogService
{
    /// <summary>Запрос подтверждения; <c>true</c> — пользователь согласен.</summary>
    bool Confirm(string title, string message);

    /// <summary>Способ добавления кандидата в исключения (путь / бренд / отмена).</summary>
    LeftoverExclusionChoice AskAddExclusion(string path, string brand);

    /// <summary>Показать отчёт (dry-run-предпросмотр или результат выполнения удаления).</summary>
    void ShowReport(LeftoverCleanReport report);
}
