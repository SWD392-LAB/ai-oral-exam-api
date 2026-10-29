namespace AiOralExam.Modules.Interview.Application;

public sealed record RecoverInterruptedTurnRequest(Guid TurnId);

public sealed record RecoverInterruptedTurnResponse(SubmitAnswerResponse Result);
