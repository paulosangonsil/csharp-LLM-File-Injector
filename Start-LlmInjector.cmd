@echo off
title LLM File Injector Startup
echo ===================================================
echo   Starting AI Environment and Code Injector
echo ===================================================
echo.

set "TARGET_DIR=%~1"
if "%TARGET_DIR%"=="" set "TARGET_DIR=%cd%"

:: Resolve where the .bat lives and then the bin output
set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"

set "UTIL_DIR=%SCRIPT_DIR%\bin\Release\net10.0-windows"

echo [INFO] Target Directory: "%TARGET_DIR%"
echo [INFO] Utility Directory: "%UTIL_DIR%"
echo.

if not exist "%UTIL_DIR%\BrowserFileInjector.exe" (
    echo [ERROR] BrowserFileInjector.exe not found in "%UTIL_DIR%".
    timeout /t 5 /nobreak >nul
    exit /b 1
)

if not exist "%UTIL_DIR%\playwright.ps1" (
    echo [ERROR] playwright.ps1 not found in "%UTIL_DIR%".
    timeout /t 5 /nobreak >nul
    exit /b 1
)

:: Ensure Playwright is installed
echo [0] Ensuring Playwright browsers are installed for this utility folder...
pushd "%UTIL_DIR%"

:: Key fix: do not use ".\playwright.ps1"
:: Option 1: script relative to current dir
:: powershell -ExecutionPolicy Bypass -File "playwright.ps1" install

:: Option 2 (recommended): explicit full path
powershell -ExecutionPolicy Bypass -File "%UTIL_DIR%\playwright.ps1" install

if errorlevel 1 (
    echo "[WARN] Playwright installation reported an error. Continuing anyway..."
) else (
    echo "[OK] Playwright install step completed (or was already up to date)."
)

popd

:: 1. Close Edge if it's already running (Required to free the CDP port)
echo [1] Closing existing instances of Microsoft Edge...
taskkill /F /IM msedge.exe /T >nul 2>&1
timeout /t 2 /nobreak >nul

:: 2. Start Edge in debugging mode
echo [2] Starting Microsoft Edge with Remote Debugging (Port 9222)...
start "" "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" --profile-directory="Default" --remote-debugging-port=9222 "https://www.perplexity.ai/"

echo [3] Waiting for the browser to load tabs...
timeout /t 5 /nobreak >nul

:: 3. Start the Console Application, passing the target directory
echo [4] Starting the BrowserFileInjector service...
pushd "%UTIL_DIR%"
start "" "BrowserFileInjector.exe" "%TARGET_DIR%"
popd

echo.
echo All set! The service is now running in the background.
echo Target folder for files: %TARGET_DIR%
echo Go to Edge and press Ctrl + Alt + F to inject your files.
echo This window will close automatically in 3 seconds.
timeout /t 3 /nobreak >nul
rem exit /b 0
