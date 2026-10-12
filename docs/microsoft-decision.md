# Microsoft Decision client

`MicrosoftDecisionClient` calls the Foundry decision API with typed `choice`, `score`,
and `noul` questions. It is registered in API, Background, and AgentHost, independently
of Jev. Inject the client into a service to use it; existing chat routing is unchanged.

## Configuration

Deploy Microsoft-Decision-1 in Foundry, then configure the host that calls it:

```json
"MicrosoftDecision": {
  "Endpoint": "https://YOUR-RESOURCE.services.ai.azure.com/providers/microsoft/v1/systemone",
  "Model": "YOUR-DEPLOYMENT-NAME",
  "ApiKey": null,
  "TimeoutSeconds": 60
}
```

Use the full decision API URL, including `/providers/microsoft/v1/systemone`.
The client sends to this URL directly, preserving any query string and appending nothing. `Model` is the deployment name;
the returned `Model` identifies the underlying model and can differ.

With no API key, the client uses `DefaultAzureCredential` (or an injected
`TokenCredential`) with scope `https://cognitiveservices.azure.com/.default`.
Grant that identity inference access to the Foundry resource. Alternatively, set
`MicrosoftDecision:ApiKey` in user secrets or `MicrosoftDecision__ApiKey` in the host's
secret-backed environment. API-key authentication uses the `api-key` header.
Other environment names are `MicrosoftDecision__Endpoint`, `MicrosoftDecision__Model`,
and `MicrosoftDecision__TimeoutSeconds`. An empty endpoint leaves the integration
unconfigured and causes calls to fail before sending a request.

## Example

```csharp
var result = await client.EvaluateAsync(
    new { message = "Checkout is unavailable for every customer." },
    new Dictionary<string, MicrosoftDecisionQuestion>
    {
        ["team"] = new MicrosoftDecisionChoiceQuestion("Which team should handle this?",
            new Dictionary<string, object?>
            {
                ["engineering"] = "Bugs and outages",
                ["billing"] = "Invoices and payments"
            }),
        ["severity"] = new MicrosoftDecisionScoreQuestion("How severe is this?",
            ["Minor", "Major", "Critical"]),
        ["urgent"] = new MicrosoftDecisionNoulQuestion("Does this need immediate attention?")
    },
    cancellationToken);

var team = ((MicrosoftDecisionChoiceAnswer)result.Answers["team"]).Choice;
var model = result.Model;
var inputTokens = result.Usage.InputTokens;
var outputTokens = result.Usage.OutputTokens;
```

Types are in `SharePointAgent.Infrastructure`; options are in
`SharePointAgent.Application`. Score criteria run from lowest to highest. Noul answers
are probabilities. The client returns usage to the caller; it does not persist usage
or add a Token usage UI tab. Requests honor cancellation and the configured timeout,
and upstream HTTP errors expose status codes without exposing response bodies.
Automatic retries and redirects are disabled to avoid duplicate evaluations and
forwarding credentials to another endpoint.

## Postman

Send `POST {{endpoint}}` with `Content-Type:
application/json` and either `api-key: {{apiKey}}` or `Authorization: Bearer {{token}}`.
Use a raw JSON body:

```json
{
  "model": "YOUR-DEPLOYMENT-NAME",
  "state": "Checkout is unavailable for every customer.",
  "questions": {
    "urgent": { "type": "noul", "instructions": "Does this need immediate attention?" }
  }
}
```

See [Microsoft's deployment and API documentation](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/how-to/use-foundry-models-microsoft-decision).
