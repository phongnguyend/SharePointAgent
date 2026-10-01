using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharePointAgent.Application;

namespace SharePointAgent.Infrastructure.Signing;

public static class DependencyInjection
{
    public static IServiceCollection AddSigningServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMemoryCache();
        services.AddOptions<SigningOptions>().Bind(configuration.GetSection(SigningOptions.SectionName))
            .Validate(o => !o.DocuSign.Enabled || (Guid.TryParse(o.DocuSign.AccountId, out _) && Guid.TryParse(o.DocuSign.ClientId, out _)
                && Guid.TryParse(o.DocuSign.SenderUserId, out _) && !string.IsNullOrWhiteSpace(o.DocuSign.PrivateKeyPem)
                && ProviderUrl(o.DocuSign.ApiBaseUrl, "docusign.net") && ReturnUrl(o.ReturnUrl)), "Configure the DocuSign shared sender, RSA key, API base URL, and Signing:ReturnUrl.")
            .Validate(o => !o.AdobeSign.Enabled || (ProviderUrl(o.AdobeSign.ApiAccessPoint, "adobesign.com")
                && !string.IsNullOrWhiteSpace(o.AdobeSign.ClientId) && !string.IsNullOrWhiteSpace(o.AdobeSign.ClientSecret)
                && !string.IsNullOrWhiteSpace(o.AdobeSign.RefreshToken)), "Configure the AdobeSign shared sender OAuth credentials and API access point.")
            .ValidateOnStart();
        services.AddHttpClient<DocuSignService>(client => client.Timeout = TimeSpan.FromMinutes(2))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient<AdobeSignService>(client => client.Timeout = TimeSpan.FromMinutes(2))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddTransient<ISignatureProvider>(sp => sp.GetRequiredService<DocuSignService>());
        services.AddTransient<ISignatureProvider>(sp => sp.GetRequiredService<AdobeSignService>());
        services.AddTransient<SignatureRequestService>();
        return services;
    }

    private static bool ProviderUrl(string value, string domain) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    private static bool ReturnUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback));
}
