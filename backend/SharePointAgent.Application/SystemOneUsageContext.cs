namespace SharePointAgent.Application;

public sealed record SystemOneUsageContext(Guid? UserId = null, Guid? ConversationId = null, Guid? QuestionId = null);
