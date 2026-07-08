@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"

set "INSTALL_ONLY="
set "LOGGED_INSTALL="
set "INSTALL_LOG=%~dp0sidecar-install.log"
set "PIP_LOG=%~dp0sidecar-pip.log"
if /I "%~1"=="--install-only" set "INSTALL_ONLY=1"
if /I "%~2"=="--logged" set "LOGGED_INSTALL=1"

if defined INSTALL_ONLY if not defined LOGGED_INSTALL (
    echo Preparing RadioChatter Pocket TTS sidecar...
    echo This can take several minutes on the first install.
    echo Detailed log: "%INSTALL_LOG%"
    > "%INSTALL_LOG%" echo RadioChatter sidecar setup log
    >> "%INSTALL_LOG%" echo Started: !DATE! !TIME!
    >> "%INSTALL_LOG%" echo Working directory: !CD!
    call "%~f0" --install-only --logged >> "%INSTALL_LOG%" 2>&1
    set "RC_EXIT=!ERRORLEVEL!"
    >> "%INSTALL_LOG%" echo Finished: !DATE! !TIME! exit code !RC_EXIT!
    if "!RC_EXIT!"=="0" (
        echo RadioChatter sidecar environment is ready.
        exit /b 0
    )

    echo.
    echo RadioChatter sidecar setup failed with exit code !RC_EXIT!.
    echo See "%INSTALL_LOG%" for details.
    if not defined RC_NO_PAUSE_ON_ERROR pause
    exit /b !RC_EXIT!
)

if not defined HF_HOME set "HF_HOME=%~dp0cache\huggingface"
if not defined TORCH_HOME set "TORCH_HOME=%~dp0cache\torch"
if not defined HF_HUB_DISABLE_SYMLINKS_WARNING set "HF_HUB_DISABLE_SYMLINKS_WARNING=1"
if defined LOCALAPPDATA (
    set "RC_UV_HOME=%LOCALAPPDATA%\RadioChatter\uv"
    set "RC_PRIVATE_PYTHON_DIR=%LOCALAPPDATA%\RadioChatter\Python312"
) else (
    set "RC_UV_HOME=%~dp0uv"
    set "RC_PRIVATE_PYTHON_DIR=%~dp0python312"
)
if not defined UV_CACHE_DIR set "UV_CACHE_DIR=%RC_UV_HOME%\cache"

rem Without these, a wedged HuggingFace connection can hang the model load forever
rem (observed as CloseWait + 0% CPU), leaving the sidecar permanently "starting".
if not defined HF_HUB_ETAG_TIMEOUT set "HF_HUB_ETAG_TIMEOUT=5"
if not defined HF_HUB_DOWNLOAD_TIMEOUT set "HF_HUB_DOWNLOAD_TIMEOUT=15"

if defined PYTHON_EXE goto ensure_deps

set "LOCAL_PYTHON_EXE=%~dp0.venv\Scripts\python.exe"
set "PYTHON_EXE=%LOCAL_PYTHON_EXE%"
if exist "%PYTHON_EXE%" (
    call :validate_python >nul 2>nul
    if not errorlevel 1 goto ensure_deps
    echo Existing RadioChatter sidecar environment is not Python 3.10+; recreating it.
    call :remove_local_venv
    if errorlevel 1 exit /b 1
)

set "PYTHON_EXE=%~dp0..\.venv-sidecar312\Scripts\python.exe"
if exist "%PYTHON_EXE%" (
    call :validate_python >nul 2>nul
    if not errorlevel 1 goto ensure_deps
    echo Ignoring repo-level sidecar environment because it is not Python 3.10+.
)
set "PYTHON_EXE="

set "PY_BOOTSTRAP="
call :check_bootstrap py -3.12
if not errorlevel 1 set "PY_BOOTSTRAP=py -3.12"
if defined PY_BOOTSTRAP goto bootstrap

call :check_bootstrap python
if not errorlevel 1 set "PY_BOOTSTRAP=python"
if defined PY_BOOTSTRAP goto bootstrap

call :check_bootstrap python3
if not errorlevel 1 set "PY_BOOTSTRAP=python3"
if defined PY_BOOTSTRAP goto bootstrap

goto bootstrap_uv

:bootstrap
call :remove_local_venv
if errorlevel 1 exit /b %errorlevel%
echo Creating RadioChatter sidecar environment in "%~dp0.venv"...
call :run_cmd %PY_BOOTSTRAP% -m venv "%~dp0.venv"
if errorlevel 1 exit /b %errorlevel%
set "PYTHON_EXE=%~dp0.venv\Scripts\python.exe"
goto install_deps

:bootstrap_uv
rem No system Python: bootstrap a private standalone CPython with uv so the mod
rem works on machines without Python installed.
set "UV_EXE=%~dp0uv\uv.exe"
call :find_uv
if not errorlevel 1 goto uv_venv

echo No system Python found. Downloading uv to bootstrap a private Python 3.12...
if not exist "%~dp0uv" mkdir "%~dp0uv"
set "UV_ZIP=%~dp0uv\uv.zip"
call :run_cmd curl.exe -L --fail --silent --show-error -o "%UV_ZIP%" "https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip"
if errorlevel 1 (
    echo curl download failed; trying PowerShell...
    call :run_cmd powershell -NoProfile -ExecutionPolicy Bypass -Command "Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip' -OutFile '%UV_ZIP%'"
    if errorlevel 1 goto no_python
)
if not exist "%UV_ZIP%" goto no_python
call :run_cmd tar.exe -x -f "%UV_ZIP%" -C "%~dp0uv"
if errorlevel 1 (
    echo tar extraction failed; trying PowerShell Expand-Archive...
    call :run_cmd powershell -NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -LiteralPath '%UV_ZIP%' -DestinationPath '%~dp0uv' -Force"
    if errorlevel 1 goto no_python
)
del "%UV_ZIP%" >nul 2>nul
call :find_uv
if errorlevel 1 goto no_python

:uv_venv
call :remove_local_venv
if errorlevel 1 exit /b %errorlevel%
if not defined UV_PYTHON_INSTALL_DIR set "UV_PYTHON_INSTALL_DIR=%RC_UV_HOME%\python"
echo Creating RadioChatter sidecar environment in "%~dp0.venv" (uv-managed Python 3.12)...
echo uv managed Python directory: "%UV_PYTHON_INSTALL_DIR%"
call :run_cmd "%UV_EXE%" venv --seed --python 3.12 "%~dp0.venv"
if errorlevel 1 (
    echo uv failed to create the sidecar environment; falling back to the official Python installer.
    goto bootstrap_private_python
)
set "PYTHON_EXE=%~dp0.venv\Scripts\python.exe"
goto install_deps

:bootstrap_private_python
call :remove_local_venv
if errorlevel 1 exit /b %errorlevel%
if not exist "%RC_PRIVATE_PYTHON_DIR%\python.exe" (
    call :install_private_python
    if errorlevel 1 exit /b 1
)
set "PYTHON_EXE=%RC_PRIVATE_PYTHON_DIR%\python.exe"
call :validate_python
if errorlevel 1 (
    echo Private Python install is not usable: "%PYTHON_EXE%"
    exit /b 1
)
echo Creating RadioChatter sidecar environment in "%~dp0.venv" (private Python 3.12)...
call :run_cmd "%PYTHON_EXE%" -m venv "%~dp0.venv"
if errorlevel 1 exit /b %errorlevel%
set "PYTHON_EXE=%~dp0.venv\Scripts\python.exe"
goto install_deps

:ensure_deps
call :validate_python
if errorlevel 1 exit /b %errorlevel%
if defined INSTALL_ONLY goto install_deps
"%PYTHON_EXE%" -c "import importlib.util, sys; sys.exit(0 if importlib.util.find_spec('pocket_tts') and importlib.util.find_spec('numpy') else 1)" >nul 2>nul
if errorlevel 1 (
    echo RadioChatter sidecar dependencies are missing; installing now...
    goto install_deps
)
goto run

:install_deps
if not exist "%PYTHON_EXE%" (
    echo Expected Python was not found: "%PYTHON_EXE%"
    exit /b 1
)
call :validate_python
if errorlevel 1 exit /b %errorlevel%
echo Installing dependencies with "%PYTHON_EXE%" at !DATE! !TIME!
call :run_cmd "%PYTHON_EXE%" -m pip --log "%PIP_LOG%" install --upgrade pip
if errorlevel 1 exit /b %errorlevel%
call :run_cmd "%PYTHON_EXE%" -m pip --log "%PIP_LOG%" install -r requirements.txt
if errorlevel 1 exit /b %errorlevel%
if defined INSTALL_ONLY (
    echo RadioChatter sidecar environment is ready.
    goto eof
)

:run
"%PYTHON_EXE%" server.py --host 127.0.0.1 --port 5075 --voices voices.json --language english
goto eof

:no_python
echo RadioChatter sidecar could not find Python and failed to bootstrap a private Python.
echo Install Python 3.10+ from python.org, or place uv.exe anywhere under "%~dp0uv", then try again.
exit /b 1

:find_uv
if defined UV_EXE if exist "%UV_EXE%" exit /b 0
if exist "%~dp0uv" (
    for /r "%~dp0uv" %%F in (uv.exe) do (
        set "UV_EXE=%%F"
        exit /b 0
    )
)
exit /b 1

:check_bootstrap
%* -c "import sys; sys.exit(0 if sys.version_info >= (3, 10) else 1)" >nul 2>nul
exit /b %errorlevel%

:remove_local_venv
if exist "%~dp0.venv" (
    echo Removing "%~dp0.venv"...
    for /L %%R in (1,1,5) do (
        rmdir /s /q "%~dp0.venv" >nul 2>nul
        if not exist "%~dp0.venv" exit /b 0
        ping -n 2 127.0.0.1 >nul
    )
    echo Failed to remove "%~dp0.venv". Close any python.exe using it, then try again.
    exit /b 1
)
exit /b 0

:install_private_python
echo Installing private Python 3.12 to "%RC_PRIVATE_PYTHON_DIR%"...
if not exist "%RC_UV_HOME%\downloads" mkdir "%RC_UV_HOME%\downloads"
set "PYTHON_INSTALLER=%RC_UV_HOME%\downloads\python-3.12.10-amd64.exe"
if not exist "%PYTHON_INSTALLER%" (
    echo Downloading official Python 3.12 installer...
    call :run_cmd curl.exe -L --fail --silent --show-error -o "%PYTHON_INSTALLER%" "https://www.python.org/ftp/python/3.12.10/python-3.12.10-amd64.exe"
    if errorlevel 1 (
        echo curl download failed; trying PowerShell...
        call :run_cmd powershell -NoProfile -ExecutionPolicy Bypass -Command "Invoke-WebRequest -UseBasicParsing -Uri 'https://www.python.org/ftp/python/3.12.10/python-3.12.10-amd64.exe' -OutFile '%PYTHON_INSTALLER%'"
        if errorlevel 1 goto no_python
    )
)
if not exist "%PYTHON_INSTALLER%" goto no_python
call :run_cmd "%PYTHON_INSTALLER%" /quiet InstallAllUsers=0 TargetDir="%RC_PRIVATE_PYTHON_DIR%" Include_pip=1 Include_launcher=0 PrependPath=0 Include_test=0 Include_doc=0 Shortcuts=0
if errorlevel 1 exit /b %errorlevel%
if not exist "%RC_PRIVATE_PYTHON_DIR%\python.exe" (
    echo Python installer completed but "%RC_PRIVATE_PYTHON_DIR%\python.exe" was not found.
    exit /b 1
)
exit /b 0

:validate_python
set "RC_PYTHON_CHECK=%~dp0.python-check.tmp"
del "%RC_PYTHON_CHECK%" >nul 2>nul
"%PYTHON_EXE%" -c "import os, pathlib, sys; sys.exit(1) if sys.version_info < (3, 10) else pathlib.Path(os.environ['RC_PYTHON_CHECK']).write_text(sys.version.split()[0])" >nul 2>nul
if errorlevel 1 goto python_bad
if not exist "%RC_PYTHON_CHECK%" goto python_bad
del "%RC_PYTHON_CHECK%" >nul 2>nul
exit /b 0

:python_bad
echo "%PYTHON_EXE%" is not a usable Python 3.10+ executable.
del "%RC_PYTHON_CHECK%" >nul 2>nul
exit /b 1

:run_cmd
if defined LOGGED_INSTALL (
    %*
) else (
    %* >> "%INSTALL_LOG%" 2>&1
)
exit /b %errorlevel%

:eof
