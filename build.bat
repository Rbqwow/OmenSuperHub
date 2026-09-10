@echo off
setlocal EnableExtensions

rem Build OmenSuperHub as Release x64.
rem Run "build.bat nopause" to keep the console from waiting at the end.

cd /d "%~dp0"
set "MSBUILD_EXE="
set "NUGET_EXE="

echo [1/3] Locating MSBuild...
where msbuild.exe >nul 2>&1
if not errorlevel 1 (
    for /f "delims=" %%I in ('where msbuild.exe') do if not defined MSBUILD_EXE set "MSBUILD_EXE=%%I"
)

if not defined MSBUILD_EXE if exist "%ProgramFiles%\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD_EXE=%ProgramFiles%\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
if not defined MSBUILD_EXE if exist "%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD_EXE=%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
if not defined MSBUILD_EXE if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD_EXE=%ProgramFiles(x86)%\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
if not defined MSBUILD_EXE if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD_EXE=%ProgramFiles(x86)%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"

if not defined MSBUILD_EXE (
    echo ERROR: MSBuild.exe was not found.
    echo Install Visual Studio with the ".NET desktop development" workload,
    echo or run this script from a Developer Command Prompt.
    set "EXIT_CODE=1"
    goto :finish
)

echo Using: "%MSBUILD_EXE%"
echo [2/3] Restoring NuGet packages...

if exist "%~dp0nuget.exe" set "NUGET_EXE=%~dp0nuget.exe"
if not defined NUGET_EXE (
    where nuget.exe >nul 2>&1
    if not errorlevel 1 for /f "delims=" %%I in ('where nuget.exe') do if not defined NUGET_EXE set "NUGET_EXE=%%I"
)

if defined NUGET_EXE (
    "%NUGET_EXE%" restore "%~dp0OmenSuperHub.sln"
    if errorlevel 1 (
        echo ERROR: NuGet restore failed.
        set "EXIT_CODE=1"
        goto :finish
    )
) else if exist "%~dp0packages\Fody.6.9.3\build\Fody.targets" (
    echo NuGet.exe was not found; existing packages will be used.
) else (
    echo ERROR: NuGet.exe was not found and the packages directory is incomplete.
    echo Install NuGet CLI or put nuget.exe beside build.bat, then run again.
    set "EXIT_CODE=1"
    goto :finish
)

echo [3/3] Building Release x64...
"%MSBUILD_EXE%" "%~dp0OmenSuperHub.csproj" /t:Rebuild /p:Configuration=Release /p:Platform=x64 /p:TargetFramework=net472 /p:TargetFrameworks=net472 /m:1 /nr:false /verbosity:minimal /nologo
if errorlevel 1 (
    echo ERROR: Build failed.
    set "EXIT_CODE=1"
    goto :finish
)

if not exist "%~dp0bin\x64\Release\OmenSuperHub.exe" (
    echo ERROR: Build completed but OmenSuperHub.exe was not found.
    set "EXIT_CODE=1"
    goto :finish
)

echo.
echo Build succeeded:
echo   "%~dp0bin\x64\Release\OmenSuperHub.exe"
set "EXIT_CODE=0"

:finish
if not defined EXIT_CODE set "EXIT_CODE=1"
if /i not "%~1"=="nopause" pause
exit /b %EXIT_CODE%
