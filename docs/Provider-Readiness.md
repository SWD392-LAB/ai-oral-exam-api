# Provider readiness for Task-P

The API uses provider-neutral interfaces. The committed default is `Mock` so
CI and local development do not require billing. The opt-in demo configuration
is:

| Capability | Provider | Transport/output | Credential |
| --- | --- | --- | --- |
| LLM analysis/scoring | Gemini Flash | JSON response; analyzer/scorer validate the exact schema | `AiProviders:GeminiApiKey` via user-secrets/environment |
| STT | local faster-whisper sidecar | multipart `file` + `language`, JSON `{text}` | none; local process |
| TTS | local Piper/Coqui sidecar | JSON `{text, language}`, WAV/MP3 bytes or base64 JSON | none; local process |

The adapter boundary is `ILlmService`, `ISpeechToTextService`, and
`ITextToSpeechService`. Provider responses never go directly to the database:
the analyzer/scorer validators reject missing, duplicate, unknown, or
out-of-range rubric criteria and return `ai_evaluation_failed` for retry.

Record deployment measurements in this table when credentials and sample files
are available (the repository intentionally leaves secrets and private audio
out of source control):

| Run | Provider/model | Language | Input/output codec | p50/p95 latency | Quota/limit | Credential source |
| --- | --- | --- | --- | --- | --- | --- |
| pending | Gemini / configured model | vi-VN or en-US | JSON | pending | provider dashboard | user-secrets/env |
| pending | faster-whisper sidecar | vi-VN or en-US | WebM/WAV -> JSON text | pending | local capacity | none |
| pending | Piper/Coqui sidecar | vi-VN or en-US | JSON -> WAV/MP3 | pending | local capacity | none |

## Smoke protocol

Run this before enabling real providers in a deployment:

1. Use one Vietnamese and one English question with a two-criterion rubric.
2. Send a short answer and verify a follow-up is returned only while quota
   remains.
3. Send a complete answer and verify one score per criterion, each within its
   max points.
4. Upload a short WebM/WAV recording and verify the exact turn gets an audio
   reference, transcript, and the same scoring result as text.
5. Request question audio and verify the returned content type is playable.
6. Force a provider timeout; verify `503 ai_evaluation_failed`, persisted
   transcript/audio, `Failed` response state, and a successful retry.

Latency, quota, and provider quality are deployment measurements and should be
recorded from this protocol; this repository does not claim a provider smoke
run because the local environment has no configured provider credentials or
.NET SDK.
