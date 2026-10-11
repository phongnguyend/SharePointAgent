namespace SharePointAgent.Application;

public sealed record JevUsageContext(Guid? UserId = null, Guid? ConversationId = null, Guid? QuestionId = null);
