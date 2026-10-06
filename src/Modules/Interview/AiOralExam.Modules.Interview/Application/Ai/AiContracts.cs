namespace AiOralExam.Modules.Interview.Application.Ai;

// =====================================================================
// AI interfaces (BE-AI-02, BE-AI-03, BE-AI-06).
// Business code depends only on these interfaces, NEVER on a provider SDK.
// M1 uses the mocks (Mock/ folder). M2 adds real adapters (OpenAI, Gemini, Google TTS...)
// and swaps the DI registration in InterviewModule - callers do not change.
// =====================================================================

/// <summary>One question/answer exchange within the same main question.</summary>
public sealed record DialogueTurn(string Question, string? Answer, bool IsFollowUp);

// ---------- Answer analysis (decides on follow-ups) - SEPARATE from scoring ----------

public sealed record AnswerAnalysisRequest(
    string MainQuestion,
    string RubricCriteria,
    IReadOnlyList<DialogueTurn> Dialogue,
    int FollowUpsUsed,
    int MaxFollowUps,
    string Language);

public sealed record AnswerAnalysis(
    bool IsSufficient,
    IReadOnlyList<string> MissingPoints,
    bool NeedsFollowUp,
    string? FollowUpQuestion);

public interface IAnswerAnalyzer
{
    Task<AnswerAnalysis> AnalyzeAsync(AnswerAnalysisRequest request, CancellationToken ct = default);
}

// ---------- Rubric scoring (called once, after the main question and its follow-ups are done) ----------

public sealed record RubricScoringRequest(
    string MainQuestion,
    string RubricCriteria,
    decimal MaxScore,
    IReadOnlyList<DialogueTurn> Dialogue,
    string Language);

public sealed record RubricScore(decimal SuggestedScore, string Feedback);

public interface IRubricScorer
{
    Task<RubricScore> ScoreAsync(RubricScoringRequest request, CancellationToken ct = default);
}

// ---------- External service adapters (used from M2) ----------

public interface ISpeechToTextService
{
    /// <summary>Audio -> transcript.</summary>
    Task<string> TranscribeAsync(Stream audio, string contentType, string language, CancellationToken ct = default);
}

public interface ITextToSpeechService
{
    /// <summary>Text -> audio (vd. audio/mpeg).</summary>
    Task<(byte[] Audio, string ContentType)> SynthesizeAsync(string text, string language, CancellationToken ct = default);
}

public interface ILlmService
{
    /// <summary>Calls the LLM and returns text (the real adapter parses JSON if needed).</summary>
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);
}
