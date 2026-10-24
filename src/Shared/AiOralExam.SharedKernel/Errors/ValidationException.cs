namespace AiOralExam.SharedKernel.Errors;

public sealed class ValidationException(IDictionary<string, string[]> errors, string message = "The submitted data is invalid.")
    : AppException(400, "validation_failed", message, errors);
