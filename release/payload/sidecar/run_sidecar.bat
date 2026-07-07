@echo off
setlocal
cd /d "%~dp0"

set "INSTALL_ONLY="
if /I "%~1"=="--install-only" set "INSTALL_ONLY=1"

if not defined HF_HOME set "HF_HOME=%~dp0cache\huggingface"
if not defined TORCH_HOME set "TORCH_HOME=%~dp0cache\torch"
if not defined HF_HUB_DISABLE_SYMLINKS_WARNING set "HF_HUB_DISABLE_SYMLINKS_WARNING=1"

rem Without these, a wedged HuggingFace connection can hang the model load forever
rem (observed as CloseWait + 0% CPU), leaving the sidecar permanently "starting".
if not defined HF_HUB_ETAG_TIMEOUT set "HF_HUB_ETAG_TIMEOUT=5"
if not defined HF_HUB_DOWNLOAD_TIMEOUT set "HF_HUB_DOWNLOAD_TIMEOUT=15"

if defined PYTHON_EXE goto run

set "PYTHON_EXE=%~dp0.venv\Scripts\python.exe"
if exist "%PYTHON_EXE%" goto run

set "PYTHON_EXE=%~dp0..\.venv-sidecar312\Scripts\python.exe"
if exist "%PYTHON_EXE%" goto run

set "PY_BOOTSTRAP="
py -3.12 -c "import sys" >nul 2>nul
if not errorlevel 1 set "PY_BOOTSTRAP=py -3.12"
if defined PY_BOOTSTRAP goto bootstrap

python -c "import sys" >nul 2>nul
if not errorlevel 1 set "PY_BOOTSTRAP=python"
if defined PY_BOOTSTRAP goto bootstrap

python3 -c "import sys" >nul 2>nul
if not errorlevel 1 set "PY_BOOTSTRAP=python3"
if defined PY_BOOTSTRAP goto bootstrap

echo RadioChatter sidecar needs Python 3.10+ to install Pocket TTS dependencies.
echo Install Python, or set PYTHON_EXE to an existing environment with sidecar\requirements.txt installed.
exit /b 1

:bootstrap
echo Creating RadioChatter sidecar environment in "%~dp0.venv"...
%PY_BOOTSTRAP% -m venv "%~dp0.venv"
if errorlevel 1 exit /b %errorlevel%

set "PYTHON_EXE=%~dp0.venv\Scripts\python.exe"
"%PYTHON_EXE%" -m pip install --upgrade pip
if errorlevel 1 exit /b %errorlevel%
"%PYTHON_EXE%" -m pip install -r requirements.txt
if errorlevel 1 exit /b %errorlevel%
if defined INSTALL_ONLY (
    echo RadioChatter sidecar environment is ready.
    goto eof
)

:run
if defined INSTALL_ONLY goto install_only
"%PYTHON_EXE%" server.py --host 127.0.0.1 --port 5075 --voices voices.json --language english
goto eof

:install_only
"%PYTHON_EXE%" -m pip install --upgrade pip
if errorlevel 1 exit /b %errorlevel%
"%PYTHON_EXE%" -m pip install -r requirements.txt
if errorlevel 1 exit /b %errorlevel%
echo RadioChatter sidecar environment is ready.

:eof
