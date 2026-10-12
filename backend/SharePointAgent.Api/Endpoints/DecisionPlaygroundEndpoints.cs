using System.Diagnostics;
using System.Text.Json;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;

namespace SharePointAgent.Api;

public static class DecisionPlaygroundEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowOutOfOrderMetadataProperties = true,
        RespectRequiredConstructorParameters = true
    };

    public static void MapDecisionPlaygroundEndpoints(this WebApplication app)
    {
        app.MapPost("/api/admin/decision-playground/{provider}", async (string provider, JsonElement input,
            HttpContext context, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.AppUser().Roles.Contains(AppRoles.GlobalAdmin))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            if (provider is not ("jev" or "microsoft-decision"))
            {
                return Results.NotFound();
            }
            if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("state", out var state) ||
                !input.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Object ||
                input.GetRawText().Length > 100_000)
            {
                return Results.BadRequest(new { error = "Provide state and a questions object, within 100,000 characters." });
            }
            object parsedQuestions;
            try
            {
                parsedQuestions = provider == "jev"
                    ? questions.Deserialize<Dictionary<string, JevQuestion>>(Json)!
                    : questions.Deserialize<Dictionary<string, MicrosoftDecisionQuestion>>(Json)!;
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Invalid questions. Use choice, score, or noul with instructions and appropriate criteria." });
            }
            var timer = Stopwatch.StartNew();
            try
            {
                object result = provider == "jev"
                    ? await context.RequestServices.GetRequiredService<JevClient>().EvaluateAsync(state, (Dictionary<string, JevQuestion>)parsedQuestions,
                        new SystemOneUsageContext(UserId: context.AppUser().Id), cancellationToken)
                    : await context.RequestServices.GetRequiredService<MicrosoftDecisionClient>().EvaluateAsync(state, (Dictionary<string, MicrosoftDecisionQuestion>)parsedQuestions,
                        new SystemOneUsageContext(UserId: context.AppUser().Id), cancellationToken);
                return Results.Json(new { result, elapsedMs = timer.ElapsedMilliseconds, traceId = Activity.Current?.TraceId.ToString() }, Json);
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(new { error = "Invalid state or questions. Check instructions and criteria for each question." });
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Results.Json(new { error = "The decision request timed out." }, statusCode: 504);
            }
            catch (Microsoft.Extensions.Options.OptionsValidationException)
            {
                return ConfigurationError(provider);
            }
            catch (InvalidOperationException)
            {
                return ConfigurationError(provider);
            }
            catch (Exception ex) when (ex is Azure.Identity.AuthenticationFailedException or Azure.Identity.CredentialUnavailableException)
            {
                return Results.Json(new { error = "Entra authentication failed. Check the API host's identity and Foundry access, or configure MicrosoftDecision:ApiKey." }, statusCode: 502);
            }
            catch (HttpRequestException ex)
            {
                var error = ex.StatusCode is { } status
                    ? $"The decision provider returned HTTP {(int)status}. Compare the API host's endpoint, deployment name, and authentication with your working request."
                    : "Could not connect to the decision provider. Check the API host's network access and endpoint.";
                return Results.Json(new { error }, statusCode: 502);
            }
            catch (JsonException)
            {
                return Results.Json(new { error = "The provider responded, but its response did not match the expected decision schema. Compare the successful response JSON with the client result types." }, statusCode: 502);
            }
        });
    }

    private static IResult ConfigurationError(string provider)
    {
        var error = provider == "microsoft-decision"
            ? "Microsoft Decision configuration is invalid. Set MicrosoftDecision:Endpoint to the full HTTPS API URL (https://RESOURCE.services.ai.azure.com/providers/microsoft/v1/systemone). Set Model to the deployment name and TimeoutSeconds to 1–3600."
            : "Jev configuration is invalid. Check Jev:Endpoint (full HTTPS evaluation URL, such as https://api.typesafe.ai/v1/systemone), ApiKey, Model, and TimeoutSeconds (1–3600) in the API host configuration.";
        return Results.Json(new { error }, statusCode: 502);
    }
}
