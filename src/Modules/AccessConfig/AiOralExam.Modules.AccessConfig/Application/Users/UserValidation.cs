using System.Collections.Generic;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.SharedKernel.Errors;
using ValidationException = AiOralExam.SharedKernel.Errors.ValidationException;

namespace AiOralExam.Modules.AccessConfig.Application.Users;

internal static class UserValidation
{
    internal static string? Clean(string? x)
        => string.IsNullOrWhiteSpace(x) ? null : x.Trim();

    internal static void ValidateCodes(UserRole role, string? s, string? l)
    {
        if (!Enum.IsDefined(role))
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["role"] = ["A valid user role is required."]
            });
        if (role == UserRole.Student && string.IsNullOrWhiteSpace(s))
            throw new ValidationException(new Dictionary<string, string[]>
            {
                {
                    "studentCode", ["Student requires a student code."]
                }
            });
        if (role == UserRole.Lecturer && string.IsNullOrWhiteSpace(l))
            throw new ValidationException(new Dictionary<string, string[]>
            {
                {
                    "lecturerCode", ["Lecturer requires a lecturer code."]
                }
            });
        if (role != UserRole.Student && s is not null)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                {
                    "studentCode", ["Only students can have a student code."]
                }
            });
        if (role != UserRole.Lecturer && l is not null)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                {
                    "lecturerCode", ["Only lecturers can have a lecturer code."]
                }
            });
    }
}
