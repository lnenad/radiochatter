#!/usr/bin/env python3
"""RadioChatter Pocket TTS sidecar.

HTTP contract:
  GET  /health -> 200 {"status":"ok","voices":[...],"stt":"ok"|"loading"|"error"|"disabled"} when ready
                  503 {"status":"loading","phase":"downloading"|"loading"} while the model loads
                  503 {"status":"error","error":"..."} when the model failed to load
                  ("status" reflects TTS only; speech-to-text readiness is the "stt" field)
  POST /speak  -> WAV bytes, JSON body {"text":"...", "voice":"awacs"}; 503 while not ready
  POST /transcribe -> {"text":"..."}, JSON body {"audio_b64":"<base64 WAV>", "prompt":"..."};
                  503 while the STT model is loading/failed/disabled

Concurrent /speak (and /transcribe) requests are accepted but executed one at a
time on persistent worker threads; overlapping requests queue FIFO.
"""

from __future__ import annotations

import argparse
import base64
import io
import json
import os
import sys
import threading
import time
import wave
from concurrent.futures import ThreadPoolExecutor
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Dict, Tuple


def _log(message: str) -> None:
    """Write a log line without ever raising: logging must not take down the server."""
    try:
        sys.stdout.write(message + "\n")
        sys.stdout.flush()
    except (OSError, ValueError, AttributeError):
        pass


def _ensure_std_streams() -> None:
    """Bind stdout/stderr to log files when the process has no console.

    Launched from the game plugin (a GUI process) with CreateNoWindow, the
    interpreter starts with sys.stdout/sys.stderr set to None; any direct
    write would raise and kill the request handler mid-response.
    """
    base = Path(__file__).resolve().parent
    if sys.stdout is None or sys.stdout.closed:
        sys.stdout = (base / "sidecar.stdout.log").open("a", buffering=1, encoding="utf-8")
    if sys.stderr is None or sys.stderr.closed:
        sys.stderr = (base / "sidecar.stderr.log").open("a", buffering=1, encoding="utf-8")


def _load_json(path: Path) -> Dict[str, str]:
    with path.open("r", encoding="utf-8") as handle:
        data = json.load(handle)
    if not isinstance(data, dict):
        raise ValueError(f"{path} must contain a JSON object")
    return {str(key): str(value) for key, value in data.items()}


def _audio_to_wav_bytes(sample_rate: int, audio) -> bytes:
    """Convert Pocket TTS output tensor/array to mono 16-bit PCM WAV."""
    if hasattr(audio, "detach"):
        audio = audio.detach().cpu().numpy()

    import numpy as np

    pcm = np.asarray(audio)
    if pcm.ndim > 1:
        pcm = pcm.reshape(-1)

    if pcm.dtype.kind == "f":
        pcm = np.clip(pcm, -1.0, 1.0)
        pcm16 = (pcm * 32767.0).astype("<i2")
    else:
        pcm16 = np.clip(pcm, -32768, 32767).astype("<i2")

    buf = io.BytesIO()
    with wave.open(buf, "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(int(sample_rate))
        wav.writeframes(pcm16.tobytes())
    return buf.getvalue()


class EngineState:
    """Shared load status the HTTP handlers report while the model loads in the background."""

    def __init__(self):
        self.lock = threading.Lock()
        self.engine: PocketTtsEngine | None = None
        self.status = "loading"  # loading | ok | error
        self.error = ""

    def snapshot(self):
        with self.lock:
            return self.engine, self.status, self.error


STATE = EngineState()

# The TTS and STT loader threads import overlapping dependency graphs (beartype,
# typing_extensions, huggingface_hub, ...). Concurrent first-time imports of the same
# module from two threads can expose a partially initialized module and permanently
# poison sys.modules, so all heavy engine imports are serialized through this lock.
IMPORT_LOCK = threading.Lock()

# PyTorch's native libraries (MKL fast memory manager, Intel OpenMP, oneDNN) keep
# per-thread caches that are never released when the owning OS thread exits. Any
# torch work executed on a short-lived thread therefore leaks a few MB of native
# memory per thread — invisible to gc/tracemalloc, unrecoverable by _heapmin().
# Pocket TTS spawns two fresh threads inside every generate_audio() call, and
# ThreadingHTTPServer runs each request handler (which drives the rest of the
# generation) on its own throwaway thread, so a long game session grew by roughly
# 3-10 MB per synthesized line (see plan/PocketTtsMemoryInvestigation.md).
# The fix is thread reuse: all torch work runs on the small persistent pools below.
#
# One worker per model also serializes generation, which Pocket TTS requires anyway
# (generate_audio is documented as not thread-safe); overlapping HTTP requests now
# queue FIFO instead of corrupting each other.
TTS_EXECUTOR = ThreadPoolExecutor(max_workers=1, thread_name_prefix="tts-serial")
STT_EXECUTOR = ThreadPoolExecutor(max_workers=1, thread_name_prefix="stt-serial")

# Pocket TTS internally starts a decoder thread and a generation thread per call
# (pocket_tts/models/tts_model.py). With /speak serialized above, at most two run
# concurrently; a third worker absorbs the brief overlap while the previous call's
# generation thread finishes its final log line.
_POCKET_TTS_INTERNAL_POOL = ThreadPoolExecutor(max_workers=3, thread_name_prefix="tts-internal")


class _PooledThread:
    """threading.Thread stand-in that runs the target on a persistent pool worker."""

    def __init__(self, group=None, target=None, name=None, args=(), kwargs=None, *, daemon=None):
        self._target = target
        self._args = args
        self._kwargs = kwargs or {}
        self._future = None

    def start(self):
        self._future = _POCKET_TTS_INTERNAL_POOL.submit(self._target, *self._args, **self._kwargs)

    def join(self, timeout=None):
        if self._future is None:
            return
        try:
            self._future.result(timeout)
        except Exception as exc:  # Thread.join never raises; report like the default excepthook
            _log(f"Pocket TTS worker thread failed: {type(exc).__name__}: {exc}")

    def is_alive(self):
        return self._future is not None and not self._future.done()


class _ThreadingProxy:
    """Forwards to the real threading module but hands out pooled threads."""

    Thread = _PooledThread

    def __getattr__(self, name):
        return getattr(threading, name)


def _patch_pocket_tts_threading() -> None:
    """Make Pocket TTS run its per-call worker threads on the persistent pool."""
    from pocket_tts.models import tts_model

    tts_model.threading = _ThreadingProxy()


class SttState:
    """Shared speech-to-text load status, independent of the TTS engine."""

    def __init__(self):
        self.lock = threading.Lock()
        self.engine: WhisperSttEngine | None = None
        self.status = "loading"  # loading | ok | error | disabled
        self.error = ""

    def snapshot(self):
        with self.lock:
            return self.engine, self.status, self.error


STT_STATE = SttState()


def _cache_is_warm() -> bool:
    """Best-effort guess whether the model still has to be downloaded.

    A completed HuggingFace snapshot means we are loading from disk; missing
    snapshots or *.incomplete blobs mean bytes are still coming over the wire.
    """
    try:
        if os.environ.get("HF_HUB_OFFLINE") == "1":
            return True
        hf_home = Path(os.environ.get("HF_HOME", str(Path.home() / ".cache" / "huggingface")))
        hub = hf_home / "hub"
        if not hub.is_dir():
            return False
        if any(hub.rglob("*.incomplete")):
            return False
        for _ in hub.glob("models--*/snapshots/*/*"):
            return True
        return False
    except OSError:
        return False


class PocketTtsEngine:
    def __init__(self, voices: Dict[str, str], language: str | None):
        try:
            with IMPORT_LOCK:
                from pocket_tts import TTSModel

                _patch_pocket_tts_threading()
        except Exception as exc:  # pragma: no cover - startup diagnostics
            raise RuntimeError(
                "pocket-tts is not installed. Install with: python -m pip install -r requirements.txt"
            ) from exc

        kwargs = {}
        if language:
            kwargs["language"] = language

        self.model = TTSModel.load_model(**kwargs)
        self.sample_rate = int(self.model.sample_rate)
        self.voice_prompts = voices
        self.voice_states = {}

        for prompt in sorted(set(voices.values())):
            self.voice_states[prompt] = self.model.get_state_for_audio_prompt(prompt)

    @property
    def voice_names(self):
        return sorted(self.voice_prompts.keys())

    def speak(self, text: str, voice: str) -> Tuple[int, bytes]:
        prompt = self.voice_prompts.get(voice, self.voice_prompts.get("default", "eve"))
        state = self.voice_states.get(prompt)
        if state is None:
            state = self.model.get_state_for_audio_prompt(prompt)
            self.voice_states[prompt] = state

        audio = self.model.generate_audio(state, text)
        return self.sample_rate, _audio_to_wav_bytes(self.sample_rate, audio)


class WhisperSttEngine:
    """Local speech-to-text via faster-whisper (CPU, int8). Short push-to-talk
    utterances transcribe in well under a second on any recent CPU."""

    def __init__(self, model_name: str):
        try:
            with IMPORT_LOCK:
                from faster_whisper import WhisperModel
        except Exception as exc:  # pragma: no cover - startup diagnostics
            raise RuntimeError(
                "faster-whisper is not installed. Install with: python -m pip install -r requirements.txt"
            ) from exc

        # cpu_threads is capped so transcription never fights the game for cores.
        self.model = WhisperModel(model_name, device="cpu", compute_type="int8", cpu_threads=4)

    def transcribe(self, wav_bytes: bytes, prompt: str) -> str:
        segments, _info = self.model.transcribe(
            io.BytesIO(wav_bytes),
            language="en",
            beam_size=2,
            vad_filter=True,
            condition_on_previous_text=False,
            initial_prompt=prompt or None,
        )
        return " ".join(segment.text.strip() for segment in segments).strip()


def _load_stt_background(model_name: str) -> None:
    """Load the STT model on its own thread; keep retrying like the TTS loader so a
    transient first-run download failure recovers without a restart."""
    while True:
        with STT_STATE.lock:
            STT_STATE.status = "loading"
        try:
            try:
                engine = WhisperSttEngine(model_name)
            except Exception as exc:
                if os.environ.get("HF_HUB_OFFLINE") == "1":
                    raise
                _log(f"STT model load failed ({type(exc).__name__}: {exc}); retrying with HF_HUB_OFFLINE=1...")
                _force_hf_offline()
                engine = WhisperSttEngine(model_name)
        except Exception as exc:
            message = f"{type(exc).__name__}: {exc}"
            with STT_STATE.lock:
                STT_STATE.status = "error"
                STT_STATE.error = message
            _log(f"STT model load failed: {message}; retrying in 60s...")
            time.sleep(60)
            continue

        with STT_STATE.lock:
            STT_STATE.engine = engine
            STT_STATE.status = "ok"
            STT_STATE.error = ""
        _log(f"Loaded STT model: {model_name}")
        return


class Handler(BaseHTTPRequestHandler):
    server_version = "RadioChatterPocketTTS/0.1"

    def log_message(self, fmt, *args):
        _log("%s - %s" % (self.address_string(), fmt % args))

    def _reject_non_loopback(self) -> bool:
        host = self.client_address[0]
        if host in ("127.0.0.1", "::1", "localhost"):
            return False
        self.send_error(403, "loopback only")
        return True

    def _json(self, status: int, payload: dict):
        data = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self._reject_non_loopback():
            return
        if self.path != "/health":
            self.send_error(404)
            return

        engine, status, error = STATE.snapshot()
        _stt_engine, stt_status, _stt_error = STT_STATE.snapshot()
        if engine is not None:
            self._json(200, {"status": "ok", "voices": engine.voice_names, "stt": stt_status})
        elif status == "error":
            self._json(503, {"status": "error", "error": error, "stt": stt_status})
        else:
            phase = "loading" if _cache_is_warm() else "downloading"
            self._json(503, {"status": "loading", "phase": phase, "stt": stt_status})

    def do_POST(self):
        if self._reject_non_loopback():
            return
        if self.path == "/transcribe":
            self._handle_transcribe()
            return
        if self.path != "/speak":
            self.send_error(404)
            return

        engine, status, error = STATE.snapshot()
        if engine is None:
            self._json(503, {"error": error or "voice model is not ready yet"})
            return

        try:
            length = int(self.headers.get("Content-Length", "0"))
            body = self.rfile.read(length)
            payload = json.loads(body.decode("utf-8"))
            text = str(payload.get("text", "")).strip()
            voice = str(payload.get("voice", "default")).strip() or "default"
            if not text:
                self._json(400, {"error": "text is required"})
                return

            # Run on the persistent TTS worker: torch on this throwaway handler
            # thread would leak native per-thread caches, and the single worker
            # serializes generation (Pocket TTS is not thread-safe).
            _, wav_bytes = TTS_EXECUTOR.submit(engine.speak, text, voice).result()
            self.send_response(200)
            self.send_header("Content-Type", "audio/wav")
            self.send_header("Content-Length", str(len(wav_bytes)))
            self.end_headers()
            self.wfile.write(wav_bytes)
        except Exception as exc:
            self._json(500, {"error": f"{type(exc).__name__}: {exc}"})

    def _handle_transcribe(self):
        engine, status, error = STT_STATE.snapshot()
        if engine is None:
            self._json(503, {"error": error or f"speech-to-text is {status}", "stt": status})
            return

        try:
            length = int(self.headers.get("Content-Length", "0"))
            body = self.rfile.read(length)
            payload = json.loads(body.decode("utf-8"))
            audio_b64 = str(payload.get("audio_b64", ""))
            prompt = str(payload.get("prompt", "")).strip()
            if not audio_b64:
                self._json(400, {"error": "audio_b64 is required"})
                return

            wav_bytes = base64.b64decode(audio_b64)
            started = time.monotonic()
            # Same reasoning as /speak: keep native inference off throwaway
            # handler threads and serialize it on one persistent worker.
            text = STT_EXECUTOR.submit(engine.transcribe, wav_bytes, prompt).result()
            _log(f"Transcribed {len(wav_bytes)} bytes in {time.monotonic() - started:.2f}s: {text!r}")
            self._json(200, {"text": text})
        except Exception as exc:
            self._json(500, {"error": f"{type(exc).__name__}: {exc}"})


def _force_hf_offline() -> None:
    """Switch huggingface_hub to offline mode in-process.

    The env var is read into huggingface_hub.constants at import time, so patch the
    already-imported module too; re-exec is not an option once the port is bound.
    """
    os.environ["HF_HUB_OFFLINE"] = "1"
    try:
        from huggingface_hub import constants as hf_constants

        hf_constants.HF_HUB_OFFLINE = True
    except Exception:
        pass


def _create_engine(voices: Dict[str, str], language: str | None) -> PocketTtsEngine:
    """Load the engine, falling back to HF offline mode so a warm cache still boots
    when huggingface.co is unreachable or a download stalls."""
    try:
        return PocketTtsEngine(voices, language)
    except Exception as exc:
        if os.environ.get("HF_HUB_OFFLINE") == "1":
            raise
        _log(f"Model load failed ({type(exc).__name__}: {exc}); retrying with HF_HUB_OFFLINE=1...")
        _force_hf_offline()
        return PocketTtsEngine(voices, language)


def _load_engine_background(voices: Dict[str, str], language: str | None) -> None:
    """Load the model on a background thread so /health can answer immediately.

    Keeps retrying every 60s so a transient failure (no internet on first run)
    recovers without a game restart."""
    while True:
        with STATE.lock:
            STATE.status = "loading"
        try:
            engine = _create_engine(voices, language)
        except Exception as exc:
            message = f"{type(exc).__name__}: {exc}"
            with STATE.lock:
                STATE.status = "error"
                STATE.error = message
            _log(f"Model load failed: {message}; retrying in 60s...")
            time.sleep(60)
            continue

        with STATE.lock:
            STATE.engine = engine
            STATE.status = "ok"
            STATE.error = ""
        _log(f"Loaded voices: {', '.join(engine.voice_names)}")
        return


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=5075)
    parser.add_argument("--voices", default=str(Path(__file__).with_name("voices.json")))
    parser.add_argument("--language", default="english")
    parser.add_argument("--stt-model", default="base.en",
                        help="faster-whisper model for /transcribe; empty string disables STT")
    args = parser.parse_args()

    _ensure_std_streams()

    voices = _load_json(Path(args.voices))
    if "default" not in voices:
        voices["default"] = "eve"

    server = ThreadingHTTPServer((args.host, args.port), Handler)
    _log(f"RadioChatter Pocket TTS sidecar listening on http://{args.host}:{args.port}")

    _log("Loading Pocket TTS model and voices in the background...")
    loader = threading.Thread(
        target=_load_engine_background, args=(voices, args.language), name="model-loader", daemon=True
    )
    loader.start()

    if args.stt_model:
        _log(f"Loading speech-to-text model '{args.stt_model}' in the background...")
        stt_loader = threading.Thread(
            target=_load_stt_background, args=(args.stt_model,), name="stt-loader", daemon=True
        )
        stt_loader.start()
    else:
        with STT_STATE.lock:
            STT_STATE.status = "disabled"
        _log("Speech-to-text is disabled (--stt-model '').")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        _log("Stopping sidecar.")
    finally:
        server.server_close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
