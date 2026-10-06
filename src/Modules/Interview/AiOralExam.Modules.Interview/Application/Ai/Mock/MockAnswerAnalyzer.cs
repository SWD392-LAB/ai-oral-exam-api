namespace AiOralExam.Modules.Interview.Application.Ai.Mock;

/// <summary>
/// Mock phan tich cau tra loi: co dinh, chay lai van ra cung ket qua.
/// Cau tra loi duoi MinWords tu (hoac bo trong) => chua du y, hoi xoay neu con luot.
/// Bat/tat bang cau hinh Interview:Mock:EnableFollowUps (M1 mac dinh tat).
/// </summary>
internal sealed class MockAnswerAnalyzer(bool enableFollowUps) : IAnswerAnalyzer
{
    private const int MinWords = 15;

    public Task<AnswerAnalysis> AnalyzeAsync(AnswerAnalysisRequest request, CancellationToken ct = default)
    {
        var lastAnswer = request.Dialogue.LastOrDefault()?.Answer;
        var words = MockText.CountWords(lastAnswer);
        var sufficient = words >= MinWords;

        var needsFollowUp = enableFollowUps && !sufficient && request.FollowUpsUsed < request.MaxFollowUps;
        var followUp = needsFollowUp
            ? (string.IsNullOrWhiteSpace(lastAnswer)
                ? "Em chưa trả lời câu hỏi. Em có thể trình bày ngắn gọn ý chính của mình không?"
                : "Em có thể giải thích rõ hơn và cho một ví dụ cụ thể không?")
            : null;

        IReadOnlyList<string> missing = sufficient ? [] : ["Câu trả lời còn ngắn, thiếu giải thích hoặc ví dụ."];
        return Task.FromResult(new AnswerAnalysis(sufficient, missing, needsFollowUp, followUp));
    }
}
