using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SharePointAgent.SandboxHost.Endpoints;

namespace SharePointAgent.SandboxHost;

public static class SandboxHost
{
    public const string ApiKeyHeader = "X-Api-Key";

    public static WebApplicationBuilder AddSandboxHost(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<SandboxHostOptions>()
            .Bind(builder.Configuration.GetSection(SandboxHostOptions.SectionName))
            .Validate(options => !options.RequireApiKey || !string.IsNullOrWhiteSpace(options.ApiKey),
                "Sandbox:ApiKey is required because Sandbox:RequireApiKey is true.")
            .Validate(options => options.DefaultTimeoutSeconds >= 1 && options.DefaultTimeoutSeconds <= options.MaxTimeoutSeconds,
                "Sandbox:DefaultTimeoutSeconds must be between 1 and Sandbox:MaxTimeoutSeconds.")
            .ValidateOnStart();
        builder.Services.AddSingleton<SandboxWorkspace>();
        builder.Services.AddSingleton<ScriptRunner>();
        return builder;
    }

    public static WebApplication MapSandboxHost(this WebApplication app)
    {
        // Checked in middleware rather than an endpoint filter so a caller without the key is turned away
        // before the server reads a 100 MB upload body.
        app.Use(async (context, next) =>
        {
            var key = context.RequestServices.GetRequiredService<IOptions<SandboxHostOptions>>().Value.ApiKey;
            if (!string.IsNullOrEmpty(key) && context.Request.Path != "/health" && !KeyMatches(context.Request.Headers[ApiKeyHeader].ToString(), key))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = $"A valid {ApiKeyHeader} header is required." });
                return;
            }

            await next(context);
        });

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        var api = app.MapGroup("");

        api.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try
            {
                return await next(context);
            }
            catch (BadHttpRequestException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (IOException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (InvalidDataException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        api.MapFileEndpoints();

        api.MapExecutionEndpoints();

        return app;
    }

    private static bool KeyMatches(string supplied, string expected)
    {
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
    }
}
