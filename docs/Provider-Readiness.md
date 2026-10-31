# Provider readiness for Task-P

The provider-neutral API keeps `Mock` as its general/test default. The
`Development` profile selects local Ollama for F1/F3 LLM calls, faster-whisper
for STT, and Piper for speech:

| Capability | Provider | Transport/output | Credential |
| --- | --- | --- | --- |
| F1 generation and F3 analysis/scoring | Ollama `qwen2.5:3b` | JSON response; question citations and interview analyzer/scorer outputs are validated | none; local CPU/container |
| STT | local faster-whisper `small` model | multipart `file` + `language`, JSON `{text}` | none; local CPU/container |
| TTS | local Piper Vietnamese/English voices | JSON `{text, language}`, WAV bytes | none; local CPU/container |

Gemini remains available as an optional provider with `AiProviders:LlmProvider=Gemini`
and a key supplied through user-secrets or the environment. Its free tier has
account eligibility and rate limits, and Google states that free-tier prompts
may be used to improve its products. Do not send restricted course material
through that tier unless its terms are acceptable. See Google's official
[pricing](https://ai.google.dev/gemini-api/docs/pricing) and
[API-key](https://ai.google.dev/gemini-api/docs/api-key) pages.

The local default uses the Qwen 2.5 3B model (about 1.9 GB); Ollama lists the
3B release under the Qwen license, so review that license before redistribution.
See the [Ollama model card](https://ollama.com/library/qwen2.5:3b).

Start the local providers with
`docker compose --profile local-ai up -d --build ollama faster-whisper piper-tts`,
then pull the LLM once with
`docker compose exec ollama ollama pull qwen2.5:3b`. They bind to loopback
ports 11434, 8001, and 8002 and persist model downloads in named Docker
volumes. Piper's runtime is GPL-3.0-or-later, while voice assets have
individual terms; review the upstream [package license](https://github.com/OHF-Voice/piper1-gpl/blob/main/setup.py)
and [voice guidance](https://github.com/OHF-Voice/piper1-gpl/blob/main/docs/VOICES.md)
before redistributing those artifacts.

F3 adapter boundaries are `ILlmService`, `ISpeechToTextService`, and
`ITextToSpeechService`; F1 question generation has a separate provider adapter.
Provider responses never go directly to the database: F1 checks that the
source ID belongs to retrieved evidence and the quote is verbatim; F3
validators reject missing, duplicate, unknown, or out-of-range rubric
criteria and return `ai_evaluation_failed` for retry.

Record deployment measurements in this table for each real-provider run using
approved sample files. The repository intentionally leaves credentials and
private audio out of source control:

| Run | Provider/model | Language | Input/output codec | p50/p95 latency | Quota/limit | Credential source |
| --- | --- | --- | --- | --- | --- | --- |
| pending | Ollama `qwen2.5:3b` | vi-VN and en-US | JSON | pending | local CPU; no hosted quota | none |
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
recorded from this protocol. The local Ollama, Whisper, and Piper smoke checks
do not require hosted credentials. Only a Gemini run needs a Google API key.
