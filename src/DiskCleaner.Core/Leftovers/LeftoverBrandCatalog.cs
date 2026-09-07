using DiskCleaner.Core.Models;

namespace DiskCleaner.Core.Leftovers;

/// <summary>
/// Конфиг-каталог продукта в одном из корней пользователя: либо каталог верхнего уровня
/// корня (<paramref name="ContainerRelativePath"/> == null, например <c>\.android</c> в
/// %USERPROFILE%), либо вложенные каталоги внутри контейнера бренда
/// (<paramref name="ContainerRelativePath"/>, например <c>Google</c> под %LOCALAPPDATA%).
/// Сопоставление имени идёт по точному равенству или по префиксу версии
/// («AndroidStudio2025.3.2» покрывается префиксом «AndroidStudio»).
/// </summary>
/// <param name="RootKind">Корень, в котором лежит конфиг-каталог.</param>
/// <param name="ContainerRelativePath">Путь контейнера бренда относительно корня или null для каталога верхнего уровня.</param>
/// <param name="FolderName">Имя каталога (точное либо префикс с версией).</param>
/// <param name="FolderNameIsPrefix">True — имя сравнивается как префикс (версионный хвост разрешён).</param>
/// <param name="Risk">Риск удаления конфига (высокий для .android — AVD и ключи подписи).</param>
public sealed record RemovedAppConfigFolder(
    LeftoverRootKind RootKind,
    string? ContainerRelativePath,
    string FolderName,
    bool FolderNameIsPrefix,
    CleanupRisk Risk)
{
    public bool MatchesName(string name) =>
        FolderNameIsPrefix
            ? name.StartsWith(FolderName, StringComparison.OrdinalIgnoreCase)
            : string.Equals(name, FolderName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Удалённое приложение, после которого в AppData остаются конфиг-каталоги (FR-3.3).
/// Каталоги предлагаются к удалению только когда продукт отсутствует в реестре Uninstall
/// (<see cref="InstalledWhitelist.ContainsName"/>) и из каталога не запущено процессов.
/// </summary>
public sealed record RemovedAppProductConfig(
    string InstalledProductName,
    string ProductDisplayName,
    IReadOnlyList<RemovedAppConfigFolder> ConfigFolders);

/// <summary>
/// Справочник брендов для движка эвристик остатков (фаза 17): (1) конфиг-папки продуктов
/// в AppData для группы «Конфиги удалённых программ»; (2) «мусорные» бренды, остающиеся в
/// ProgramData/Program Files после деинсталляции; (3) portable/вручную распакованные SDK,
/// которые не регистрируются в Uninstall и потому являются источником ложных срабатываний
/// (их каталоги никогда не предлагаются как остаток).
/// </summary>
public sealed class LeftoverBrandCatalog
{
    public static LeftoverBrandCatalog Default { get; } = new();

    /// <summary>Продукты с известными конфиг-каталогами в AppData (FR-3.3).</summary>
    public IReadOnlyList<RemovedAppProductConfig> RemovedAppProducts { get; } = new[]
    {
        new RemovedAppProductConfig(
            InstalledProductName: "Android Studio",
            ProductDisplayName: "Android Studio",
            ConfigFolders: new[]
            {
                new RemovedAppConfigFolder(
                    LeftoverRootKind.LocalApplicationData, "Google", "AndroidStudio", FolderNameIsPrefix: true, CleanupRisk.Medium),
                new RemovedAppConfigFolder(
                    LeftoverRootKind.ApplicationData, "Google", "AndroidStudio", FolderNameIsPrefix: true, CleanupRisk.Medium),
                new RemovedAppConfigFolder(
                    LeftoverRootKind.UserProfile, ContainerRelativePath: null, ".android", FolderNameIsPrefix: false, CleanupRisk.High)
            })
    };

    /// <summary>
    /// Имена верхнего уровня в ProgramData/Program Files, оставшиеся от удалённых программ
    /// (PRD 03: Windows4DDiGFileRepair, Wondershare, PDFConverter и т.п.). Служат основанием
    /// «известный бренд» для группы «Осиротевшие папки» (FR-3.4).
    /// </summary>
    public IReadOnlySet<string> JunkBrandFolderNames { get; } = new HashSet<string>(
        new[]
        {
            "4ddig", "4ddig file repair", "windows4ddig", "windows4ddigfilerepair", "wondershare",
            "wondershareupdate", "pdfconverter", "360", "viewplaycap", "activation-renewal", "pachca"
        },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Каталоги portable-программ и вручную распакованных SDK/рантаймов (без записи Uninstall).
    /// Их наличие не является признаком остатка — каталог исключается из кандидатов
    /// (источник ложных срабатываний, §5 PRD 03).
    /// </summary>
    public IReadOnlySet<string> PortableOrSdkFolderNames { get; } = new HashSet<string>(
        new[]
        {
            "portableapps", "scoop", "chocolatey", "wsl", "cygwin", "msys64", "cmder", "git",
            "go", "nodejs", "python", "flutter", "rust", "php", "ruby", "java",
            "android", "android-sdk", "platform-tools", "cmdline-tools", "ndk", "vcpkg", "emscripten"
        },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Префиксы SDK, у которых каталог обычно версионирован (<c>jdk-21</c>, <c>openjdk-17</c>):
    /// имя исключается, когда после префикса следует цифра/разделитель версии.
    /// </summary>
    public IReadOnlyList<string> VersionedSdkPrefixes { get; } = new[]
    {
        "jdk", "jre", "openjdk", "corretto", "zulu", "temurin"
    };

    public bool IsJunkBrandFolderName(string folderName) =>
        !string.IsNullOrWhiteSpace(folderName) &&
        JunkBrandFolderNames.Contains(folderName.Trim());

    public bool IsPortableOrSdkFolderName(string folderName)
    {
        var name = folderName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (PortableOrSdkFolderNames.Contains(name))
        {
            return true;
        }

        foreach (var prefix in VersionedSdkPrefixes)
        {
            if (name.Length > prefix.Length &&
                name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                (char.IsDigit(name[prefix.Length]) ||
                 name[prefix.Length] == '-' ||
                 name[prefix.Length] == '_'))
            {
                return true;
            }
        }

        return false;
    }
}
