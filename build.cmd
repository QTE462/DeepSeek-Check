@echo off
setlocal enabledelayedexpansion
rem Build script for DeepSeek Balance Widget. ASCII only: batch files are read
rem with the OEM code page, so non-ASCII comments break parsing.

set "APP=%~dp0"
set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "SRC=%APP%src"
set "TOOLS=%APP%tools"
set "ASSETS=E:\project\assets"
if exist "%APP%assets\deepseek_whale_character.png" set "ASSETS=%APP%assets"

if not exist "%CSC%" (
    echo [ERROR] csc.exe not found: %CSC%
    exit /b 1
)
if not exist "%TOOLS%" mkdir "%TOOLS%"

echo [1/4] Compiling icon tool...
"%CSC%" /nologo /target:exe /main:IconTool /out:"%TOOLS%\IconTool.exe" "%SRC%\IconTool.cs"
if errorlevel 1 (
    echo [ERROR] icon tool build failed
    exit /b 1
)

echo [2/4] Generating icon assets...
if exist "%ASSETS%\dsh_src_DSniang1.png" (
    "%TOOLS%\IconTool.exe" "%ASSETS%\dsh_src_DSniang1.png" "%ASSETS%"
    if errorlevel 1 echo [WARN] icon generation failed, reusing existing assets
) else (
    echo [WARN] source image not found, reusing existing assets
)

if not exist "%ASSETS%\deepseek_whale_character.png" (
    echo [ERROR] missing character image: %ASSETS%\deepseek_whale_character.png
    exit /b 1
)
if not exist "%ASSETS%\deepseek_whale.ico" (
    echo [ERROR] missing icon: %ASSETS%\deepseek_whale.ico
    exit /b 1
)

echo [3/4] Compiling widget...
"%CSC%" /nologo /target:winexe /main:DeepSeekBalance.Program /platform:anycpu /optimize+ /utf8output ^
 /out:"%APP%DeepSeekBalanceWidget.exe" ^
 /win32icon:"%ASSETS%\deepseek_whale.ico" ^
 /win32manifest:"%APP%app.manifest" ^
 /resource:"%ASSETS%\deepseek_whale_character.png",character.png ^
 /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
 "%SRC%\*.cs"
if errorlevel 1 (
    echo [ERROR] widget build failed
    exit /b 1
)

echo [4/4] Done.
dir /b "%APP%DeepSeekBalanceWidget.exe"
endlocal
