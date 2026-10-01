namespace SharePointAgent.Application;

public sealed class SigningOptions
{
    public const string SectionName = "Signing";

    public string ReturnUrl { get; set; } = "";

    public DocuSignOptions DocuSign { get; set; } = new();

    public AdobeSignOptions AdobeSign { get; set; } = new();
}

public sealed class DocuSignOptions
{
    public bool Enabled { get; set; }

    public bool Demo { get; set; } = true;

    public string ApiBaseUrl { get; set; } = "https://demo.docusign.net/restapi/v2.1/";

    public string AccountId { get; set; } = "";

    public string ClientId { get; set; } = "";

    public string SenderUserId { get; set; } = "";

    public string PrivateKeyPem { get; set; } = "";
}

public sealed class AdobeSignOptions
{
    public bool Enabled { get; set; }

    public string ApiAccessPoint { get; set; } = "";

    public string ClientId { get; set; } = "";

    public string ClientSecret { get; set; } = "";

    public string RefreshToken { get; set; } = "";
}
