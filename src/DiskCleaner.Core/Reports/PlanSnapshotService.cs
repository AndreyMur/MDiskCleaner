using DiskCleaner.Core.Analysis;

namespace DiskCleaner.Core.Reports;

/// <summary>
/// Сервис снапшотов скана (FR-1.13): после каждого анализа сохраняет план (категории/объекты
/// с размерами) единой JSON-схемой в scancache, а при наличии предыдущего снапшота возвращает
/// сравнение «было/стало» (<see cref="PlanComparer"/>).
/// </summary>
public sealed class PlanSnapshotService
{
    private readonly PlanSnapshotStore _store;

    public PlanSnapshotService(PlanSnapshotStore? store = null)
    {
        _store = store ?? new PlanSnapshotStore();
    }

    /// <summary>
    /// Зафиксировать результат скана как текущий снапшот и построить сравнение с предыдущим.
    /// <c>null</c>, если предыдущего снапшота ещё нет (первый скан).
    /// </summary>
    public PlanComparison? Capture(AnalysisResult result)
    {
        var current = PlanDocumentBuilder.FromScan(result);
        return Capture(current);
    }

    /// <summary>Зафиксировать снапшот и построить сравнение с предыдущим.</summary>
    public PlanComparison? Capture(PlanDocument current)
    {
        var previous = _store.TryLoad();
        PlanComparison? comparison = null;
        if (previous is not null)
        {
            comparison = PlanComparer.Compare(previous, current);
        }

        _store.Save(current);
        return comparison;
    }

    /// <summary>Загрузить снапшот предыдущего скана без изменения текущего.</summary>
    public PlanDocument? TryLoadPrevious() => _store.TryLoad();
}
