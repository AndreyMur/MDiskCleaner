@echo off
setlocal
rem Сборка установочного MSI DiskCleaner (WiX v4): публикация single-file exe (GUI + Elevated)
rem и упаковка в MSI через setup\DiskCleaner.Installer.wixproj.
rem Использование: build-msi.cmd [ProductVersion]   (по умолчанию 0.1.0)
set ROOT=%~dp0
set VERSION=%~1
if "%VERSION%"=="" set VERSION=0.1.0
set APP=%ROOT%publish\installer\app
set OUT=%ROOT%publish\installer

echo [1/5] Публикация Elevated (PublishSingleFile, self-contained)...
if exist "%APP%" rmdir /s /q "%APP%"
if exist "%ROOT%publish\installer\elevated-staging" rmdir /s /q "%ROOT%publish\installer\elevated-staging"
dotnet publish "%ROOT%src\DiskCleaner.Elevated\DiskCleaner.Elevated.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "%ROOT%publish\installer\elevated-staging" || goto :error

echo [2/5] Публикация GUI (PublishSingleFile, self-contained)...
dotnet publish "%ROOT%src\DiskCleaner.Gui\DiskCleaner.Gui.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "%APP%" || goto :error

echo [3/5] Подмена Elevated self-contained single-file рядом с GUI...
copy /y "%ROOT%publish\installer\elevated-staging\DiskCleaner.Elevated.exe" "%APP%\DiskCleaner.Elevated.exe" >nul || goto :error
del /q "%APP%\DiskCleaner.Elevated.runtimeconfig.json" "%APP%\*.pdb" 2>nul
rmdir /s /q "%ROOT%publish\installer\elevated-staging" 2>nul

echo [4/5] Сборка MSI (WiX v4)...
dotnet build "%ROOT%setup\DiskCleaner.Installer.wixproj" -c Release -p:Platform=x64 -p:ProductVersion=%VERSION% -p:PublishDir=%APP% -p:OutputPath=..\publish\installer || goto :error

echo [5/5] Готово.
echo Установщик: "%OUT%\DiskCleaner-%VERSION%.msi"
exit /b 0

:error
echo Сборка MSI завершилась с ошибкой.
exit /b 1
