namespace AiOralExam.Modules.Interview.Application.Ai.Mock;

/// <summary>
/// Mock cham diem (BE-AI-04): diem tinh tu so tu cua tat ca cau tra loi, lam tron 0.5.
/// Co dinh: cung dau vao => cung diem + nhan xet.
/// </summary>
internal sealed class MockRubricScorer : IRubricScorer
{
    private const int WordsForFullScore = 60;

    public Task<RubricScore> ScoreAsync(RubricScoringRequest request, CancellationToken ct = default)
    {
        var words = request.Dialogue.Sum(t => MockText.CountWords(t.Answer));
        var ratio = Math.Min(1m, (decimal)words / WordsForFullScore);
        var score = Math.Round(request.MaxScore * ratio * 2, MidpointRounding.AwayFromZero) / 2;

        var feedback = ratio switch
        {
            0m => "[Mock AI] Sinh viên không trả lời câu hỏi.",
            < 0.4m => "[Mock AI] Câu trả lời quá ngắn, chưa đáp ứng các tiêu chí của rubric.",
            < 0.8m => "[Mock AI] Trả lời được một phần ý chính, cần giải thích sâu hơn và có ví dụ.",
            _ => "[Mock AI] Trả lời đầy đủ, có giải thích và ví dụ phù hợp với rubric."
        };

        return Task.FromResult(new RubricScore(score, feedback));
    }
}

internal static class MockText
{
    public static int CountWords(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
