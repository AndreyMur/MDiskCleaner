namespace DiskCleaner.Core.Leftovers;

/// <summary>
/// Маска «остатков апдейтеров» (FR-3.2): каталоги <c>*-updater</c> и варианты
/// с суффиксами/окончаниями <c>updater</c>/<c>update</c> и разделителями <c>-</c>/<c>_</c>.
/// Эти папки остаются после установки приложения из инсталлятора и не привязаны к
/// записи Uninstall установленного ПО.
/// </summary>
public static class UpdaterFolderNames
{
    public static bool IsUpdaterFolder(string folderName)
    {
        var name = folderName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        return name.EndsWith("updater", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("update", StringComparison.OrdinalIgnoreCase);
    }
}
