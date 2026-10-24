using AiOralExam.Modules.AccessConfig.Contracts;

namespace AiOralExam.Modules.Interview.Application.Ai;

// =====================================================================
// AI interfaces (BE-AI-02, BE-AI-03, BE-AI-06).
// Business code depends only on these interfaces, NEVER on a provider SDK.
// M1 uses the mocks (Mock/ folder). M2 adds real adapters (OpenAI, Gemini, Google TTS...)
// and swaps the DI registration in InterviewModule - callers do not change.
// =====================================================================

/// <summary>One question/answer exchange within the same main question.</summary>
public sealed record DialogueTurn(string Question, string? Answer, bool IsFollowUp);
