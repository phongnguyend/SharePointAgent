using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SharePointAgent.SandboxHost;
using Xunit;

namespace SharePointAgent.Tests;

/// <summary>
/// The HTTP contract agent tools are written against. The same image serves a Dynamic Sessions pool
/// and a Container Apps sandbox, which differ only in the settings their deployments pass.
/// </summary>
public sealed class SandboxHostTests : IAsyncLifetime
{
    private const string ApiKey = "test-key";

    private WebApplication? _app;

    private string Workspace { get; } = Path.Combine(Path.GetTempPath(), $"sandbox-host-{Guid.NewGuid():N}");

    private HttpClient Client { get; set; } = null!;

    public async Task InitializeAsync()
    {
        _app = Build(Settings());
        await _app.StartAsync();
        Client = _app.GetTestClient();
        Client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        if (Directory.Exists(Workspace))
        {
            Directory.Delete(Workspace, true);
        }
    }

    [Fact]
    public async Task RefusesToStartWithoutAnApiKeyByDefault()
    {
        var settings = Settings();
        settings.Remove("Sandbox:ApiKey");
        await using var app = Build(settings);
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync());
        Assert.Contains("Sandbox:ApiKey", error.Message);
    }

    [Fact]
    public async Task ASessionPoolCanTurnTheKeyRequirementOff()
    {
        // A pool authenticates callers with Entra before forwarding, which is why its deployment sets this.
        var settings = Settings();
        settings.Remove("Sandbox:ApiKey");
        settings["Sandbox:RequireApiKey"] = "false";
        await using var app = Build(settings);
        await app.StartAsync();
        using var client = app.GetTestClient();
        Assert.True((await client.GetAsync("/files")).IsSuccessStatusCode);
    }

    [Fact]
    public void ScriptsDoNotInheritHostSettings()
    {
        var environment = new Dictionary<string, string?>
        {
            ["Sandbox__ApiKey"] = "secret",
            ["SANDBOX__WORKSPACEROOT"] = "/workspace",
            ["PATH"] = "/usr/bin",
        };
        ScriptRunner.ApplyEnvironment(environment, new Dictionary<string, string> { ["EXTRA"] = "1" });
        Assert.DoesNotContain(environment.Keys, key => key.StartsWith("Sandbox__", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("/usr/bin", environment["PATH"]);
        Assert.Equal("1", environment["EXTRA"]);
        Assert.Throws<ArgumentException>(() => ScriptRunner.ApplyEnvironment(environment, new Dictionary<string, string> { ["A=B"] = "1" }));
    }

    [Fact]
    public async Task HealthIsAnonymousButEveryOtherRouteNeedsTheKey()
    {
        using var anonymous = _app!.GetTestClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/files")).StatusCode);

        anonymous.DefaultRequestHeaders.Add("X-Api-Key", "wrong-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/executions", new { language = "python", code = "print(1)" })).StatusCode);
    }

    [Fact]
    public async Task TextIsWrittenAndReadBackByLineRange()
    {
        var write = await Client.PutAsJsonAsync("/files/text", new { path = "notes/today.txt", content = "one\ntwo\nthree\nfour" });
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        Assert.Equal("notes/today.txt", (await Json(write)).GetProperty("path").GetString());

        var read = await Json(await Client.GetAsync("/files/text?path=notes/today.txt&startLine=2&lineCount=2"));
        Assert.Equal("two\nthree", read.GetProperty("content").GetString());
        Assert.Equal(4, read.GetProperty("totalLines").GetInt32());

        await Client.PutAsJsonAsync("/files/text", new { path = "notes/today.txt", content = "\nfive", append = true });
        Assert.EndsWith("four\nfive", (await Json(await Client.GetAsync("/files/text?path=notes/today.txt"))).GetProperty("content").GetString());
    }

    [Fact]
    public async Task BinaryFilesRoundTripThroughTheContentRoute()
    {
        var bytes = new byte[] { 0, 1, 2, 255, 0, 42 };
        var upload = await Client.PutAsync("/files/content?path=data/blob.bin", new ByteArrayContent(bytes));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        Assert.Equal(bytes.Length, (await Json(upload)).GetProperty("size").GetInt64());

        Assert.Equal(bytes, await Client.GetByteArrayAsync("/files/content?path=data/blob.bin"));

        // Text reads refuse binary rather than return garbage.
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync("/files/text?path=data/blob.bin")).StatusCode);
    }

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("nested/../../escaped.txt")]
    public async Task PathsOutsideTheWorkspaceAreRefused(string path)
    {
        var response = await Client.PutAsJsonAsync("/files/text", new { path, content = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("outside the workspace", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnAbsolutePathIsAcceptedOnlyInsideTheWorkspace()
    {
        var inside = Path.Combine(Workspace, "inside.txt");
        Assert.Equal(HttpStatusCode.OK, (await Client.PutAsJsonAsync("/files/text", new { path = inside, content = "x" })).StatusCode);

        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.txt");
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PutAsJsonAsync("/files/text", new { path = outside, content = "x" })).StatusCode);
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public async Task MissingPathsAre404AndExistingOnesAreNotReplacedUnlessAsked()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync("/files/info?path=missing.txt")).StatusCode);

        await Client.PutAsJsonAsync("/files/text", new { path = "keep.txt", content = "original" });
        var refused = await Client.PutAsJsonAsync("/files/text", new { path = "keep.txt", content = "new", overwrite = false });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(Workspace, "keep.txt")));
    }

    [Fact]
    public async Task AnEditMustMatchExactlyOnceUnlessReplaceAllIsSet()
    {
        await Client.PutAsJsonAsync("/files/text", new { path = "app.py", content = "x = 1\nx = 1\ny = 2\n" });

        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync("/files/edit", new { path = "app.py", oldText = "x = 1", newText = "x = 3" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync("/files/edit", new { path = "app.py", oldText = "z = 9", newText = "z = 0" })).StatusCode);

        var unique = await Json(await Client.PostAsJsonAsync("/files/edit", new { path = "app.py", oldText = "y = 2", newText = "y = 5" }));
        Assert.Equal(1, unique.GetProperty("replacements").GetInt32());

        var all = await Json(await Client.PostAsJsonAsync("/files/edit", new { path = "app.py", oldText = "x = 1", newText = "x = 3", replaceAll = true }));
        Assert.Equal(2, all.GetProperty("replacements").GetInt32());
        Assert.Equal("x = 3\nx = 3\ny = 5\n", await File.ReadAllTextAsync(Path.Combine(Workspace, "app.py")));
    }

    [Fact]
    public async Task DirectoriesAreCreatedCopiedMovedAndDeleted()
    {
        Assert.Equal(HttpStatusCode.OK, (await Client.PostAsJsonAsync("/files/directories", new { path = "src/lib" })).StatusCode);
        await Client.PutAsJsonAsync("/files/text", new { path = "src/lib/a.txt", content = "a" });

        Assert.Equal(HttpStatusCode.OK, (await Client.PostAsJsonAsync("/files/copy", new { source = "src", destination = "backup/src" })).StatusCode);
        Assert.True(File.Exists(Path.Combine(Workspace, "backup", "src", "lib", "a.txt")));
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync("/files/copy", new { source = "src", destination = "src/inner" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Client.PostAsJsonAsync("/files/copy", new { source = "src", destination = "backup/src" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Client.PostAsJsonAsync("/files/move", new { source = "src/lib/a.txt", destination = "moved.txt" })).StatusCode);
        Assert.True(File.Exists(Path.Combine(Workspace, "moved.txt")));
        Assert.False(File.Exists(Path.Combine(Workspace, "src", "lib", "a.txt")));

        Assert.Equal(HttpStatusCode.Conflict, (await Client.DeleteAsync("/files?path=backup")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Client.DeleteAsync("/files?path=backup&recursive=true")).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(Workspace, "backup")));
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.DeleteAsync("/files?path=.&recursive=true")).StatusCode);
    }

    [Fact]
    public async Task ListingFiltersByPatternAndRecursesOnRequest()
    {
        await Client.PutAsJsonAsync("/files/text", new { path = "top.py", content = "" });
        await Client.PutAsJsonAsync("/files/text", new { path = "deep/inner.py", content = "" });
        await Client.PutAsJsonAsync("/files/text", new { path = "deep/readme.md", content = "" });

        var shallow = await Json(await Client.GetAsync("/files?pattern=*.py"));
        Assert.Equal(["top.py"], Paths(shallow));

        var deep = await Json(await Client.GetAsync("/files?pattern=*.py&recursive=true"));
        Assert.Equal(["deep/inner.py", "top.py"], Paths(deep));
    }

    [Fact]
    public async Task SearchReturnsMatchingLinesWithNumbers()
    {
        await Client.PutAsJsonAsync("/files/text", new { path = "a.cs", content = "class A\n{\n    // TODO: tidy\n}" });
        await Client.PutAsJsonAsync("/files/text", new { path = "b.txt", content = "todo list" });

        var plain = await Json(await Client.PostAsJsonAsync("/files/search", new { query = "todo", glob = "*.cs" }));
        var match = Assert.Single(plain.GetProperty("matches").EnumerateArray());
        Assert.Equal("a.cs", match.GetProperty("path").GetString());
        Assert.Equal(3, match.GetProperty("line").GetInt32());

        var regex = await Json(await Client.PostAsJsonAsync("/files/search", new { query = "^todo", isRegex = true, caseSensitive = true }));
        Assert.Equal("b.txt", Assert.Single(regex.GetProperty("matches").EnumerateArray()).GetProperty("path").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync("/files/search", new { query = "(", isRegex = true })).StatusCode);
    }

    [Fact]
    public async Task ZipAndUnzipRoundTrip()
    {
        await Client.PutAsJsonAsync("/files/text", new { path = "report/summary.md", content = "# Summary" });
        await Client.PutAsJsonAsync("/files/text", new { path = "report/data/values.csv", content = "a,b" });

        Assert.Equal(HttpStatusCode.OK, (await Client.PostAsJsonAsync("/files/zip", new { paths = new[] { "report" }, destination = "out/report.zip" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client.PostAsJsonAsync("/files/unzip", new { path = "out/report.zip", destination = "restored" })).StatusCode);
        Assert.Equal("a,b", await File.ReadAllTextAsync(Path.Combine(Workspace, "restored", "report", "data", "values.csv")));

        // A second extract would overwrite, so it is refused before any file is touched.
        Assert.Equal(HttpStatusCode.Conflict, (await Client.PostAsJsonAsync("/files/unzip", new { path = "out/report.zip", destination = "restored" })).StatusCode);
    }

    [Fact]
    public async Task AnArchiveEntryCannotEscapeTheDestination()
    {
        using (var buffer = new MemoryStream())
        {
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(archive.CreateEntry("../escaped.txt").Open());
                writer.Write("x");
            }

            await Client.PutAsync("/files/content?path=evil.zip", new ByteArrayContent(buffer.ToArray()));
        }

        var response = await Client.PostAsJsonAsync("/files/unzip", new { path = "evil.zip", destination = "target", overwrite = true });
        Assert.False(response.IsSuccessStatusCode);
        Assert.False(File.Exists(Path.Combine(Workspace, "escaped.txt")));
    }

    [Fact]
    public async Task PythonRunsWithArgumentsStdinAndEnvironment()
    {
        var result = await Execute(new
        {
            language = "python",
            code = "import os, sys\nprint(sys.argv[1], sys.stdin.read().strip(), os.environ['GREETING'])",
            arguments = new[] { "first arg" },
            stdin = "from stdin",
            environment = new Dictionary<string, string> { ["GREETING"] = "héllo" },
        });
        Assert.Equal(0, result.GetProperty("exitCode").GetInt32());
        Assert.Equal("first arg from stdin héllo", result.GetProperty("stdout").GetString()!.Trim());
    }

    [Fact]
    public async Task NodeRunsBothCommonJsAndModuleSyntax()
    {
        var commonJs = await Execute(new { language = "node", code = "const os = require('os'); console.log(typeof os.platform());" });
        Assert.Equal("string", commonJs.GetProperty("stdout").GetString()!.Trim());

        var module = await Execute(new { language = "javascript", code = "import { platform } from 'node:os';\nconsole.log(typeof platform());" });
        Assert.Equal("string", module.GetProperty("stdout").GetString()!.Trim());
    }

    [Fact]
    public async Task PowerShellRunsNonInteractively()
    {
        var result = await Execute(new { language = "pwsh", code = "param($Name) Write-Output \"Hello $Name\"", arguments = new[] { "sandbox" } });
        Assert.Equal(0, result.GetProperty("exitCode").GetInt32());
        Assert.Equal("Hello sandbox", result.GetProperty("stdout").GetString()!.Trim());
    }

    [Fact]
    public async Task AFailingScriptIsStillASuccessfulRequest()
    {
        var result = await Execute(new { language = "python", code = "import sys\nprint('bad input', file=sys.stderr)\nsys.exit(3)" });
        Assert.Equal(3, result.GetProperty("exitCode").GetInt32());
        Assert.Contains("bad input", result.GetProperty("stderr").GetString());
    }

    [Fact]
    public async Task ATimedOutScriptIsKilled()
    {
        var result = await Execute(new { language = "python", code = "import time\nprint('started', flush=True)\ntime.sleep(60)", timeoutSeconds = 2 });
        Assert.True(result.GetProperty("timedOut").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("exitCode").ValueKind);
        Assert.Contains("started", result.GetProperty("stdout").GetString());
        Assert.True(result.GetProperty("durationMs").GetInt64() < 30_000);
    }

    [Fact]
    public async Task OutputPastTheLimitIsTruncated()
    {
        var result = await Execute(new { language = "python", code = "print('x' * 5000)" });
        Assert.True(result.GetProperty("stdoutTruncated").GetBoolean());
        Assert.Equal(1000, result.GetProperty("stdout").GetString()!.Length);
    }

    [Fact]
    public async Task AWorkspaceScriptRunsInItsWorkingDirectoryAndItsFilesAreVisible()
    {
        await Client.PostAsJsonAsync("/files/directories", new { path = "jobs" });
        await Client.PutAsJsonAsync("/files/text", new { path = "tools/make.py", content = "open('made.txt', 'w').write('done')" });

        var result = await Execute(new { scriptPath = "tools/make.py", workingDirectory = "jobs" });
        Assert.Equal("python", result.GetProperty("language").GetString());
        Assert.Equal(0, result.GetProperty("exitCode").GetInt32());

        var made = await Json(await Client.GetAsync("/files/text?path=jobs/made.txt"));
        Assert.Equal("done", made.GetProperty("content").GetString());
    }

    [Fact]
    public async Task InvalidExecutionRequestsAreRefused()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync("/executions", new { language = "ruby", code = "puts 1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync("/executions", new { language = "python" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync("/executions", new { language = "python", code = "print(1)", timeoutSeconds = 100_000 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync("/executions", new { language = "python", code = "print(1)", workingDirectory = "../" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsJsonAsync("/executions", new { scriptPath = "missing.py" })).StatusCode);
    }

    [Fact]
    public async Task RuntimesListEveryLanguage()
    {
        var runtimes = (await Json(await Client.GetAsync("/runtimes"))).EnumerateArray().ToList();
        Assert.Equal(["powershell", "python", "node", "bash"], runtimes.Select(runtime => runtime.GetProperty("language").GetString()));
        Assert.True(runtimes.Single(runtime => runtime.GetProperty("language").GetString() == "python").GetProperty("available").GetBoolean());
    }

    private Dictionary<string, string?> Settings()
    {
        return new Dictionary<string, string?>
        {
            ["Sandbox:WorkspaceRoot"] = Workspace,
            ["Sandbox:ApiKey"] = ApiKey,
            ["Sandbox:MaxOutputChars"] = "1000",
        };
    }

    private static WebApplication Build(IDictionary<string, string?> settings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddSandboxHost();
        var app = builder.Build();
        app.MapSandboxHost();
        return app;
    }

    private async Task<JsonElement> Execute(object request)
    {
        var response = await Client.PostAsJsonAsync("/executions", request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await Json(response);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static string[] Paths(JsonElement listing)
    {
        return listing.GetProperty("entries").EnumerateArray()
            .Where(entry => !entry.GetProperty("isDirectory").GetBoolean())
            .Select(entry => entry.GetProperty("path").GetString()!)
            .ToArray();
    }
}
