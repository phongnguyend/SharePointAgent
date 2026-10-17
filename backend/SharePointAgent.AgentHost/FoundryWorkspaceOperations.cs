using System.Diagnostics;
using System.Text;
using SharePointAgent.Application;
using SharePointAgent.Infrastructure;

namespace SharePointAgent.AgentHost;

public static class FoundryWorkspaceOperations
{
    public static async Task<object?> RunAsync(AgentFileSystem files, FoundryWorkspaceRequest request, CancellationToken ct)
    {
        IAgentWorkspace workspace = files;
        var path = request.Path ?? "";
        var destination = request.Destination ?? "";
        switch (request.Operation)
        {
            case "list":
                return await workspace.ListAsync(path, request.Recursive, ct);
            case "find":
                return await workspace.FindAsync(path, ct);
            case "read":
                return await workspace.ReadAsync(path, ct);
            case "manage":
                return await workspace.ManageAsync(request.Change ?? throw new ArgumentException("A file operation is required."), ct);
            case "text":
                return await workspace.WriteTextAsync(path, request.Content ?? "", request.Overwrite, ct);
            case "write":
                using (var stream = new MemoryStream(Convert.FromBase64String(request.Content ?? "")))
                {
                    return await workspace.WriteAsync(path, stream, request.Overwrite, ct);
                }
            case "mkdir":
                return await workspace.CreateDirectoryAsync(path, ct);
            case "copy":
                return await workspace.CopyAsync(path, destination, request.Overwrite, ct);
            case "move":
                return await workspace.MoveAsync(path, destination, request.Overwrite, ct);
            case "delete":
                await workspace.DeleteAsync(path, request.Recursive, ct);
                return new { deleted = true };
            case "zip":
                return await workspace.ZipAsync(request.Paths ?? [], destination, request.Overwrite, ct);
            case "unzip":
                return await workspace.UnzipAsync(path, destination, request.Overwrite, ct);
            case "execute":
                return await ExecuteAsync(files, request.Execution ?? throw new ArgumentException("An execution is required."), ct);
            default:
                throw new ArgumentException("Unknown workspace operation.");
        }
    }

    private static async Task<WorkspaceExecutionResult> ExecuteAsync(AgentFileSystem files, WorkspaceExecution request, CancellationToken ct)
    {
        if (request.TimeoutSeconds is < 1 or > 220 || request.Code?.Length > 100_000
            || string.IsNullOrWhiteSpace(request.Code) == string.IsNullOrWhiteSpace(request.ScriptPath)
            || request.Arguments is null || request.Arguments.Count > 100 || request.Arguments.Any(x => x is null || x.Length > 4096))
        {
            throw new ArgumentException("Provide code or a script, valid arguments, and a timeout of 1–220 seconds.");
        }
        var (executable, extension) = request.Language switch
        {
            "python" => (OperatingSystem.IsWindows() ? "python" : "python3", ".py"),
            "node" => ("node", ".js"),
            "powershell" => ("pwsh", ".ps1"),
            "bash" => ("bash", ".sh"),
            _ => throw new ArgumentException("Choose Python, Node.js, PowerShell, or Bash.")
        };
        var directory = files.Resolve(request.WorkingDirectory ?? ".");
        if (!Directory.Exists(directory))
        {
            throw new ArgumentException("The working directory does not exist.");
        }
        var scratch = string.IsNullOrWhiteSpace(request.ScriptPath) ? Path.Combine(files.Root, $".agent-execution-{Guid.NewGuid():N}{extension}") : null;
        try
        {
            var script = scratch ?? files.Resolve(request.ScriptPath!);
            if (scratch is not null)
            {
                await File.WriteAllTextAsync(scratch, request.Code, new UTF8Encoding(false), ct);
            }
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
            };
            if (request.Language == "powershell")
            {
                foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File" })
                {
                    start.ArgumentList.Add(argument);
                }
            }
            start.ArgumentList.Add(script);
            foreach (var argument in request.Arguments)
            {
                start.ArgumentList.Add(argument);
            }
            using var process = Process.Start(start) ?? throw new IOException("Could not start the script runtime.");
            process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
            using var drain = new CancellationTokenSource();
            var stdout = DrainAsync(process.StandardOutput, drain.Token);
            var stderr = DrainAsync(process.StandardError, drain.Token);
            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                drain.CancelAfter(TimeSpan.FromSeconds(2));
            }
            var output = await stdout;
            var error = await stderr;
            ct.ThrowIfCancellationRequested();
            return new(process.HasExited ? process.ExitCode : null, timedOut, output, error);
        }
        finally
        {
            if (scratch is not null)
            {
                File.Delete(scratch);
            }
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader, CancellationToken ct)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                output.Append(buffer, 0, Math.Min(read, Math.Max(0, 1_000_000 - output.Length)));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        return output.ToString();
    }
}
