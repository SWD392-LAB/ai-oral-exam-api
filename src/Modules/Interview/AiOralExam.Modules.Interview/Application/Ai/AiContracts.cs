namespace AiOralExam.Modules.Interview.Application.Ai;

// =====================================================================
// Cac interface AI (BE-AI-02, BE-AI-03, BE-AI-06).
// Code nghiep vu chi phu thuoc cac interface nay, KHONG phu thuoc SDK nha cung cap.
// M1 dung ban Mock (thu muc Mock/). M2 them adapter that (OpenAI, Gemini, Google TTS...)
// va doi dang ky DI trong InterviewModule - khong sua cho goi.
// =====================================================================

/// <summary>Mot luot hoi/dap da dien ra trong cung 1 cau hoi chinh.</summary>
public sealed record DialogueTurn(string Question, string? Answer, bool IsFollowUp);

// ---------- Phan tich cau tra loi (de quyet dinh hoi xoay) - TACH RIENG voi cham diem ----------

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

// ---------- Cham diem theo rubric (goi 1 lan khi cau hoi chinh + cac cau xoay da xong) ----------

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

// ---------- Adapter dich vu ngoai (dung tu M2) ----------

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
    /// <summary>Goi LLM tra ve text (adapter that tu parse JSON neu can).</summary>
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);
}
