namespace AiOralExam.Modules.Interview.Application.Ai.Mock;

internal static class MockText
{
    public static int CountWords(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
