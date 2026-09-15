@echo off
setlocal EnableExtensions DisableDelayedExpansion
rem Builds with the Roslyn csc of Visual Studio or its Build Tools, found by vswhere: no SDK, no
rem NuGet, no project file.
rem   build.cmd             bin\TeamsKeepAlive.exe, version 0.0.0
rem   build.cmd 1.2.3       the same, stamped 1.2.3
rem   build.cmd test        bin\Tests.exe, run once per language
rem   build.cmd test live   adds checks against the Task Scheduler, the registry and this session
rem Every path is quoted and no block is parenthesised, so a folder name holding & ( ) is safe. A
rem comma is not: /resource splits on it.

set "TARGET=app"
set "VERSION=%~1"
set "LIVE="
if /i "%~1"=="test" set "TARGET=test"& set "VERSION="
if /i "%~2"=="live" if "%TARGET%"=="test" set "LIVE=live"
if not defined VERSION set "VERSION=0.0.0"
rem Anything else stops here, not as a csc error about the Version.cs written below. Digits spelt
rem out, as findstr's [0-9] takes a few non-ASCII ones; the 1 in front keeps a leading zero from
rem reading as octal, and csc takes no part over 65534.
if not "%~2"=="" if not defined LIVE goto usage
if not "%~3"=="" goto usage
set "PART=[0123456789][0123456789]*"
echo(%VERSION%| findstr /r /x "%PART%\.%PART%\.%PART%" > nul || goto usage
for %%p in (%VERSION:.= %) do if 1%%p gtr 165534 goto usage

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
rem Continued in column 0: the caret escapes the next line's first character, and an escaped space
rem in front of a command is read as its name.
if not exist "%VSWHERE%" >&2 ^
echo vswhere.exe not found - install Visual Studio or its Build Tools& exit /b 1
set "CSC="
set "VSARGS=-latest -products * -requires Microsoft.Component.MSBuild"
set "VSARGS=%VSARGS% -find MSBuild\Current\Bin\Roslyn\csc.exe"
for /f "usebackq delims=" %%i in (`"%VSWHERE%" %VSARGS%`) do set "CSC=%%i"
if not defined CSC >&2 ^
echo csc.exe not found - install the MSBuild component of Visual Studio& exit /b 1

set "SRC=%~dp0src"
set "ICO=%~dp0src\ico"
set "OUT=%~dp0bin"
if not exist "%OUT%" mkdir "%OUT%"
rem csc stamps the file and product versions from this one.
> "%OUT%\Version.cs" echo [assembly: System.Reflection.AssemblyVersion("%VERSION%.0")]

rem C# 7.3: the last version supported on .NET Framework. 32-bit preferred: about a third less
rem private memory for a daemon that runs all day. Subsystem 6.00, as MSBuild writes for a 4.5 or
rem later target: csc alone stamps 4.00.
set FLAGS=/nologo /langversion:7.3 /deterministic /platform:anycpu32bitpreferred
set FLAGS=%FLAGS% /optimize+ /subsystemversion:6.00 /codepage:65001 /warnaserror+
rem /noconfig: exactly these references, not the framework-wide set csc.rsp adds.
set REFS=/noconfig /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll
rem PNG frames throughout: Windows decodes them, and System.Drawing too for a 4.8 target.
set ICONS="/win32icon:%ICO%\active.ico" "/resource:%ICO%\paused.ico,paused.ico"
set ICONS=%ICONS% "/resource:%ICO%\blocked.ico,blocked.ico"
set MANIFEST="/win32manifest:%SRC%\app.manifest"
goto %TARGET%

:app
"%CSC%" %FLAGS% %REFS% %ICONS% %MANIFEST% /target:winexe ^
  "/out:%OUT%\TeamsKeepAlive.exe" "%SRC%\*.cs" "%OUT%\Version.cs" || exit /b 1
echo Built "%OUT%\TeamsKeepAlive.exe" %VERSION%
exit /b 0

:test
rem A managed copy of active.ico lets the checks read its icon directory.
"%CSC%" %FLAGS% %REFS% %ICONS% %MANIFEST% /target:exe /main:Tests /r:System.Xml.dll ^
  "/resource:%ICO%\active.ico,active.ico" "/out:%OUT%\Tests.exe" ^
  "%SRC%\*.cs" "%OUT%\Version.cs" "%~dp0tests\*.cs" || exit /b 1
rem Strings picks its language once per process. The live checks run in the first.
"%OUT%\Tests.exe" en %LIVE% || exit /b 1
"%OUT%\Tests.exe" fr || exit /b 1
exit /b 0

:usage
>&2 echo usage: build.cmd [MAJOR.MINOR.PATCH ^| test [live]]
exit /b 1
