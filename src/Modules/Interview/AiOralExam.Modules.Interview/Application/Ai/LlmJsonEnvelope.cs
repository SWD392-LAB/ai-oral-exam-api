namespace AiOralExam.Modules.Interview.Application.Ai;

internal static class LlmJsonEnvelope
{
    public static string RemoveMarkdownCodeFence(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("```") && trimmed.EndsWith("```"))
        {
            var firstLineEnd = trimmed.IndexOf('\n');
            return firstLineEnd < 0
                ? trimmed[3..^3].Trim()
                : trimmed[(firstLineEnd + 1)..^3].Trim();
        }

        return trimmed;
    }
}
