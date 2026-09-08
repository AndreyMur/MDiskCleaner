using DiskCleaner.Core.Leftovers;

namespace DiskCleaner.Gui.Services;

/// <summary>
/// Исключения пользователя «Остатки» (FR-3.7): хранилище <c>exclusions.json</c>
/// (%LOCALAPPDATA%\DiskCleaner) с записями «путь» или «бренд/имя каталога».
/// Кандидаты, попавшие под исключение, не появляются в плане. Абстракция позволяет
/// unit-тестам ViewModel работать с изолированным списком записей.
/// </summary>
public interface ILeftoverExclusionsService
{
    /// <summary>Полный путь к файлу хранилища (<c>exclusions.json</c>).</summary>
    string FilePath { get; }

    IReadOnlyList<string> Load();

    /// <summary>Добавляет исключение по полному пути каталога (FR-3.7).</summary>
    void AddPath(string path);

    /// <summary>Добавляет исключение по бренду/имени каталога верхнего уровня (FR-3.7).</summary>
    void AddBrand(string brandName);

    /// <summary>Добавляет запись как есть (для ручного ввода пути или бренда).</summary>
    void AddEntry(string entry);

    void Remove(string entry);

    /// <summary>Похожа ли запись на путь (а не на имя каталога/бренд) — для пояснения в списке.</summary>
    bool IsPathEntry(string entry);
}
