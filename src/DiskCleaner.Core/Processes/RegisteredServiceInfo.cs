namespace DiskCleaner.Core.Processes;

/// <summary>
/// Зарегистрированная Windows-служба и путь к её исполняемому файлу (ImagePath).
/// Используется проверками безопасности остатков (§5 PRD 03): каталог кандидата,
/// внутри которого лежит исполняемый файл службы, не предлагается к удалению.
/// </summary>
public sealed record RegisteredServiceInfo(string ServiceName, string? ExecutablePath);
