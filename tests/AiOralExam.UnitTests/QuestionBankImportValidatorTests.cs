using AiOralExam.Modules.AccessConfig.Application.Questions;
using AiOralExam.SharedKernel.Errors;

namespace AiOralExam.UnitTests;

public class QuestionBankImportValidatorTests
{
    [Theory]
    [InlineData("Remember")]
    [InlineData("Understand")]
    [InlineData("Apply")]
    [InlineData("Analyze")]
    public void Validate_accepts_supported_bloom_levels(string bloomLevel)
    {
        var request = new QuestionBankImportRequest(
            [new QuestionBankImportItemRequest("Question", "Topic", bloomLevel)]);

        QuestionBankImportValidator.Validate(request);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("remember")]
    [InlineData("Create")]
    public void Validate_rejects_missing_or_unsupported_bloom_level(string? bloomLevel)
    {
        var request = new QuestionBankImportRequest(
            [new QuestionBankImportItemRequest("Question", "Topic", bloomLevel)]);

        var exception = Assert.Throws<ValidationException>(() => QuestionBankImportValidator.Validate(request));

        Assert.Contains("items[0].bloomLevel", exception.Details!.Keys);
    }

    [Fact]
    public void Validate_reports_required_fields_by_item_and_field()
    {
        var request = new QuestionBankImportRequest(
        [
            new QuestionBankImportItemRequest(" ", null, null),
            null
        ]);

        var exception = Assert.Throws<ValidationException>(() => QuestionBankImportValidator.Validate(request));

        Assert.Contains("items[0].content", exception.Details!.Keys);
        Assert.Contains("items[0].topic", exception.Details.Keys);
        Assert.Contains("items[0].bloomLevel", exception.Details.Keys);
        Assert.Contains("items[1]", exception.Details.Keys);
    }

    [Fact]
    public void Validate_rejects_null_or_empty_batch()
    {
        var nullException = Assert.Throws<ValidationException>(
            () => QuestionBankImportValidator.Validate(null));
        var emptyException = Assert.Throws<ValidationException>(
            () => QuestionBankImportValidator.Validate(new QuestionBankImportRequest([])));

        Assert.Contains("items", nullException.Details!.Keys);
        Assert.Contains("items", emptyException.Details!.Keys);
    }
}
