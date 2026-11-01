import asyncio
import os
import tempfile
import threading
from contextlib import asynccontextmanager
from pathlib import Path

from faster_whisper import WhisperModel
from fastapi import FastAPI, File, Form, HTTPException, UploadFile


MAX_AUDIO_BYTES = int(os.getenv("MAX_AUDIO_BYTES", str(15 * 1024 * 1024)))
MODEL_CACHE = os.getenv("MODEL_CACHE", "/models")
MODEL_NAME = os.getenv("WHISPER_MODEL", "small")
DEVICE = os.getenv("WHISPER_DEVICE", "cpu")
COMPUTE_TYPE = os.getenv("WHISPER_COMPUTE_TYPE", "int8")
CPU_THREADS = int(os.getenv("WHISPER_CPU_THREADS", "4"))
_model: WhisperModel | None = None
_inference_lock = threading.Lock()

_MIME_EXTENSIONS = {
    "audio/webm": ".webm",
    "audio/wav": ".wav",
    "audio/x-wav": ".wav",
    "audio/mpeg": ".mp3",
    "audio/mp3": ".mp3",
    "audio/ogg": ".ogg",
    "audio/mp4": ".m4a",
    "audio/aac": ".aac",
    "audio/flac": ".flac",
}


def load_model() -> WhisperModel:
    return WhisperModel(
        MODEL_NAME,
        device=DEVICE,
        compute_type=COMPUTE_TYPE,
        cpu_threads=CPU_THREADS,
        download_root=MODEL_CACHE,
    )


@asynccontextmanager
async def lifespan(_: FastAPI):
    global _model
    _model = await asyncio.to_thread(load_model)
    yield
    _model = None


app = FastAPI(title="Local faster-whisper", docs_url=None, redoc_url=None, lifespan=lifespan)


@app.get("/health")
async def health() -> dict[str, str]:
    if _model is None:
        raise HTTPException(status_code=503, detail="model_loading")
    return {"status": "ready"}


def _transcribe(path: str, language: str | None) -> str:
    if _model is None:
        raise RuntimeError("model_not_ready")
    with _inference_lock:
        segments, _ = _model.transcribe(
            path,
            language=language,
            beam_size=1,
            vad_filter=True,
        )
        return " ".join(segment.text.strip() for segment in segments).strip()


@app.post("/transcribe")
async def transcribe(
    file: UploadFile = File(...),
    language: str = Form(default="auto"),
) -> dict[str, str]:
    if _model is None:
        raise HTTPException(status_code=503, detail="model_not_ready")

    audio = await file.read(MAX_AUDIO_BYTES + 1)
    if not audio:
        raise HTTPException(status_code=400, detail="empty_audio")
    if len(audio) > MAX_AUDIO_BYTES:
        raise HTTPException(status_code=413, detail="audio_too_large")

    mime_type = (file.content_type or "").split(";", 1)[0].lower()
    suffix = _MIME_EXTENSIONS.get(mime_type, Path(file.filename or "").suffix or ".audio")
    normalized_language = language.strip().replace("_", "-").split("-", 1)[0].lower()
    language_code = None if normalized_language in {"", "auto", "automatic"} else normalized_language

    path = ""
    try:
        with tempfile.NamedTemporaryFile(prefix="stt-", suffix=suffix, delete=False) as temporary:
            temporary.write(audio)
            path = temporary.name
        text = await asyncio.to_thread(_transcribe, path, language_code)
    except Exception as error:
        # Do not include request content, temporary paths, or model internals in the response.
        raise HTTPException(status_code=422, detail="transcription_failed") from error
    finally:
        if path:
            Path(path).unlink(missing_ok=True)

    return {"text": text}
