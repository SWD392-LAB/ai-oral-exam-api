namespace AiOralExam.Modules.AccessConfig.Domain;

// Enum member names MUST match the CHECK constraints in database/01_schema.sql
// (EF Core stores enums as strings; UserRole is the exception and is stored as the number = roles.id).

public enum UserRole : short
{
    Student = 1,
    Lecturer = 2,
    Administrator = 3
}

public enum AuthProvider
{
    Password,
    GoogleSSO
}

public enum TokenPurpose
{
    AccountSetup,
    PasswordReset
}

public enum AiServiceType
{
    STT,
    TTS,
    LLM
}

public enum SpeechLanguage
{
    Vietnamese,
    English
}
