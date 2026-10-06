using AiOralExam.Modules.Interview.Contracts;

namespace AiOralExam.Modules.Interview.Domain;

/// <summary>ERD: QUESTION RESPONSE - cau tra loi cho MOT cau hoi chinh (gom nhieu turn: chinh + xoay).</summary>
public class QuestionResponse
{
    public Guid Id { get; set; }
    public Guid AttemptId { get; set; }
    public Guid QuestionId { get; set; }   // FK sang questions (module F7)
    public int FollowUpCount { get; set; }
    public bool IsFinished { get; set; }

    public ICollection<InterviewTurn> Turns { get; set; } = new List<InterviewTurn>();
    public AiEvaluation? Evaluation { get; set; }

    /// <summary>Turn dang cho tra loi = turn cuoi cung neu chua co AnsweredAt.</summary>
    public InterviewTurn? CurrentTurn
    {
        get
        {
            var last = Turns.MaxBy(t => t.TurnNo);
            return last is { AnsweredAt: null } ? last : null;
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
            QuestionText = questionText,   // luu nguyen van cau da hoi (quy tac #6)
            AskedAt = utcNow,
        };
        Turns.Add(turn);
        if (type == TurnType.FollowUp) FollowUpCount++;
        return turn;
    }
}

/// <summary>ERD: INTERVIEW TURN - mot luot hoi/dap. Tap cac turn chinh la transcript.</summary>
public class InterviewTurn
{
    public Guid Id { get; set; }
    public Guid QuestionResponseId { get; set; }
    public int TurnNo { get; set; }
    public TurnType Type { get; set; }
    public string QuestionText { get; set; } = null!;
    public string? AnswerTranscript { get; set; }
    public string? AudioUrl { get; set; }
    public DateTime AskedAt { get; set; }
    public DateTime? AnsweredAt { get; set; }
}

/// <summary>ERD: AI EVALUATION - diem AI GOI Y cho 1 question response. Luu 1 lan, Reporting chi doc.</summary>
public class AiEvaluation
{
    public Guid Id { get; set; }
    public Guid QuestionResponseId { get; set; }
    public decimal SuggestedScore { get; set; }
    public string Feedback { get; set; } = null!;
    public DateTime EvaluatedAt { get; set; }
}

/// <summary>ERD: SCORE REVIEW - diem CUOI do giang vien chot cho ca luot thi.</summary>
public class ScoreReview
{
    public Guid Id { get; set; }
    public Guid AttemptId { get; set; }
    public Guid ReviewerId { get; set; }
    public decimal FinalScore { get; set; }
    public string? Comment { get; set; }
    public ReviewStatus Status { get; set; } = ReviewStatus.Pending;
    public DateTime? ReviewedAt { get; set; }
}
