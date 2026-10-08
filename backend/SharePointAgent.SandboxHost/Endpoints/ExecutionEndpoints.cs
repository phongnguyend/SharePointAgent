namespace SharePointAgent.SandboxHost.Endpoints;

public static class ExecutionEndpoints
{
    public static void MapExecutionEndpoints(this IEndpointRouteBuilder routes)
    {
        // Which interpreters this image has, with versions, so tool descriptions can state them.
        routes.MapGet("/runtimes", async (ScriptRunner runner) => Results.Ok(await runner.GetRuntimesAsync()));

        // Runs synchronously and returns when the script exits or times out. A non-zero exit code is a
        // 200 with that exit code; only a request the host could not attempt is an error status.
        routes.MapPost("/executions", async (ScriptRunner runner, ExecutionRequest request, CancellationToken ct) =>
            Results.Ok(await runner.RunAsync(request, ct)));
    }
}
