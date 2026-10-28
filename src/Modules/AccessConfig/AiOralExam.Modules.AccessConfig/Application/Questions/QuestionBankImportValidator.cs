using AiOralExam.SharedKernel.Errors;

namespace AiOralExam.Modules.AccessConfig.Application.Questions;

internal static class QuestionBankImportValidator
{
    private static readonly HashSet<string> BloomLevels = new(StringComparer.Ordinal)
    {
        "Remember",
        "Understand",
        "Apply",
        "Analyze"
    };

    public static void Validate(QuestionBankImportRequest? request)
    {
        var errors = new Dictionary<string, List<string>>();
        if (request?.Items is not { Count: > 0 } items)
        {
            AddError(errors, "items", "At least one item is required.");
            ThrowIfInvalid(errors);
            return;
        }

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var prefix = $"items[{index}]";
            if (item is null)
            {
                AddError(errors, prefix, "Item cannot be null.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.Content))
                AddError(errors, $"{prefix}.content", "Content is required.");
            if (string.IsNullOrWhiteSpace(item.Topic))
                AddError(errors, $"{prefix}.topic", "Topic is required.");
            if (string.IsNullOrWhiteSpace(item.BloomLevel))
                AddError(errors, $"{prefix}.bloomLevel", "Bloom level is required.");
            else if (!BloomLevels.Contains(item.BloomLevel))
                AddError(errors, $"{prefix}.bloomLevel",
                    "Bloom level must be Remember, Understand, Apply, or Analyze.");
        }

        ThrowIfInvalid(errors);
    }

    private static void AddError(IDictionary<string, List<string>> errors, string key, string message)
    {
        if (!errors.TryGetValue(key, out var messages))
        {
            messages = [];
            errors.Add(key, messages);
        }

        messages.Add(message);
    }

    private static void ThrowIfInvalid(IDictionary<string, List<string>> errors)
    {
        if (errors.Count == 0)
            return;

        throw new ValidationException(errors.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.ToArray()));
    }
}
