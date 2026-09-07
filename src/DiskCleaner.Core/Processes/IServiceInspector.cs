namespace DiskCleaner.Core.Processes;

/// <summary>
/// Источник списка зарегистрированных Windows-служб с путями их исполняемых файлов.
/// Отделён интерфейсом для фикстур в тестах (проверка «каталог кандидата используется
/// службой» выполняется без обращения к реальной системе, §5 PRD 03).
/// </summary>
public interface IServiceInspector
{
    IReadOnlyList<RegisteredServiceInfo> GetServices();
}
