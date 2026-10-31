# Provider readiness for Task-P

The provider-neutral API keeps `Mock` as its general/test default. The committed
`Development` profile selects Gemini for LLM calls and local faster-whisper and
Piper sidecars for speech:

| Capability | Provider | Transport/output | Credential |
| --- | --- | --- | --- |
| F1 generation and F3 analysis/scoring | Gemini 3.5 Flash-Lite | JSON response; question citations and interview analyzer/scorer outputs are validated | `AiProviders:GeminiApiKey` via user-secrets/environment; free-tier quota applies |
| STT | local faster-whisper `small` model | multipart `file` + `language`, JSON `{text}` | none; local CPU/container |
| TTS | local Piper Vietnamese/English voices | JSON `{text, language}`, WAV bytes | none; local CPU/container |

Gemini's free tier is subject to account eligibility and rate limits; Google
states that free-tier prompts may be used to improve its products. Do not send
restricted course material through that tier unless its terms are acceptable
for that material. See the official [pricing](https://ai.google.dev/gemini-api/docs/pricing)
and [API-key](https://ai.google.dev/gemini-api/docs/api-key) pages.

Start the two speech services with
`docker compose --profile local-ai up -d --build faster-whisper piper-tts`.
They bind to loopback ports 8001 and 8002 and persist model downloads in named
Docker volumes. Piper's runtime is GPL-3.0-or-later, while voice assets have
individual terms; review the upstream [package license](https://github.com/OHF-Voice/piper1-gpl/blob/main/setup.py)
and [voice guidance](https://github.com/OHF-Voice/piper1-gpl/blob/main/docs/VOICES.md)
before redistributing those artifacts.

F3 adapter boundaries are `ILlmService`, `ISpeechToTextService`, and
`ITextToSpeechService`; F1 question generation has a separate provider adapter.
Provider responses never go directly to the database: F1 checks that the
source ID belongs to retrieved evidence and the quote is verbatim; F3
validators reject missing, duplicate, unknown, or out-of-range rubric
criteria and return `ai_evaluation_failed` for retry.

Record deployment measurements in this table when a Gemini key and approved
sample files are available (the repository intentionally leaves secrets and
private audio out of source control):

| Run | Provider/model | Language | Input/output codec | p50/p95 latency | Quota/limit | Credential source |
| --- | --- | --- | --- | --- | --- | --- |
| pending | Gemini 3.5 Flash-Lite | vi-VN or en-US | JSON | pending | free-tier quota | user-secrets/env; no key configured in this workspace |
| 2026-10-31 local smoke | faster-whisper `small` | vi-VN and en-US | WAV -> JSON text | one sample per language; latency not benchmarked | local CPU | none |
| 2026-10-31 local smoke | Piper `vi_VN-vais1000-medium`, `en_US-lessac-medium` | vi-VN and en-US | JSON -> WAV | one sample per language; latency not benchmarked | local CPU | none |

The local smoke generated WAV audio for both languages and posted each file to
faster-whisper. The English sample transcript matched exactly. The short
Vietnamese transcript was understandable but had two word-recognition errors;
this confirms the local CPU path runs, not production transcription quality.

## Smoke protocol

Run this before enabling real providers in a deployment:

1. Use one Vietnamese and one English question with a two-criterion rubric.
2. Generate an F1 draft from a course material and verify every citation quote
   exists in the retrieved page/slide; verify a request with no usable source
   returns `insufficient_source`.
3. Send a short answer and verify a follow-up is returned only while quota
   remains.
4. Send a complete answer and verify one score per criterion, each within its
   max points.
5. Upload a short WebM/WAV recording and verify the exact turn gets an audio
   reference, transcript, and the same scoring result as text.
6. Request question audio and verify the returned content type is playable.
7. Force a provider timeout; verify `503 ai_evaluation_failed`, persisted
   transcript/audio, `Failed` response state, and a successful retry.

Latency, quota, and provider quality are deployment measurements and should be
recorded from this protocol. Local speech smoke checks do not require a Gemini
key; F1/F3 LLM smoke checks do.
