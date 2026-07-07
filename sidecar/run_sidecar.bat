@echo off
setlocal
cd /d "%~dp0"

if not defined HF_HOME set "HF_HOME=%~dp0cache\huggingface"
if not defined TORCH_HOME set "TORCH_HOME=%~dp0cache\torch"
if not defined HF_HUB_DISABLE_SYMLINKS_WARNING set "HF_HUB_DISABLE_SYMLINKS_WARNING=1"

rem Without these, a wedged HuggingFace connection can hang the model load forever
rem (observed as CloseWait + 0% CPU), leaving the sidecar permanently "starting".
if not defined HF_HUB_ETAG_TIMEOUT set "HF_HUB_ETAG_TIMEOUT=5"
if not defined HF_HUB_DOWNLOAD_TIMEOUT set "HF_HUB_DOWNLOAD_TIMEOUT=15"

set "PYTHON_EXE=%~dp0.venv\Scripts\python.exe"
if exist "%PYTHON_EXE%" goto run

set "PYTHON_EXE=%~dp0..\.venv-sidecar312\Scripts\python.exe"
if exist "%PYTHON_EXE%" goto run

set "PYTHON_EXE=python"

:run
"%PYTHON_EXE%" server.py --host 127.0.0.1 --port 5075 --voices voices.json --language english
