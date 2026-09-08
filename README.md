## 1. Анализ диска и обнаружение мусора — `Core/Analysis/`, `Core/Scanning/`

| Функция                                                        | Реализация                                                                    |
| -------------------------------------------------------------- | ----------------------------------------------------------------------------- |
| Сканирование диска с расчётом размеров каталогов               | `DiskScanService`, `DirectoryScanner`, нативный перебор `NativeDirectory`     |
| Кэширование результатов измерений (ускорение повторных сканов) | `CachedDirectoryMeasurer`, `ScanCacheStore`                                   |
| Два источника анализа: выбранный диск или «известные объекты»  | `ScanSeedsProvider`, `SystemScanSeedsProvider`                                |
| Опциональное включение системных каталогов в скан              | настройка `IncludeSystemDirectories`                                          |
| Категоризация найденного по 9 категориям с уровнем риска       | `CategorizationService`, `Models/CleanupCategory.cs`, `Models/CleanupRisk.cs` |
| Действие по умолчанию для элемента (корзина / удаление)        | `Models/CleanupDefaultAction.cs`                                              |
| Построение дерева объектов с чекбоксами                        | `TreeBuilder`, `AnalysisResult`                                               |
| Формирование плана очистки с оценкой освобождаемого места      | `DiskScanPlanBuilder`, `AnalysisCoordinator`, `AnalysisService`               |
| Прогресс и отмена сканирования                                 | `ScanProgress`, поддержка `CancellationToken`                                 |

Категории: `Cache`, `Leftover`, `DevToolchain`, `InstalledApp`, `SystemFile`, `Temp`, `RecycleBin`, `UserData`, `Other`.

## 2. Очистка кэшей пакетных менеджеров — `Core/Caches/`

| Функция                                                                                                      | Реализация                                                                     |
| ------------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------ |
| Встроенный каталог известных кэшей (npm, pnpm, yarn, Gradle, Maven, pip, uv, Cargo, NuGet, Playwright и др.) | `caches.json` (встроенный ресурс), `CacheCatalogService`, `CacheCatalogModels` |
| Парсинг вывода менеджеров пакетов для точного определения путей                                              | `ManagerOutputParser`                                                          |
| Построение плана очистки кэшей                                                                               | `CacheCleanPlanner`, `CacheCleanPlanModels`                                    |
| Исполнение очистки кэшей                                                                                     | `CacheCleanerService`, `CacheCleanModels`                                      |

## 3. Поиск остатков удалённых программ — `Core/Leftovers/`

| Функция                                                                         | Реализация                                                                                           |
| ------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------- |
| Скан кандидатов-остатков: папки-осиротки, `*-updater`, зарезервированные имена  | `LeftoverCandidateScanner`, `UpdaterFolderNames`, `ReservedFolderNames`, корни — `LeftoverScanRoots` |
| Эвристический движок правил (возраст, совпадение с установленными приложениями) | `LeftoverRuleEngine`, `LeftoverCandidate`                                                            |
| Белый список установленных приложений (не помечать их остатками)                | `InstalledWhitelist`                                                                                 |
| Пользовательские исключения (добавить папку в игнор)                            | `ExclusionsStore`                                                                                    |
| Каталог брендов и нормализация имён (группировка вроде `Company/Product`)       | `LeftoverBrandCatalog`, `LeftoverNameNormalizer`                                                     |
| Измерение размеров папок-остатков                                               | `LeftoverDirectoryMeasurer`                                                                          |
| План очистки остатков и его исполнение                                          | `LeftoverPlanService`, `LeftoverPlan`, `LeftoverCleanService`, `LeftoverCleanModels`                 |
| Отдельный elevated-сценарий для системных остатков                              | `LeftoverElevatedScenarioBuilder`                                                                    |

## 4. Деинсталляция приложений — `Core/Uninstall/`

| Функция                                                                              | Реализация                                                                                                     |
| ------------------------------------------------------------------------------------ | -------------------------------------------------------------------------------------------------------------- |
| Список установленных программ из реестра (HKCU + HKLM)                               | `InstalledAppAnalyzer`, `UninstallRegistryService`, `InstalledApp`                                             |
| Разбор строк деинсталляции (MSI, Inno, NSIS и др., QuietUninstallString)             | `UninstallStringParser`                                                                                        |
| Планирование удаления: оценка размера папки, анализ зависимостей                     | `UninstallPlannerService`, `UninstallPlanService`, `UninstallFolderSizeProvider`, `UninstallDependencyCatalog` |
| Пакетное исполнение деинсталляций: HKCU — напрямую, HKLM — один UAC на пачку         | `UninstallExecutionService`, `UninstallElevatedScenarioBuilder`                                                |
| Идемпотентность: коды 1605/1612 = «не установлено», reboot-код 3010                  | `UninstallExitCodes`, `UninstallRebootRequest`                                                                 |
| Fallback для bundle-установщиков через `%ProgramData%\Package Cache`                 | `BundleFallbackResolver`                                                                                       |
| Массовое удаление всех MSI-компонентов версии Windows SDK / JDK (несколько проходов) | `SdkBulkUninstallService`, `WindowsSdkFamily`, `SdkBulkUninstallModels`                                        |
| Поиск осиротевших записей реестра удалённых программ                                 | `UninstallOrphanRegistryScanner`                                                                               |
| Поиск приложений без записи в реестре                                                | `UnrecordedAppScanner`                                                                                         |
| Поиск осиротевших каталогов Windows Kits                                             | `WindowsKitsOrphanScanner`                                                                                     |
| Очистка кэша пакетов деинсталлятора                                                  | `PackageCacheCleaner`                                                                                          |
| Построение путей удаления ключей реестра                                             | `RegistryDeletePathBuilder`                                                                                    |
| Дата первой установки Windows (для эвристик возраста)                                | `WindowsFirstRunDateProvider`                                                                                  |

## 5. Безопасность и системные операции — `Core/Cleaning/`, `Core/Processes/`, `Core/Elevated/`

| Функция                                                                                                                                           | Реализация                                                                                                                                                                |
| ------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Детекция занятых файлов перед удалением (снимки процессов и служб)                                                                                | `InUseDetector`, `ProcessInspector`, `ServiceInspector`, `ProcessSnapshotCache`, `InUseAdvice`, `InUseMessages`                                                           |
| Deny-list: критичные пути, которые никогда не удаляются                                                                                           | `Cleaning/DenyList`                                                                                                                                                       |
| Удаление папок в корзину или мимо корзины                                                                                                         | `DirectoryDeleter`, `RecycleBinService`                                                                                                                                   |
| Очистка Корзины                                                                                                                                   | `RecycleBinService`                                                                                                                                                       |
| Удаление ключей реестра                                                                                                                           | `LocalRegistryCleaner`                                                                                                                                                    |
| Elevated-сценарий: JSON со списком операций → отдельный процесс с UAC → обратный маппинг результатов                                              | `ElevatedScenarioBuilder`, `ElevatedProcessLauncher`, `ElevatedScenarioRunner`, `ElevatedJson`, `ElevatedModels`, `IElevatedRunner`; при отказе UAC — `ElevationDeclined` |
| Журнал/аудит каждой операции очистки                                                                                                              | `CleanActionJournal`                                                                                                                                                      |
| Центральный оркестратор очистки: dry-run, группировка по потокам (файлы / реестр / деинсталляции / elevated), подтверждение рискованных элементов | `PlanExecutor`, `ElevatedScenarioBuilder`                                                                                                                                 |
| Запуск внешних команд (деинсталляторы, schtasks) с поиском по PATH/System32                                                                       | `Commanding/CommandLocator`, `ProcessCommandRunner`, `CommandDefinition`, `CommandResult`                                                                                 |

## 6. Отчёты и сравнение — `Core/Reports/`

| Функция                                                 | Реализация                                                             |
| ------------------------------------------------------- | ---------------------------------------------------------------------- |
| Снапшоты планов очистки (сохранение состояния до/после) | `PlanSnapshotService`, `PlanSnapshotStore`, `PlanDocument`, `PlanJson` |
| Сравнение «было / стало»                                | `PlanComparer`                                                         |
| Экспорт плана и отчёта: Markdown, JSON, CSV             | `PlanExporter`, `ReportJson`, `ReportDocument`, `CleanReportFormatter` |

## 7. Планировщик — `Core/Scheduling/`

| Функция                                                                                                                                        | Реализация                                                                                      |
| ---------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------- |
| Еженедельный автозапуск анализа через Task Scheduler (задача `DiskCleanerAutoScan`, аргумент `--scan-scheduled` — только анализ, без удалений) | `SchedulerService`, `ScheduleModels` (`CleanSchedule`: Enabled, Day, Time)                      |
| Создание / изменение / удаление задачи, чтение текущего состояния                                                                              | `SaveAsync`, `DeleteAsync`, `QueryAsync` (через `schtasks.exe` + XML-триггер `CalendarTrigger`) |

## 8. GUI (WPF, MVVM) — `Gui/`

**Главное окно** (`MainWindow`, `MainViewModel`):

- «Анализ» — скан выбранного источника с прогрессом и отменой (`AnalyzeCommand`)
- «Выполнить план» — очистка отмеченного с диалогом подтверждения рисков и автообновлением анализа (`CleanCommand`)
- «Очистка кэшей» / «Остатки» / «Деинсталляция» / «Расписание» — открыть соответствующие окна
- «Экспорт» — сохранение плана в Markdown / JSON / CSV (`ExportCommand`, `ExportFormatOption`)
- Настройки: dry-run предпросмотр, источник (диск или известные объекты), включение системных каталогов
- Панели: дерево категорий с чекбоксами (`TreeItemViewModel`), «Сведения об объекте», «Было/стало» (`ComparisonRowViewModel`), план (`PlanRowViewModel`)

**Диалоговые окна:**

- **«Кэши»** (`CacheCleanWindow`, `CacheCleanViewModel`): сформировать план, dry-run предпросмотр, выполнить очистку (с подтверждением)
- **«Остатки»**: скан кандидатов, просмотр, исключения, очистка
- **«Деинсталляция»**: список установленных приложений, удаление, массовое удаление SDK, осиротевшие записи реестра
- **«Расписание»**: включение, день недели и время еженедельного автоскана

## 9. CLI — `Cli/` (FR-0.5)

| Аргумент               | Действие                                                                                                    |
| ---------------------- | ----------------------------------------------------------------------------------------------------------- |
| `--scan`               | Анализ без удаления; опционально фильтр `--category` и JSON-отчёт                                           |
| `--clean`              | Очистка по селекторам `--category` / `--key` (точный ключ, повторяемый) / `--name` (подстрока, повторяемая) |
| `--yes`                | Подтверждение очистки (без него очистка не выполняется, exit 2)                                             |
| `--dry-run`            | Предпросмотр без удаления                                                                                   |
| `--help` / `--version` | Справка / версия сборки                                                                                     |

Категории можно указывать и русскими названиями (`Localization/LocalizedNames`). Осмысленные exit-коды (0 — успех, 2 — ошибка использования).

## 10. Инфраструктура

- Логирование Serilog: rolling-файлы `%LocalAppData%\DiskCleaner\logs`, ротация 14 дней — `Logging/DiskCleanerLog`
- Локализация названий категорий — `Localization/LocalizedNames`
- Раскрытие токенов путей (`%LOCALAPPDATA%`, `%PROGRAMDATA%` и др.) — `Environment/EnvironmentProvider`, `Abstractions/IEnvironment`
- Модели очистки (категория, риск, действие по умолчанию) — `Models/`

---

## Сводка по соответствию «модуль docs ↔ код»

| Документ                  | Код                                                                 |
| ------------------------- | ------------------------------------------------------------------- |
| 01 Module Analysis        | `Analysis/`, `Scanning/`, `Models/`                                 |
| 02 Module CacheCleaner    | `Caches/`                                                           |
| 03 Module LeftoverScanner | `Leftovers/`                                                        |
| 04 Module Uninstaller     | `Uninstall/`                                                        |
| 05 Module SystemAndSafety | `Cleaning/`, `Processes/`, `Elevated/`, `Scheduling/`, `Reports/`   |
| Интерфейсы                | `Gui/` (WPF), `Cli/` (консоль), `DiskCleaner.Elevated` (UAC-хелпер) |
