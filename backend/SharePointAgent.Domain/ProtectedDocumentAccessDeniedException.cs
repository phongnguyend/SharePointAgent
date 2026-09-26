namespace SharePointAgent.Domain;

/// <summary>The application's identity cannot extract content from a protected document.</summary>
public sealed class ProtectedDocumentAccessDeniedException(Exception? innerException = null)
    : Exception("The application cannot decrypt this protected document. Its SharePoint app identity needs Azure Rights Management authorization and EXTRACT rights. SharePoint download permission alone is insufficient.", innerException);
