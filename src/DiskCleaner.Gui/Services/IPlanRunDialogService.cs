using DiskCleaner.Core.Cleaning;
using DiskCleaner.Core.Models;

namespace DiskCleaner.Gui.Services;

/// <summary>
/// Пользовательские диалоги выполнения полного плана (FR-5.17): пошаговое подтверждение
/// опасных шагов (с последствиями и инструкцией «требует админа», FR-5.3/5.4) и показ
/// итогового отчёта (FR-5.14/5.16). Абстракция позволяет unit-тестам ViewModel подменять
/// MessageBox/ReportWindow.
/// </summary>
public interface IPlanRunDialogService
{
    /// <summary>
    /// Запрос подтверждения опасного шага плана (FR-5.17). <c>true</c> — пользователь
    /// подтверждает выполнение шага; <c>false</c> — шаг будет пропущен с исходом
    /// <see cref="CleanOutcome.NotConfirmed"/>.
    /// </summary>
    Task<bool> ConfirmDangerousStepAsync(CleanupItem item);

    /// <summary>Показать итоговый отчёт о выполнении плана (Markdown/JSON, FR-5.14–5.16).</summary>
    void ShowRunReport(CleanPlanRunReport report);
}
