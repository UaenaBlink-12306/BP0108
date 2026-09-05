@echo off
title Prime Viewer Bot Installer
color a

python --version >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Python is not installed or not in system PATH.
    echo Please install Python 3.10 or higher and check "Add Python to PATH".
    pause
    exit /b 1
)

echo [INFO] Setting up virtual environment (.venv)...
if not exist .venv (
    python -m venv .venv
    if errorlevel 1 (
        echo [ERROR] Failed to create virtual environment.
        pause
        exit /b 1
    )
)

echo [INFO] Installing required libraries from requirements.txt...
.venv\Scripts\pip install --upgrade pip
.venv\Scripts\pip install -r requirements.txt

if errorlevel 1 (
    echo [ERROR] Dependency installation failed.
    pause
    exit /b 1
)

echo.
echo ==================================================
echo [SUCCESS] Environment setup is complete!
echo You can now run the bot by double-clicking run.bat
echo ==================================================
pause