using DiskCleaner.Core.Commanding;

namespace DiskCleaner.Core.Caches;

public sealed class CacheCatalogDocument
{
    public List<CacheTargetDefinition> Targets { get; set; } = new();
}

public sealed class CacheTargetDefinition
{
    public string Id { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string? Group { get; set; }

    public string Category { get; set; } = "Cache";

    public string Risk { get; set; } = "Low";

    /// <summary>
    /// Уровень согласия на очистку (структура под FR-2.10–2.11):
    /// <c>Auto</c> — штатная массовая очистка после отметки пользователя;
    /// <c>Ask</c> — требует явного согласия (Gradle <c>jdks</c>, rustup тулчейн).
    /// По умолчанию <c>Auto</c>.
    /// </summary>
    public string ConsentLevel { get; set; } = "Auto";

    public List<string> EnvPaths { get; set; } = new();

    public bool CommandOnly { get; set; }

    public bool AllowDirectDelete { get; set; } = true;

    /// <summary>
    /// Требуется ли повышенный токен для исполнения штатной команды (раздел 5 PRD 02):
    /// глобальные менеджеры, установленные в Program Files, запускаются с UAC-подъёмом
    /// (<c>Verb = "RunAs"</c>, полная elevated-инфраструктура M2). Кэши пользователя
    /// (<c>%LOCALAPPDATA%</c>, профиль) и <c>rustup self uninstall</c> — без админа.
    /// </summary>
    public bool RequiresAdmin { get; set; }

    public List<string> OwnerProcessNames { get; set; } = new();

    public CommandDefinition? PathQuery { get; set; }

    /// <summary>
    /// Резервный способ определения фактического пути: ключ в INI-конфиге менеджера
    /// (например, <c>cache</c> в <c>~/.npmrc</c>), когда команда недоступна/не вернула путь.
    /// </summary>
    public string? ConfigPathKey { get; set; }

    /// <summary>Пути к INI-конфигам (npmrc и т.п.) для ConfigPathKey, в порядке проверки.</summary>
    public List<string> ConfigFilePaths { get; set; } = new();

    public CommandDefinition? CleanCommand { get; set; }

    public string? Description { get; set; }

    public string? Warning { get; set; }

    /// <summary>
    /// Как восстанавливается кэш после очистки (карточка действия, FR-2.5):
    /// <c>download</c> — содержимое скачивается заново (трафик ≈ объёму кэша);
    /// <c>reinstall</c> — требуется повторная установка тулчейна/инструмента;
    /// <c>recreate</c> — пересоздаётся локально без трафика.
    /// По умолчанию <c>download</c>.
    /// </summary>
    public string RestoreHint { get; set; } = "download";
}
