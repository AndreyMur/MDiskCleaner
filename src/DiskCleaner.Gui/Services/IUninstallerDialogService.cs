using DiskCleaner.Core.Caches;
using DiskCleaner.Core.Uninstall;

namespace DiskCleaner.Gui.Services;

/// <summary>Решение пользователя на запрос перезагрузки (FR-4.12): перезагрузить сейчас или позже.</summary>
public enum UninstallerRebootChoice
{
    /// <summary>Перезагрузить компьютер не нужно / пользователь отклонил запрос.</summary>
    None,

    /// <summary>Пользователь согласился перезагрузить компьютер сейчас.</summary>
    Now,

    /// <summary>Пользователь отложил перезагрузку (перезагрузит вручную позже).</summary>
    Later
}

/// <summary>
/// Пользовательские диалоги экрана «Деинсталляция и зачистка»: пообъектное подтверждение шага
/// с объёмом и названием (FR-4.11), запрос перезагрузки (FR-4.12), подтверждение осиротевших
/// записей (FR-4.13) и показ отчётов. Абстракция позволяет unit-тестам ViewModel подменять
/// MessageBox/окна.
/// </summary>
public interface IUninstallerDialogService
{
    /// <summary>Запрос подтверждения; <c>true</c> — пользователь согласен.</summary>
    bool Confirm(string title, string message);

    /// <summary>
    /// Пообъектное подтверждение шага деинсталляции (FR-4.11): название, объём (общий и «на C:»),
    /// путь и пометка-рекомендация. <c>true</c> — пользователь подтверждает удаление.
    /// </summary>
    bool ConfirmStep(UninstallPlanItem item);

    /// <summary>
    /// Запрос перезагрузки после операции (FR-4.12). Перезагрузка не выполняется принудительно:
    /// GUI только запрашивает согласие; решение возвращается вызывающему.
    /// </summary>
    UninstallerRebootChoice AskReboot(UninstallRebootRequest request);

    /// <summary>Показать отчёт о выполнении деинсталляции (журнал по шагам, exit-коды).</summary>
    void ShowReport(UninstallExecutionReport report);

    /// <summary>Показать отчёт массового удаления версии Windows SDK (по компонентам/проходам).</summary>
    void ShowSdkReport(SdkBulkUninstallReport report);

    /// <summary>Показать отчёт зачистки (осиротевшие записи реестра, FR-4.13).</summary>
    void ShowCleanReport(CleanReport report);
}
