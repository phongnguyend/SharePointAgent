# Jev decision client

`JevClient` implements the [TypeSafe HTTP API](https://docs.typesafe.ai/api).
It is registered in API, Background, and AgentHost. It is an optional backend
client; it is not automatically invoked by chat or indexing.

## Configuration

Each host has a `Jev` configuration section. Set `Jev:ApiKey` through that host's
user secrets, or `Jev__ApiKey` in its environment. Keep this provider key out of
frontend configuration. For dev/test deployments, set the GitHub environment secret `JEV_API_KEY` and run the API, Background, and AgentHost releases. Container Apps use a secret reference; AgentHost uses the Foundry `agent-secrets` connection. The key is optional; an absent key clears the deployed `Jev__ApiKey` setting on the next release. See [deployment configuration](../infra/README.md#configure-the-jev-api-key).

```json
{
  "Jev": {
    "Endpoint": "https://api.typesafe.ai/v1",
    "ApiKey": null,
    "Model": "jev-latest",
    "TimeoutSeconds": 60
  }
}
```

The endpoint includes `/v1`. Use a versioned model ID when reproducible model
selection matters; `jev-latest` is an alias. The response includes the actual
model ID. See [available models](https://docs.typesafe.ai/models).

## Usage

Inject `SharePointAgent.Infrastructure.JevClient` into the consuming service:

```csharp
var result = await jevClient.EvaluateAsync(
    new { message = "Please find the onboarding policy." },
    new Dictionary<string, JevQuestion>
    {
        ["route"] = new JevChoiceQuestion("Which handler should process this request?",
            new Dictionary<string, object?>
            {
                ["search"] = "Find documents",
                ["chat"] = "General conversation"
            }),
        ["relevance"] = new JevScoreQuestion("How relevant is this to company policies?",
            ["Unrelated", "Related", "Directly related"]),
        ["urgent"] = new JevNoulQuestion("Does this require immediate attention?")
    }, cancellationToken);

var route = (JevChoiceAnswer)result.Answers["route"];
var score = (JevScoreAnswer)result.Answers["relevance"];
var urgent = (JevNoulAnswer)result.Answers["urgent"];
var inputTokens = result.Usage.InputTokens;
var outputTokens = result.Usage.OutputTokens;
var models = await jevClient.ListModelsAsync(cancellationToken);
```

State and instructions accept text or JSON-serializable objects/arrays. Questions
are keyed by caller-selected IDs. Choice and Score expose confidence and
probability distributions; Noul exposes a probability rather than a Boolean.
The caller decides thresholds and actions. A Score can fall between rubric levels.

Calls honor cancellation and the configured HTTP timeout. Invalid inputs fail
before sending. HTTP failures throw `HttpRequestException` with `StatusCode`;
invalid response JSON or missing/mismatched answers throw `JsonException`.
Upstream error bodies are omitted to avoid exposing submitted content.
There are no automatic retries; callers can handle rate limits (429) and overload
(529) with bounded backoff. Token usage is returned, not persisted in usage reports.

To register the client in another host, call
`services.AddJevClient(configuration)`. No provider SDK package is required.
