# Чек-лист администратора: массовая очистка ПК через CLI (M4)

- **Относится к:** PRD 00 (FR-0.5), M4 «CLI, отчёты JSON, интеграция со штатными средствами»
- **Персона:** Системный администратор
- **Цель:** Полный цикл очистки ПК через `DiskCleaner.Cli` без GUI: анализ → dry-run → очистка → JSON-отчёт; повторяемость на группе машин и на эталонной машине.

## 1. Требования к ПК и запуску

| Требование | Значение |
|---|---|
| ОС | Windows 10/11 (x64) |
| .NET | не требуется — CLI публикуется self-contained (см. раздел 6) |
| Права | обычный пользователь; админ-права запрашиваются только для системных шагов/деинсталляции (один UAC-подъём на пачку) |
| Журнал | `%LOCALAPPDATA%\DiskCleaner\logs\*.log` (UTF-8) |
| Отчёт | файл JSON, единая схема `diskcleaner.report` (UTF-8) |

## 2. Типовой цикл очистки одного ПК

Последовательность команд (пути к `DiskCleaner.Cli.exe` и файлам отчётов — на усмотрение администратора):

```bat
rem 1. Анализ: план категорий и объектов (ничего не удаляется).
DiskCleaner.Cli.exe --scan --json "C:\clean-reports\scan-2026-09-06.json"

rem 2. Просмотр того, что будет удалено (dry-run, ничего не удаляется).
DiskCleaner.Cli.exe --clean --category Cache,Temp,RecycleBin,Leftover --dry-run --json "C:\clean-reports\dry-2026-09-06.json"

rem 3. Массовая очистка низкорисковых категорий после проверки dry-run.
DiskCleaner.Cli.exe --clean --category Cache,Temp,RecycleBin --yes --json "C:\clean-reports\clean-2026-09-06.json"

rem 4. Точечная деинсталляция конкретного приложения/SDK (требует явного выбора).
rem    Сначала найдите точное имя/key в scan-отчёте, затем:
DiskCleaner.Cli.exe --clean --category InstalledApp --name "Windows SDK" --yes --json "C:\clean-reports\uninstall-sdk-2026-09-06.json"

rem 5. Системный шаг (гибернация) — только по явному выбору (--key из scan-отчёта).
DiskCleaner.Cli.exe --clean --key system:hibernation:off --yes --json "C:\clean-reports\hibernate-2026-09-06.json"
```

### Пояснения к шагам

- **`--scan`** выполняет анализ ядра (кэши, остатки, приложения, системные объекты) и записывает
  JSON-план. Никаких удалений.
- **`--dry-run`** прогоняет план очистки в режиме предпросмотра: показывает объекты, размеры,
  предупреждения; **ничего не удаляет**.
- **Массовая очистка** допустима только для категорий с низким риском:
  `Cache`, `Temp`, `RecycleBin`, `Leftover`. Для них достаточно `--category`, подтверждение — флаг `--yes`.
- **Деинсталляция и системные шаги** (категории `InstalledApp`, `DevToolchain`, `SystemFile`, `UserData`)
  не удаляются «пачкой»: необходимо явно выбрать объекты через `--name` (подстрока имени/группы)
  или `--key` (точный ключ из scan-отчёта). Это соответствует принципу «программы никогда не удаляются молча».
- Если для массовой категории не передан `--yes`, CLI отказывается выполнять очистку и завершается с кодом 2.

## 3. Чтение JSON-отчёта

Единая схема `diskcleaner.report` одинакова для GUI (экспорт JSON из окна отчёта) и CLI:

```json
{
  "schema": "diskcleaner.report",
  "schemaVersion": 1,
  "mode": "scan | dry-run | clean",
  "summary": {
    "freedBytes": 123456,
    "freedText": "120 КБ",
    "totalItems": 42,
    "deletedItems": 40,
    "failedItems": 0,
    "blockedItems": 2,
    "deferredItems": 0
  },
  "categories": [ { "category": "Cache", "categoryText": "Кэши", "bytes": 100000, "items": 30 } ],
  "items": [ { "key": "...", "name": "...", "path": "...", "outcome": "DirectDeleted", "freedBytes": 5000 } ],
  "removed": [ /* объекты, которые были удалены/очищены */ ],
  "blocked": [ { "key": "...", "name": "...", "reason": "запрещено (deny-список)", "note": "..." } ]
}
```

Пример запроса на PowerShell:

```powershell
$r = Get-Content "C:\clean-reports\clean-2026-09-06.json" -Raw -Encoding UTF8 | ConvertFrom-Json
"Освобождено: {0} байт" -f $r.summary.freedBytes
$r.blocked | Format-Table name, reason -AutoSize
```

Файл пишется в UTF-8 (кириллица в путях и именах корректна).

## 4. Повторяемость и проверка результата

1. **До/после:** сравните `summary.freedBytes` соседних отчётов и свободное место диска
   (`Get-PSDrive C`). Повторный `--scan` после очистки должен показать уменьшение размеров категорий.
2. **Повторный прогон безопасен:** очистка несуществующего/уже удалённого объекта — идемпотентна
   (не считается ошибкой; см. обработку exit-кодов msiexec 1605/1612 и «объект уже отсутствует»).
3. **Dry-run воспроизводим:** `--scan` детерминирован по составу категорий; повторяемость проверяется
   сравнением двух dry-run JSON.
4. **Заблокированные объекты** не прерывают план: они попадают в `blocked` с причиной
   (IN_USE / deny-список / ошибка доступа / отказ UAC). После закрытия приложений запуск можно повторить.

## 5. Коды возврата CLI

| Код | Значение |
|---|---|
| 0 | Успех (в т.ч. dry-run) |
| 1 | Очистка выполнена, но есть объекты с ошибками/частично (`summary.failedItems > 0`) |
| 2 | Ошибка использования: неверный аргумент/категория; очистка без `--yes` |
| 3 | Под фильтры не попало ни одного объекта (или не найдены объекты анализа) |

## 6. Подготовка дистрибутива CLI

Сборка (Windows):

```bat
dotnet publish src\DiskCleaner.Cli\DiskCleaner.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\cli
rem рядом с CLI нужен elevated-исполнитель для системных шагов/деинсталляции:
dotnet publish src\DiskCleaner.Elevated\DiskCleaner.Elevated.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\cli\Elevated
```

На целевом ПК скопируйте папку целиком (`DiskCleaner.Cli.exe` + `Elevated\DiskCleaner.Elevated.exe`).

## 7. Прогон на эталонной машине

1. Убедиться, что машина в том же состоянии (эталонный набор ПО/кэшей или та же виртуальная машина).
2. Выполнить пункты раздела 2.
3. Проверить: свободное место увеличилось; `summary.freedBytes` и `blocked` соответствуют ожиданиям;
   журнал в `%LOCALAPPDATA%\DiskCleaner\logs` содержит записи `Clean action` с полями
   `object`, `sizeBytes`, `op`, `result`, `freedBytes`.
4. Повторить на 2–3 машинах — результаты должны быть сопоставимы, инцидентов удаления
   используемых/нужных файлов нет (критерий беты M3).

## 8. Проверочный список (чек-лист)

- [ ] `--scan` завершается кодом 0 и создаёт JSON-план.
- [ ] `--clean --category Cache,Temp --dry-run` ничего не удаляет (код 0, `mode: dry-run`).
- [ ] `--clean --category Cache --yes` удаляет кэши, отчёт содержит `mode: clean`, `removed` непуст.
- [ ] Без `--yes` CLI отказывается выполнять очистку (код 2).
- [ ] Деинсталляция выполняется только при явном `--name`/`--key`.
- [ ] Заблокированные файлы не прерывают план и попадают в `blocked`.
- [ ] JSON-отчёт читается в UTF-8, кириллица корректна, схема соответствует разделу 3.
- [ ] Журнал присутствует в `%LOCALAPPDATA%\DiskCleaner\logs`.
