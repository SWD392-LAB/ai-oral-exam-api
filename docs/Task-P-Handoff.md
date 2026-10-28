# Task-P backend handoff

Phúc's backend slice is implemented in this repository. The frontend can use
the following contracts without depending on provider SDKs.

## Text interview

`GET /api/interview-attempts/{id}` returns `currentTurn.turnId`. Send that value
back in `POST /api/interview-attempts/{id}/responses` together with
`answerText`. `turnId` is required to make retries idempotent. A provider failure returns `503 ai_evaluation_failed`; the
transcript remains stored and the same turn can be retried.

Reporting responses also expose `aiEvaluationState` (`NotStarted`, `Pending`,
`Failed`, `Completed`) and a sanitized retry message. Reporting/F6 must keep
Pending/Failed suggestions out of confirmed student results.

## Voice interview

- `POST /api/interview-attempts/{id}/audio` (`multipart/form-data`: `turnId`,
  `file`) stores the recording privately, runs STT, and enters the same text
  evaluation pipeline.
- `GET /api/interview-attempts/{id}/turns/{turnId}/question-audio` returns TTS
  audio for the exact stored question.
- `GET /api/interview-attempts/{id}/turns/{turnId}/answer-audio` returns the
  private recording for its owner, assigned lecturer, or administrator.

The default provider mode is mock. For a no-billing local demo, configure
`AiProviders:SttProvider=LocalHttp` and `AiProviders:TtsProvider=LocalHttp`.
The sidecar request/response shapes are documented in the root README.
The current backend exposes complete-turn audio/transcript endpoints. Partial
transcript streaming and reconnect event ordering remain the Minh-owned
SignalR/event integration seam; the FE should use `currentTurn.turnId` and
retry the same turn until that contract is merged.

## F1 generation demo

`POST /api/courses/{courseId}/question-bank/generations` accepts `topic`,
`bloom`, `count`, `sourceExcerpts`, `sourceMaterialIds`, and
`platformRubricVersionId`. It returns
`Draft` candidates with source citations and persists them as bank Draft items.
The returned `draftId` is the persisted bank `itemId`, so the lecturer can
approve/reject it directly through the question-bank route.
It never writes rubric data, auto-approves a question, or creates a session
attachment; approval/review and attach remain explicit question-bank actions.

Course source material is uploaded through
`/api/courses/{courseId}/materials`, ingested through
`/api/courses/{courseId}/materials/{materialId}/ingest`, and retrieved only
within the same course. The demo extractor supports PDF, PPTX, TXT and
Markdown and emits citation-labelled chunks.
