@echo off
setlocal
rem Сборка бета-версии DiskCleaner: два self-contained single-file exe (GUI + Elevated) и CLI.
set ROOT=%~dp0
set PUBLISH=%ROOT%publish

echo [1/4] Публикация elevated-процесса (PublishSingleFile)...
dotnet publish "%ROOT%src\DiskCleaner.Elevated\DiskCleaner.Elevated.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "%PUBLISH%\staging-elevated" || goto :error

echo [2/4] Публикация GUI (PublishSingleFile)...
dotnet publish "%ROOT%src\DiskCleaner.Gui\DiskCleaner.Gui.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "%PUBLISH%" || goto :error

echo [3/4] Копирование elevated-процесса рядом с GUI...
copy /y "%PUBLISH%\staging-elevated\DiskCleaner.Elevated.exe" "%PUBLISH%\DiskCleaner.Elevated.exe" >nul || goto :error
rmdir /s /q "%PUBLISH%\staging-elevated" 2>nul

echo [4/4] Публикация CLI (PublishSingleFile)...
dotnet publish "%ROOT%src\DiskCleaner.Cli\DiskCleaner.Cli.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "%PUBLISH%\cli" || goto :error
rem elevated-процесс рядом с CLI (ищется в {base}\Elevated\DiskCleaner.Elevated.exe):
dotnet publish "%ROOT%src\DiskCleaner.Elevated\DiskCleaner.Elevated.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "%PUBLISH%\cli\Elevated" || goto :error

echo.
echo Готово. Бета-версия: "%PUBLISH%\DiskCleaner.exe"
echo CLI-режим: "%PUBLISH%\cli\DiskCleaner.Cli.exe" --help
echo Для беты у 10 пользователей распространите папку publish целиком.
exit /b 0

:error
echo Сборка завершилась с ошибкой.
exit /b 1
