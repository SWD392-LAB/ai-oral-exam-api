import asyncio
import io
import os
import subprocess
import sys
import threading
import wave
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
from piper import PiperVoice
from starlette.responses import Response


VOICES_DIR = Path(os.getenv("PIPER_VOICES_DIR", "/voices"))
VI_VOICE = os.getenv("PIPER_VI_VOICE", "vi_VN-vais1000-medium")
EN_VOICE = os.getenv("PIPER_EN_VOICE", "en_US-lessac-medium")
MAX_TEXT_CHARS = int(os.getenv("MAX_TEXT_CHARS", "10000"))
_voices: dict[str, PiperVoice] = {}
_synthesis_lock = threading.Lock()


def load_voices() -> dict[str, PiperVoice]:
    VOICES_DIR.mkdir(parents=True, exist_ok=True)
    subprocess.run(
        [
            sys.executable,
            "-m",
            "piper.download_voices",
            "--data-dir",
            str(VOICES_DIR),
            VI_VOICE,
            EN_VOICE,
        ],
        check=True,
        timeout=600,
        stdout=subprocess.DEVNULL,
    )
    return {
        "vi": PiperVoice.load(str(VOICES_DIR / f"{VI_VOICE}.onnx")),
        "en": PiperVoice.load(str(VOICES_DIR / f"{EN_VOICE}.onnx")),
    }


@asynccontextmanager
async def lifespan(_: FastAPI):
    global _voices
    _voices = await asyncio.to_thread(load_voices)
    yield
    _voices = {}


app = FastAPI(title="Local Piper TTS", docs_url=None, redoc_url=None, lifespan=lifespan)


class SynthesisRequest(BaseModel):
    text: str
    language: str = "vi-VN"


@app.get("/health")
async def health() -> dict[str, str]:
    if not _voices:
        raise HTTPException(status_code=503, detail="voices_loading")
    return {"status": "ready"}


def _synthesize(text: str, voice: PiperVoice) -> bytes:
    output = io.BytesIO()
    with _synthesis_lock, wave.open(output, "wb") as wav_file:
        voice.synthesize_wav(text, wav_file)
    return output.getvalue()


@app.post("/synthesize")
async def synthesize(request: SynthesisRequest) -> Response:
    text = request.text.strip()
    if not text:
        raise HTTPException(status_code=400, detail="text_required")
    if len(text) > MAX_TEXT_CHARS:
        raise HTTPException(status_code=413, detail="text_too_long")
    if not _voices:
        raise HTTPException(status_code=503, detail="voices_not_ready")

    language = request.language.strip().replace("_", "-").split("-", 1)[0].lower()
    voice = _voices.get("vi" if language == "vi" else "en")
    try:
        audio = await asyncio.to_thread(_synthesize, text, voice)
    except Exception as error:
        # Never return generated text or voice/model details to the caller.
        raise HTTPException(status_code=500, detail="synthesis_failed") from error
    return Response(content=audio, media_type="audio/wav")
