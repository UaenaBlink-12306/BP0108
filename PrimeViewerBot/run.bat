@echo off
title Prime Viewer Bot
color a

python --version >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Python is not installed or not in system PATH.
    echo Please install Python 3.10 or higher and check "Add Python to PATH".
    pause
    exit /b 1
)

if not exist .venv (
    echo [INFO] Virtual environment .venv not found. Setting it up now...
    python -m venv .venv
    if errorlevel 1 (
        echo [ERROR] Failed to create virtual environment.
        pause
        exit /b 1
    )
    echo [INFO] Virtual environment created successfully.
    echo [INFO] Installing required libraries from requirements.txt...
    .venv\Scripts\pip install --upgrade pip
    .venv\Scripts\pip install -r requirements.txt
    if errorlevel 1 (
        echo [ERROR] Failed to install required libraries.
        pause
        exit /b 1
    )
    echo [INFO] Setup complete! Starting the bot...
    echo.
)

.venv\Scripts\python.exe main.py
if errorlevel 1 (
    echo.
    echo [ERROR] The bot encountered an error and closed.
    pause
)