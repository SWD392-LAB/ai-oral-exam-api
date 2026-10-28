using AiOralExam.Modules.Interview.Contracts;

namespace AiOralExam.Modules.Interview.Domain;

/// <summary>ERD: QUESTION RESPONSE - the answer to ONE main question (several turns: main + follow-ups).</summary>
public class QuestionResponse
{
    public Guid Id { get; set; }
    public Guid AttemptId { get; set; }
    public Guid QuestionId { get; set; }   // FK to questions (module F7)
    public int FollowUpCount { get; set; }
    public bool IsFinished { get; set; }

    /// <summary>AI-suggested score = sum of CriterionScores.Points (also kept in sync by a trigger in 01_schema.sql).</summary>
    public decimal? AiSuggestedScore { get; set; }
    public string? AiFeedback { get; set; }
    public DateTime? EvaluatedAt { get; set; }
    public AiEvaluationState AiEvaluationState { get; set; } = AiEvaluationState.NotStarted;
    public string? AiEvaluationError { get; set; }

    public ICollection<InterviewTurn> Turns { get; set; } = new List<InterviewTurn>();
    public ICollection<CriterionScore> CriterionScores { get; set; } = new List<CriterionScore>();

    /// <summary>The turn waiting for an answer = the last turn, if it has no AnsweredAt yet.</summary>
    public InterviewTurn? CurrentTurn
    {
        get
        {
            var last = Turns.MaxBy(t => t.TurnNo);
            return last is { AnsweredAt: null, IsTimedOut: false, ProcessingStatus: TurnProcessingStatus.AwaitingAnswer }
                ? last
                : null;
        }
    }

    public InterviewTurn AddTurn(TurnType type, string questionText, DateTime utcNow)
    {
        var turn = new InterviewTurn
        {
            Id = Guid.NewGuid(),
            QuestionResponseId = Id,
            TurnNo = Turns.Count == 0 ? 1 : Turns.Max(t => t.TurnNo) + 1,
            Type = type,
            QuestionText = questionText,   // store the exact question text that was asked (rule #6)
            AskedAt = utcNow,
        };
        Turns.Add(turn);
        if (type == TurnType.FollowUp) FollowUpCount++;
        return turn;
    }

    /// <summary>Stores the AI scoring once (Reporting only reads it) and finishes this question.</summary>
    public void RecordAiEvaluation(IEnumerable<CriterionScore> scores, string feedback, DateTime utcNow)
    {
        foreach (var s in scores) CriterionScores.Add(s);
        AiSuggestedScore = CriterionScores.Sum(s => s.Points);
        AiFeedback = feedback;
        EvaluatedAt = utcNow;
        AiEvaluationState = AiEvaluationState.Completed;
        AiEvaluationError = null;
        IsFinished = true;
    }
}
