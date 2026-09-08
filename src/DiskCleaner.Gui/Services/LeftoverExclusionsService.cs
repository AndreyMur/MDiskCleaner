using DiskCleaner.Core.Leftovers;

namespace DiskCleaner.Gui.Services;

/// <summary>Адаптер поверх <see cref="ExclusionsStore"/> для экрана «Остатки».</summary>
public sealed class LeftoverExclusionsService : ILeftoverExclusionsService
{
    private readonly ExclusionsStore _store;

    public LeftoverExclusionsService(ExclusionsStore? store = null)
    {
        _store = store ?? new ExclusionsStore();
    }

    public string FilePath => _store.FilePath;

    public IReadOnlyList<string> Load() => _store.Load();

    public void AddPath(string path) => _store.AddPath(path);

    public void AddBrand(string brandName) => _store.AddBrand(brandName);

    public void AddEntry(string entry) => _store.Add(entry);

    public void Remove(string entry) => _store.Remove(entry);

    public bool IsPathEntry(string entry) => ExclusionsStore.IsPathEntry(entry);
}
