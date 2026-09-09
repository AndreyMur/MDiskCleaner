@echo off
setlocal
rem Создаёт ярлыки DiskCleaner в меню «Пуск» и на рабочем столе для portable-сборки.
rem
rem Использование:
rem   create-shortcuts.cmd                            - ярлыки для .\publish\DiskCleaner.exe (после publish-beta.cmd)
rem   create-shortcuts.cmd "D:\path\DiskCleaner.exe"   - указать exe вручную
rem   create-shortcuts.cmd "exe" startmenu^|desktop^|both - куда класть ярлыки (по умолчанию both)
rem
set ROOT=%~dp0
set EXE=%~1
if "%EXE%"=="" set "EXE=%ROOT%publish\DiskCleaner.exe"
set TARGET=%~2
if "%TARGET%"=="" set TARGET=both

if not exist "%EXE%" (
    echo Ошибка: не найден "%EXE%".
    echo Сначала соберите бета-версию: publish-beta.cmd
    exit /b 1
)

echo exe: %EXE%
echo ярлыки: %TARGET%

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ws = New-Object -ComObject WScript.Shell;" ^
  "function Add-Lnk([string]$dir) {" ^
  "  $lnk = Join-Path $dir 'DiskCleaner.lnk';" ^
  "  $s = $ws.CreateShortcut($lnk);" ^
  "  $s.TargetPath = '%EXE%';" ^
  "  $s.WorkingDirectory = Split-Path '%EXE%' -Parent;" ^
  "  $s.IconLocation = '%EXE%,0';" ^
  "  $s.Description = 'DiskCleaner - disk cleaner GUI';" ^
  "  $s.Save();" ^
  "  Write-Host ('created: ' + $lnk);" ^
  "}" ^
  "$p = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs';" ^
  "$desk = [Environment]::GetFolderPath('Desktop');" ^
  "if ('%TARGET%' -in 'both','startmenu') { Add-Lnk $p };" ^
  "if ('%TARGET%' -in 'both','desktop')  { Add-Lnk $desk }"

echo Готово.
exit /b 0
