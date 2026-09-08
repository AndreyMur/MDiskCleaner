namespace DiskCleaner.Core.Uninstall;

/// <summary>
/// Один шаг удаления (журнал, NFR PRD 04): запись реестра, построенная команда и исход по
/// exit-коду. Фундамент фазы 21 — минимальный путь «запись → команда → тихое удаление
/// (через повышенный процесс для LM) → контроль exit-кода → журнал».
/// </summary>
public sealed record UninstallExecutionItem(InstalledApp App, UninstallCommand? Command)
{
    /// <summary>Стабильный ключ для сопоставления шагов elevated-сценария и журнала.</summary>
    public string Key => $"{App.ScopeKey}:{App.ProductCode}";

    /// <summary>Машина (LM) требует прав администратора (FR-4.15); HKCU/пользовательские — нет (FR-4.16).</summary>
    public bool RequiresAdmin => App.RequiresAdmin;

    public bool IsMsiexec => Command?.Kind == UninstallerKind.Msi;

    public string DisplayName => App.DisplayName;
}

public enum UninstallExecutionOutcome
{
    /// <summary>Деинсталлятор завершился успешно (exit 0).</summary>
    Uninstalled,

    /// <summary>Деинсталляция прошла, требуется перезагрузка (MSI-код 3010).</summary>
    RebootRequired,

    /// <summary>Продукт уже не установлен (MSI-коды 1605/1612) — не ошибка, идемпотентно.</summary>
    NotInstalled,

    /// <summary>Деинсталлятор уже отсутствует на диске (повторный запуск плана) — успех без действий.</summary>
    AlreadyUninstalled,

    /// <summary>По записи невозможно построить команду (нет UninstallString/QuietUninstallString).</summary>
    NoUninstaller,

    /// <summary>Повышение прав (UAC) отклонено пользователем — пачка не выполнялась.</summary>
    ElevationDeclined,

    /// <summary>Команда превысила допустимое время ожидания.</summary>
    TimedOut,

    /// <summary>Деинсталлятор завершился с ошибкой (прочий exit-код) либо не запустился.</summary>
    Failed
}

/// <summary>
/// Запись журнала исполнения одного удаления (NFR PRD 04): объект, исход, exit-код,
/// пояснение и время. <see cref="ExitCode"/> отсутствует для исходов без запуска процесса
/// (таймаут, отказ UAC, нет деинсталлятора).
/// </summary>
public sealed record UninstallExecutionEntry(
    UninstallExecutionItem Item,
    UninstallExecutionOutcome Outcome,
    int? ExitCode,
    string? Note,
    DateTime StartedAt)
{
    public string DisplayName => Item.DisplayName;

    /// <summary>Успех/идемпотентность: 0, 3010, 1605/1612, отсутствие деинсталлятора (NFR PRD 04).</summary>
    public bool IsOk =>
        Outcome is UninstallExecutionOutcome.Uninstalled
            or UninstallExecutionOutcome.RebootRequired
            or UninstallExecutionOutcome.NotInstalled
            or UninstallExecutionOutcome.AlreadyUninstalled;
}

/// <summary>
/// Итог исполнения пачки удалений: журнал пообъектных записей с exit-кодами (NFR PRD 04).
/// Повторный запуск на уже удалённых объектах не считается ошибкой: коды 1605/1612 и
/// отсутствие деинсталлятора дают успешные исходы (идемпотентность).
/// </summary>
public sealed class UninstallExecutionReport
{
    public IReadOnlyList<UninstallExecutionEntry> Entries { get; init; } = Array.Empty<UninstallExecutionEntry>();

    public DateTime StartedAt { get; init; }

    public DateTime FinishedAt { get; init; }

    public int UninstalledCount => Entries.Count(e =>
        e.Outcome is UninstallExecutionOutcome.Uninstalled or UninstallExecutionOutcome.RebootRequired);

    /// <summary>Сколько записей завершились с признаком «требуется перезагрузка» (MSI-код 3010).</summary>
    public int RebootRequiredCount => Entries.Count(e => e.Outcome == UninstallExecutionOutcome.RebootRequired);

    /// <summary>
    /// Нужно ли предложить перезагрузку после операции (FR-4.12): хотя бы одна запись вернула
    /// exit-код 3010. Перезагрузка не выполняется принудительно — только запрос пользователю.
    /// </summary>
    public bool NeedsReboot => RebootRequiredCount > 0;

    /// <summary>Сколько записей завершилось как «не ошибка» (включая идемпотентные исходы 1605/1612/нет файла).</summary>
    public int OkCount => Entries.Count(e => e.IsOk);

    /// <summary>Сколько записей — ошибки исполнения (неуспешный код, таймаут, нет команды).</summary>
    public int ErrorCount => Entries.Count(e =>
        e.Outcome is UninstallExecutionOutcome.Failed
            or UninstallExecutionOutcome.TimedOut
            or UninstallExecutionOutcome.NoUninstaller);

    /// <summary>Сколько записей пропущено из-за отказа повышения прав (UAC).</summary>
    public int DeclinedCount => Entries.Count(e => e.Outcome == UninstallExecutionOutcome.ElevationDeclined);
}

public sealed class UninstallExecutionOptions
{
    /// <summary>Максимальное время ожидания одного деинсталлятора (NFR: «не зависать»).</summary>
    public int CommandTimeoutSec { get; set; } = 600;
}
