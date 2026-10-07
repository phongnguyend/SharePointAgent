using SharePointAgent.Infrastructure;
using SharePointAgent.Infrastructure.Monitoring;
using SharePointAgent.Infrastructure.DocumentSigning;
using SharePointAgent.Api;

const string FrontendCorsPolicy = "frontend";

var builder = WebApplication.CreateBuilder(args);
builder.AddApplicationTelemetry("sharepointagent-api");
builder.Services.AddEntraAuthentication(builder.Configuration);
builder.Services.AddWebhookServices(builder.Configuration);
builder.Services.AddSearchQueryServices(builder.Configuration);
builder.Services.AddIndexStateServices(builder.Configuration);
builder.Services.AddChatServices(builder.Configuration);
builder.Services.AddAttachmentFileServices(builder.Configuration);
builder.Services.AddDocumentSigningServices(builder.Configuration);
builder.Services.AddIndexedFileReindexServices(builder.Configuration);
builder.Services.AddAppIdentity();
builder.Services.AddSingleton<BackgroundHealthMonitor>();
builder.Services.AddHttpClient<ServiceHealthMonitor>(client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
    client.MaxResponseContentBufferSize = 64 * 1024;
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<SitePermissionService>(client => client.Timeout = TimeSpan.FromSeconds(90))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

// The viewer front end is served from its own origin during development. Origins are configured rather
// than wildcarded. Browser requests carry Entra access tokens.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:5173"];
builder.Services.AddCors(options => options.AddPolicy(FrontendCorsPolicy, policy => policy
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

app.UseCors(FrontendCorsPolicy);
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<AppIdentityMiddleware>();
app.MapAppUsers();
app.MapEmbeddingUsage();
app.MapTokenUsage();
app.MapContentSafetyUsage();
app.MapImageDescriptionUsage();
app.MapTranscriptionUsage();
app.MapSystemEndpoints();
app.MapSharePointWebhookEndpoints();
app.MapSearchEndpoints();
app.MapIndexedFilesEndpoints();
app.MapDeltaStateEndpoints();
app.MapAttachmentFilesEndpoints();
app.MapSignatureEndpoints();
app.MapSigningTemplateEndpoints();
app.MapSigningAuthorizationEndpoints();
app.MapSitePermissionEndpoints();
app.MapSubscriptionsEndpoints();
app.MapBrowseEndpoints();
app.MapAgentsEndpoints();
app.MapChatWorkspacesEndpoints();
app.MapChatEndpoints();
app.MapChatTranscriptionEndpoints();
app.MapSandboxFileEndpoints();

app.Run();

public partial class Program;
