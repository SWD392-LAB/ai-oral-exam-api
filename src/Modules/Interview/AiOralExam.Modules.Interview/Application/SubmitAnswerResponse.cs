namespace AiOralExam.Modules.Interview.Application;

public sealed record SubmitAnswerResponse(
    string Outcome,
    bool TimedOut,
    // Null when Outcome = FollowUp or when AI scores are hidden from students.
    EvaluationDto? Evaluation,
    AttemptStateDto Attempt,
    // Returned only by the audio endpoint so the student sees the provider transcript.
    string? Transcript = null);
