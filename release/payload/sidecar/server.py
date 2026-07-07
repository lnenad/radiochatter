#!/usr/bin/env python3
"""RadioChatter Pocket TTS sidecar.

HTTP contract:
  GET  /health -> {"status":"ok","voices":[...]}
  POST /speak  -> WAV bytes, JSON body {"text":"...", "voice":"awacs"}
"""

from __future__ import annotations

import argparse
import io
import json
import os
import sys
import wave
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Dict, Tuple


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


class PocketTtsEngine:
    def __init__(self, voices: Dict[str, str], language: str | None):
        try:
            from pocket_tts import TTSModel
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


class Handler(BaseHTTPRequestHandler):
    engine: PocketTtsEngine

    server_version = "RadioChatterPocketTTS/0.1"

    def log_message(self, fmt, *args):
        sys.stdout.write("%s - %s\n" % (self.address_string(), fmt % args))
        sys.stdout.flush()

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
        self._json(200, {"status": "ok", "voices": self.engine.voice_names})

    def do_POST(self):
        if self._reject_non_loopback():
            return
        if self.path != "/speak":
            self.send_error(404)
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

            _, wav_bytes = self.engine.speak(text, voice)
            self.send_response(200)
            self.send_header("Content-Type", "audio/wav")
            self.send_header("Content-Length", str(len(wav_bytes)))
            self.end_headers()
            self.wfile.write(wav_bytes)
        except Exception as exc:
            self._json(500, {"error": f"{type(exc).__name__}: {exc}"})


def _create_engine(voices: Dict[str, str], language: str | None) -> PocketTtsEngine:
    """Load the engine, falling back to HF offline mode so a warm cache still boots
    when huggingface.co is unreachable or a download stalls.

    huggingface_hub reads HF_HUB_OFFLINE at import time, so the retry has to re-exec
    the interpreter rather than just set the variable.
    """
    try:
        return PocketTtsEngine(voices, language)
    except Exception as exc:
        if os.environ.get("HF_HUB_OFFLINE") == "1":
            raise
        print(
            f"Model load failed ({type(exc).__name__}: {exc}); retrying with HF_HUB_OFFLINE=1...",
            flush=True,
        )
        os.environ["HF_HUB_OFFLINE"] = "1"
        os.execve(sys.executable, [sys.executable] + sys.argv, os.environ)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=5075)
    parser.add_argument("--voices", default=str(Path(__file__).with_name("voices.json")))
    parser.add_argument("--language", default="english")
    args = parser.parse_args()

    voices = _load_json(Path(args.voices))
    if "default" not in voices:
        voices["default"] = "eve"

    print("Loading Pocket TTS model and voices...", flush=True)
    Handler.engine = _create_engine(voices, args.language)
    print(f"Loaded voices: {', '.join(Handler.engine.voice_names)}", flush=True)

    server = ThreadingHTTPServer((args.host, args.port), Handler)
    print(f"RadioChatter Pocket TTS sidecar listening on http://{args.host}:{args.port}", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("Stopping sidecar.", flush=True)
    finally:
        server.server_close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
