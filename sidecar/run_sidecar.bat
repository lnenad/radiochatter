@echo off
setlocal
cd /d "%~dp0"

set "INSTALL_ONLY="
if /I "%~1"=="--install-only" set "INSTALL_ONLY=1"

if not defined HF_HOME set "HF_HOME=%~dp0cache\huggingface"
if not defined TORCH_HOME set "TORCH_HOME=%~dp0cache\torch"
if not defined HF_HUB_DISABLE_SYMLINKS_WARNING set "HF_HUB_DISABLE_SYMLINKS_WARNING=1"
if not defined UV_CACHE_DIR set "UV_CACHE_DIR=%~dp0cache\uv"

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

goto bootstrap_uv

:bootstrap
echo Creating RadioChatter sidecar environment in "%~dp0.venv"...
%PY_BOOTSTRAP% -m venv "%~dp0.venv"
if errorlevel 1 exit /b %errorlevel%
goto install_deps

:bootstrap_uv
rem No system Python: bootstrap a private standalone CPython with uv so the mod
rem works on machines without Python installed.
set "UV_EXE=%~dp0uv\uv.exe"
if exist "%UV_EXE%" goto uv_venv

echo No system Python found. Downloading uv to bootstrap a private Python 3.12...
if not exist "%~dp0uv" mkdir "%~dp0uv"
set "UV_ZIP=%~dp0uv\uv.zip"
curl.exe -L --fail --silent --show-error -o "%UV_ZIP%" "https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip"
if errorlevel 1 powershell -NoProfile -ExecutionPolicy Bypass -Command "Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip' -OutFile '%UV_ZIP%'"
if not exist "%UV_ZIP%" goto no_python
tar.exe -x -f "%UV_ZIP%" -C "%~dp0uv"
del "%UV_ZIP%" >nul 2>nul
if not exist "%UV_EXE%" goto no_python

:uv_venv
if not defined UV_PYTHON_INSTALL_DIR set "UV_PYTHON_INSTALL_DIR=%~dp0uv\python"
echo Creating RadioChatter sidecar environment in "%~dp0.venv" (uv-managed Python 3.12)...
"%UV_EXE%" venv --seed --python 3.12 "%~dp0.venv"
if errorlevel 1 exit /b %errorlevel%
goto install_deps

:install_deps
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
goto eof

:no_python
echo RadioChatter sidecar could not find Python and failed to download uv.
echo Install Python 3.10+ from python.org, or place uv.exe in "%~dp0uv", then try again.
exit /b 1

:eof
