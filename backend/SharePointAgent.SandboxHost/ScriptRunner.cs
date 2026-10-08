using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;

namespace SharePointAgent.SandboxHost;

/// <summary>
/// Runs PowerShell, Python, Node.js, and Bash in the workspace. Each run is one child process with a
/// timeout that kills its whole process tree, bounded stdout and stderr, and stdin closed unless input
/// is supplied, so a script waiting on the keyboard fails instead of hanging until the timeout.
/// </summary>
public sealed class ScriptRunner(IOptions<SandboxHostOptions> options, SandboxWorkspace workspace, ILogger<ScriptRunner> logger)
{
    public const string PowerShell = "powershell";

    public const string Python = "python";

    public const string Node = "node";

    public const string Bash = "bash";

    public static readonly IReadOnlyList<string> Languages = [PowerShell, Python, Node, Bash];

    /// <summary>How long to keep reading output after the process exits, in case a background child still holds the pipe.</summary>
    private static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(2);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly SandboxHostOptions _options = options.Value;

    private readonly Lazy<Task<IReadOnlyList<RuntimeInfo>>> _runtimes = new(() => DetectRuntimesAsync(options.Value));

    public Task<IReadOnlyList<RuntimeInfo>> GetRuntimesAsync()
    {
        return _runtimes.Value;
    }

    public async Task<ExecutionResult> RunAsync(ExecutionRequest request, CancellationToken cancellationToken)
    {
        var hasCode = !string.IsNullOrEmpty(request.Code);
        var hasScript = !string.IsNullOrWhiteSpace(request.ScriptPath);
        if (hasCode == hasScript)
        {
            throw new ArgumentException("Provide exactly one of code or scriptPath.");
        }

        var language = Normalize(request.Language)
            ?? (hasScript ? FromExtension(request.ScriptPath!) : null)
            ?? throw new ArgumentException($"language is required. Use one of: {string.Join(", ", Languages)}.");
        var timeoutSeconds = request.TimeoutSeconds ?? _options.DefaultTimeoutSeconds;
        if (timeoutSeconds < 1 || timeoutSeconds > _options.MaxTimeoutSeconds)
        {
            throw new ArgumentException($"timeoutSeconds must be between 1 and {_options.MaxTimeoutSeconds}.");
        }

        var workingDirectory = workspace.Resolve(request.WorkingDirectory);
        if (!Directory.Exists(workingDirectory))
        {
            throw new ArgumentException($"workingDirectory '{workspace.Relative(workingDirectory)}' is not an existing directory.");
        }

        string? scratch = null;
        try
        {
            string scriptFile;
            if (hasCode)
            {
                // Inline code is written outside the workspace so it never shows up in the caller's files.
                scratch = Path.Combine(Path.GetTempPath(), "sandbox-executions", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(scratch);
                scriptFile = Path.Combine(scratch, "script" + Extension(language));
                // Windows PowerShell 5.1 reads a file without a byte order mark as ANSI; PowerShell 7 accepts either.
                await File.WriteAllTextAsync(scriptFile, request.Code, language == PowerShell ? new UTF8Encoding(true) : Utf8NoBom, cancellationToken);
            }
            else
            {
                scriptFile = workspace.ResolveFile(request.ScriptPath);
            }

            var start = CreateStartInfo(language, Executable(_options, language), scriptFile, request.Arguments, workingDirectory);
            ApplyEnvironment(start.Environment, request.Environment);
            return await RunProcessAsync(language, start, request.Stdin, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);
        }
        finally
        {
            if (scratch is not null && Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    /// <summary>
    /// Prepares the child's environment. Host settings — the <c>Sandbox__*</c> variables, including the
    /// API key — are removed, so code that prints its environment cannot hand back the key it was called with.
    /// </summary>
    public static void ApplyEnvironment(IDictionary<string, string?> environment, IReadOnlyDictionary<string, string>? overrides)
    {
        foreach (var key in environment.Keys.Where(key => key.StartsWith(SandboxHostOptions.SectionName + "__", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            environment.Remove(key);
        }

        environment["PYTHONIOENCODING"] = "utf-8";
        environment["PYTHONUNBUFFERED"] = "1";
        environment["NO_COLOR"] = "1";
        if (overrides is null)
        {
            return;
        }

        foreach (var (name, value) in overrides)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains('=') || name.Contains('\0'))
            {
                throw new ArgumentException($"'{name}' is not a valid environment variable name.");
            }

            environment[name] = value;
        }
    }

    public static string? Normalize(string? language)
    {
        return language?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "powershell" or "pwsh" or "ps1" => PowerShell,
            "python" or "python3" or "py" => Python,
            "node" or "nodejs" or "javascript" or "js" => Node,
            "bash" or "sh" or "shell" => Bash,
            _ => throw new ArgumentException($"'{language}' is not supported. Use one of: {string.Join(", ", Languages)}."),
        };
    }

    private static string? FromExtension(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".ps1" => PowerShell,
            ".py" => Python,
            ".js" or ".mjs" or ".cjs" => Node,
            ".sh" => Bash,
            _ => null,
        };
    }

    private static string Extension(string language)
    {
        return language switch
        {
            PowerShell => ".ps1",
            Python => ".py",
            // Plain .js so Node 22's module detection accepts both require() and import.
            Node => ".js",
            _ => ".sh",
        };
    }

    private static string Executable(SandboxHostOptions options, string language)
    {
        if (options.Executables.TryGetValue(language, out var configured) && !string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return language switch
        {
            PowerShell => OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh",
            Python => OperatingSystem.IsWindows() ? "python.exe" : "python3",
            Node => "node",
            _ => "bash",
        };
    }

    private static ProcessStartInfo CreateStartInfo(string language, string executable, string scriptFile, IReadOnlyList<string>? arguments, string workingDirectory)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        if (language == PowerShell)
        {
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
        }
        else if (language == Python)
        {
            start.ArgumentList.Add("-X");
            start.ArgumentList.Add("utf8");
            start.ArgumentList.Add("-u");
        }

        start.ArgumentList.Add(scriptFile);
        foreach (var argument in arguments ?? [])
        {
            start.ArgumentList.Add(argument ?? "");
        }

        return start;
    }

    private async Task<ExecutionResult> RunProcessAsync(string language, ProcessStartInfo start, string? stdin, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"The {language} runtime ('{start.FileName}') could not be started: {ex.Message}");
        }

        var stdout = new OutputCapture(process.StandardOutput, _options.MaxOutputChars);
        var stderr = new OutputCapture(process.StandardError, _options.MaxOutputChars);
        await WriteStdinAsync(process, stdin);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            timedOut = true;
        }

        // A background child that inherited the pipes keeps them open after the script itself exits.
        await Task.WhenAny(Task.WhenAll(stdout.Completion, stderr.Completion), Task.Delay(OutputGrace, CancellationToken.None));
        stopwatch.Stop();

        int? exitCode = timedOut ? null : process.ExitCode;
        logger.LogInformation("Ran {Language} in {DurationMs} ms: exit code {ExitCode}, timed out {TimedOut}",
            language, stopwatch.ElapsedMilliseconds, exitCode, timedOut);
        var (output, outputTruncated) = stdout.Snapshot();
        var (error, errorTruncated) = stderr.Snapshot();
        return new(language, exitCode, timedOut, output, error, outputTruncated, errorTruncated, stopwatch.ElapsedMilliseconds);
    }

    private static async Task WriteStdinAsync(Process process, string? stdin)
    {
        try
        {
            if (!string.IsNullOrEmpty(stdin))
            {
                await process.StandardInput.WriteAsync(stdin);
            }

            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The process exited without reading its input; its exit code and output still tell the story.
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(5));
            }
        }
        catch (InvalidOperationException)
        {
            // Exited between the check and the kill.
        }
    }

    private static async Task<IReadOnlyList<RuntimeInfo>> DetectRuntimesAsync(SandboxHostOptions options)
    {
        var detected = await Task.WhenAll(Languages.Select(async language =>
        {
            var executable = Executable(options, language);
            var start = new ProcessStartInfo(executable)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (language == PowerShell)
            {
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-NonInteractive");
                start.ArgumentList.Add("-Command");
                start.ArgumentList.Add("$PSVersionTable.PSVersion.ToString()");
            }
            else
            {
                start.ArgumentList.Add("--version");
            }

            try
            {
                using var process = Process.Start(start)!;
                process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                var text = (await output).Trim();
                if (text.Length == 0)
                {
                    text = (await error).Trim();
                }

                var version = text.Split('\n', 2)[0].Trim();
                return new RuntimeInfo(language, executable, process.ExitCode == 0, version.Length == 0 ? null : version);
            }
            catch (Exception ex) when (ex is Win32Exception or OperationCanceledException or InvalidOperationException)
            {
                return new RuntimeInfo(language, executable, false, null);
            }
        }));
        return detected;
    }

    /// <summary>Reads a stream to its end, keeping the first <c>limit</c> characters and dropping the rest.</summary>
    private sealed class OutputCapture
    {
        private readonly StringBuilder _text = new();

        private readonly int _limit;

        private bool _truncated;

        public OutputCapture(StreamReader reader, int limit)
        {
            _limit = limit;
            Completion = Task.Run(() => ReadAsync(reader));
        }

        public Task Completion { get; }

        public (string Text, bool Truncated) Snapshot()
        {
            lock (_text)
            {
                return (_text.ToString(), _truncated);
            }
        }

        private async Task ReadAsync(StreamReader reader)
        {
            var buffer = new char[8192];
            try
            {
                int read;
                while ((read = await reader.ReadAsync(buffer)) > 0)
                {
                    lock (_text)
                    {
                        var room = _limit - _text.Length;
                        if (room > 0)
                        {
                            _text.Append(buffer, 0, Math.Min(room, read));
                        }

                        if (read > room)
                        {
                            _truncated = true;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The pipe closed under us, which happens when the process is disposed before a background child exits.
            }
        }
    }
}
