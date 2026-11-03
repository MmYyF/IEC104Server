@echo off
rem ============================================================
rem  IEC104-Server - build / publish helper
rem
rem  Usage:
rem    build.bat            build Release, framework-dependent
rem    build.bat publish    publish console exe (self-contained single file)
rem    build.bat gui        publish GUI exe  (self-contained single file)
rem    build.bat all        publish both console and GUI
rem    build.bat clean      remove bin / obj
rem
rem  All dependencies live in D:\01_File\03_rea\tools
rem  (portable: delete that folder to uninstall everything)
rem
rem  NOTE: keep this file pure ASCII. cmd.exe parses .bat with the
rem  local code page (GBK on zh-CN); UTF-8 Chinese comments break it.
rem ============================================================
setlocal

set "REA_ROOT=D:\01_File\03_rea"
rem Override the portable toolchain root if it lives elsewhere:
rem   set REA_ROOT=D:\your\tools
rem If there is no portable SDK, fall back to whatever dotnet is on PATH.
if exist "%REA_ROOT%\tools\dotnet\dotnet.exe" (
    set "DOTNET=%REA_ROOT%\tools\dotnet\dotnet.exe"
) else (
    set "DOTNET=dotnet"
)
set "CODE_DIR=%~dp0"
set "OUT_CONSOLE=%REA_ROOT%\tools\IEC104Simulator"
rem GUI output folder and exe name are IEC104-Server:
rem the name shown in UI, in exe properties and in taskbar must match.
set "OUT_GUI=%REA_ROOT%\tools\IEC104-Server"

set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1

if /I "%DOTNET%"=="dotnet" (
    where dotnet >nul 2>nul || (
        echo ERROR: dotnet not found in PATH, and no portable SDK at %REA_ROOT%\tools\dotnet
        exit /b 1
    )
) else (
    if not exist "%DOTNET%" (
        echo ERROR: dotnet not found at %DOTNET%
        echo Install .NET 8 SDK into %REA_ROOT%\tools\dotnet first.
        exit /b 1
    )
)

if /I "%~1"=="publish" goto :publish
if /I "%~1"=="gui"     goto :gui
if /I "%~1"=="all"     goto :all
if /I "%~1"=="clean"   goto :clean
goto :build

:build
echo [build] Compiling Release ...
"%DOTNET%" build "%CODE_DIR%IEC104.sln" -c Release
if errorlevel 1 goto :failed
echo.
echo Build OK.
echo Console: %CODE_DIR%src\IEC104.Host\bin\Release\net8.0\IEC104Simulator.exe
echo GUI    : %CODE_DIR%src\IEC104.App\bin\Release\net8.0\IEC104Simulator.exe
echo Note: framework-dependent, target machine needs .NET 8 Runtime.
goto :end

:publish
echo [publish] Console exe - self-contained single file ...
call :publish_one "IEC104.Host" "%OUT_CONSOLE%"
if errorlevel 1 goto :failed
goto :end

:gui
echo [publish] GUI exe - self-contained single file ...
call :publish_one "IEC104.App" "%OUT_GUI%"
if errorlevel 1 goto :failed
rem AssemblyName stays IEC104Simulator (avares resource paths depend on it),
rem so the produced file is still IEC104Simulator.exe; rename it here.
if exist "%OUT_GUI%\IEC104Simulator.exe" (
    if exist "%OUT_GUI%\IEC104-Server.exe" del /Q "%OUT_GUI%\IEC104-Server.exe"
    move /Y "%OUT_GUI%\IEC104Simulator.exe" "%OUT_GUI%\IEC104-Server.exe" >nul
)
goto :end

:all
echo [publish] Console exe ...
call :publish_one "IEC104.Host" "%OUT_CONSOLE%"
if errorlevel 1 goto :failed
echo.
echo [publish] GUI exe ...
call :publish_one "IEC104.App" "%OUT_GUI%"
if errorlevel 1 goto :failed
if exist "%OUT_GUI%\IEC104Simulator.exe" (
    if exist "%OUT_GUI%\IEC104-Server.exe" del /Q "%OUT_GUI%\IEC104-Server.exe"
    move /Y "%OUT_GUI%\IEC104Simulator.exe" "%OUT_GUI%\IEC104-Server.exe" >nul
)
goto :end

rem %1 = project folder name, %2 = output folder
:publish_one
if exist "%~2" (
    echo Cleaning "%~2" ...
    del /Q "%~2\*.*" 2>nul
)
if not exist "%~2" mkdir "%~2"
rem Publish flags live in src/<project>/Properties/PublishProfiles/win-x64.pubxml,
rem so local builds and the GitHub Actions workflow cannot drift apart.
"%DOTNET%" publish "%CODE_DIR%src\%~1\%~1.csproj" -p:PublishProfile=win-x64 -o "%~2"
if errorlevel 1 exit /b 1
echo.
echo Publish OK: %~2\IEC104Simulator.exe
echo Note: self-contained, target machine needs nothing installed.
exit /b 0

:clean
echo [clean] Removing bin / obj ...
for /d /r "%CODE_DIR%src" %%d in (bin obj) do (
    if exist "%%d" rd /S /Q "%%d"
)
echo Clean OK.
goto :end

:failed
echo.
echo BUILD FAILED.
exit /b 1

:end
endlocal
