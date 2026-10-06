namespace AiOralExam.Modules.AccessConfig.Domain;

// Gia tri enum PHAI trung ten voi CHECK constraint trong database/01_schema.sql
// (EF Core luu enum dang string, rieng UserRole luu so = roles.id).

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
