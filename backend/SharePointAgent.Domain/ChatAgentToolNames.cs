namespace SharePointAgent.Domain;

/// <summary>
/// The names the chat agent's tools are published under. The agent instructions, tool descriptions, and
/// error messages refer to tools by name, so every reference uses these constants and a rename cannot leave
/// a stale name behind.
/// </summary>
public static class ChatAgentToolNames
{
    public const string SearchSharePointDocuments = "search_sharepoint_documents";

    public const string FindRelatedDocuments = "find_related_documents";

    public const string SearchAttachments = "search_attachments";

    public const string DownloadAttachment = "download_attachment";

    public const string DescribeImage = "describe_image";

    public const string RecognizeText = "recognize_text";

    public const string ConvertToMarkdown = "convert_to_markdown";

    public const string ReadText = "read_text";

    public const string GetDocumentOutline = "get_document_outline";

    public const string ListFiles = "list_files";

    public const string WriteTextFile = "write_text_file";

    public const string CreateDirectory = "create_directory";

    public const string MoveFile = "move_file";

    public const string CopyFile = "copy_file";

    public const string DeleteFile = "delete_file";

    public const string ZipFiles = "zip_files";

    public const string UnzipFile = "unzip_file";

    /// <summary>Offered only when the working directory is isolated; never on the host's own disk.</summary>
    public const string ExecuteScript = "execute_script";

    public const string DownloadSharePointFile = "download_sharepoint_file";

    public const string UploadSharePointFile = "upload_sharepoint_file";
}
