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
            if (provider is not ("jev" or "microsoft-decision" or "ollaya" or "winow-native" or "laya-native"))
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
                parsedQuestions = provider is "winow-native" or "laya-native"
                    ? questions
                    : provider == "ollaya"
                    ? questions.Deserialize<Dictionary<string, OllayaQuestion>>(Json)!
                    : provider == "jev"
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
                if (provider == "ollaya" && (state.ValueKind != JsonValueKind.String ||
                    ((Dictionary<string, OllayaQuestion>)parsedQuestions).Values.Any(question => question is null ||
                        question.Type is not ("choice" or "score" or "noul") || string.IsNullOrWhiteSpace(question.Instructions))))
                {
                    return Results.BadRequest(new { error = "Ollaya requires a text state and questions with type choice, score, or noul and instructions." });
                }
                object result = provider == "winow-native"
                    ? await context.RequestServices.GetRequiredService<WinnowNativeClient>().EvaluateAsync(input, cancellationToken)
                    : provider == "laya-native"
                    ? await context.RequestServices.GetRequiredService<LayaNativeClient>().EvaluateAsync(input, cancellationToken)
                    : provider == "ollaya"
                    ? await context.RequestServices.GetRequiredService<OllayaClient>().DecideAsync(state.GetString()!,
                        (Dictionary<string, OllayaQuestion>)parsedQuestions, cancellationToken)
                    : provider == "jev"
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
        if (provider is "winow-native" or "laya-native")
        {
            var section = provider == "winow-native" ? "WinowNative" : "LayaNative";
            return Results.Json(new { error = $"{section} configuration is invalid. Check {section}:Endpoint (server base URL), ApiKey, Model, and TimeoutSeconds (1–3600) in the API host configuration." }, statusCode: 502);
        }
        if (provider == "ollaya")
        {
            return Results.Json(new { error = "Ollaya configuration is invalid. Check Ollaya:Endpoint (server base URL), ApiKey, Model, DecidePath, and TimeoutSeconds in the API host configuration." }, statusCode: 502);
        }
        var error = provider == "microsoft-decision"
            ? "Microsoft Decision configuration is invalid. Set MicrosoftDecision:Endpoint to the full HTTPS API URL (https://RESOURCE.services.ai.azure.com/providers/microsoft/v1/systemone). Set Model to the deployment name and TimeoutSeconds to 1–3600."
            : "Jev configuration is invalid. Check Jev:Endpoint (full HTTPS evaluation URL, such as https://api.typesafe.ai/v1/systemone), ApiKey, Model, and TimeoutSeconds (1–3600) in the API host configuration.";
        return Results.Json(new { error }, statusCode: 502);
    }
}
