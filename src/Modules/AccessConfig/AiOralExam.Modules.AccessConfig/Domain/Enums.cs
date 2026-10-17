namespace AiOralExam.Modules.AccessConfig.Domain;

// UserRole is an API selector by role name; it is not persisted as roles.id.
public enum UserRole
{
    Student,
    Lecturer,
    Administrator
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
