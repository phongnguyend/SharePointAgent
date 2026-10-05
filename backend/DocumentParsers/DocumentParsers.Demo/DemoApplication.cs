using System.Text;
using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace DocumentParsers.Demo;

internal static class DemoApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Help();
            return 0;
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .AddUserSecrets(typeof(DemoApplication).Assembly, optional: true)
                .AddEnvironmentVariables("DOCUMENTPARSERS_")
                .AddInMemoryCollection(ParseArguments(args))
                .Build();
            using var configurationLifetime = config as IDisposable;
            var input = config["Demo:InputPath"];
            if (string.IsNullOrWhiteSpace(input))
            {
                Help();
                return 0;
            }
            input = Path.GetFullPath(input);
            var format = new DocumentFormatResolver().Resolve(input, null);
            var output = config["Demo:OutputPath"];
            output = Path.GetFullPath(string.IsNullOrWhiteSpace(output) ? Path.ChangeExtension(input, ".md") : output);
            if (string.Equals(input, output, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new ArgumentException("Input and output paths must be different.");
            }
            var overwrite = config.GetValue<bool>("Demo:Overwrite");
            if (!overwrite && File.Exists(output))
            {
                throw new IOException("Output already exists. Choose another path or pass --overwrite.");
            }
            var skipImages = config.GetValue<bool>("Demo:SkipImages");
            var analyzeImages = !skipImages && config.GetValue<bool>("Demo:AnalyzeImages");
            var extractText = config.GetValue<bool>("Demo:ExtractText");
            if (extractText && !analyzeImages && !skipImages)
            {
                throw new ArgumentException("--ocr requires --analyze-images.");
            }
            var options = config.GetSection("Parser").Get<ParserOptions>() ?? new();
            var token = cancellation.Token;
            DocumentIntelligenceClient? documentClient = null;
            var pdfOcr = format == DocumentFormat.Pdf && config.GetValue<bool>("Demo:PdfOcr");
            if (pdfOcr || analyzeImages)
            {
                documentClient = new DocumentIntelligenceClient(Endpoint(config, "DocumentIntelligence:Endpoint"),
                    new AzureKeyCredential(Required(config, "DocumentIntelligence:ApiKey")));
            }
            using IChatClient? vision = analyzeImages
                ? new AzureOpenAIClient(Endpoint(config, "AzureOpenAI:Endpoint"),
                    new AzureKeyCredential(Required(config, "AzureOpenAI:ApiKey")))
                    .GetChatClient(Required(config, "AzureOpenAI:Deployment")).AsIChatClient()
                : null;
            var processor = vision is null ? null : new ImageDescriptionProcessor(
                new ImageAnalysisService(vision, documentClient!, config["AzureOpenAI:Deployment"]));
            var imageOptions = new ImageProcessingOptions
            {
                ExtractText = extractText,
                MaxConcurrency = config.GetValue("ImageProcessing:MaxConcurrency", 4),
                ConfigurationKey = "azure/" + config["AzureOpenAI:Deployment"] + "/demo-v1"
            };
            Console.Error.WriteLine($"Converting {Path.GetFileName(input)} ({format})...");
            await using var stream = File.OpenRead(input);
            string markdown;
            switch (format)
            {
                case DocumentFormat.Pdf:
                    var pdf = new PdfDocumentParser(pdfOcr ? documentClient : null, options);
                    var pdfResult = await pdf.ParseAsync(stream, token);
                    markdown = await RenderAsync(pdfResult.Elements.OfType<ImageElement>(), pdfResult.Warnings,
                        () => pdf.ConvertToMarkdown(pdfResult, token, skipImages));
                    break;
                case DocumentFormat.Docx:
                    var docx = new DocxDocumentParser(options);
                    var docxResult = await docx.ParseAsync(stream, token);
                    markdown = await RenderAsync(docxResult.BodyElements.OfType<ImageElement>(), docxResult.Warnings,
                        () => docx.ConvertToMarkdown(docxResult, token, skipImages));
                    break;
                case DocumentFormat.Pptx:
                    var pptx = new PptxDocumentParser(options);
                    var pptxResult = await pptx.ParseAsync(stream, token);
                    markdown = await RenderAsync(pptxResult.Slides.SelectMany(slide => slide.Elements).OfType<ImageElement>(), pptxResult.Warnings,
                        () => pptx.ConvertToMarkdown(pptxResult, token, skipImages));
                    break;
                case DocumentFormat.Xlsx:
                    var xlsx = new XlsxDocumentParser(options);
                    var xlsxResult = await xlsx.ParseAsync(stream, token);
                    markdown = await RenderAsync(xlsxResult.Worksheets.SelectMany(sheet => sheet.Images), xlsxResult.Warnings,
                        () => xlsx.ConvertToMarkdown(xlsxResult, token, skipImages));
                    break;
                default:
                    throw new NotSupportedException("Unsupported format.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await using (var target = new FileStream(output, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write))
            {
                await using var writer = new StreamWriter(target, new UTF8Encoding(false));
                await writer.WriteAsync(markdown.AsMemory(), token);
            }
            Console.WriteLine($"Markdown written to {output}");
            return 0;

            async Task<string> RenderAsync(IEnumerable<ImageElement> images, List<DocumentParseWarning> warnings, Func<string> render)
            {
                if (processor is not null)
                {
                    warnings.AddRange(await processor.ProcessAsync(images, imageOptions, token));
                }
                foreach (var warning in warnings)
                {
                    Console.Error.WriteLine($"Warning [{warning.Code}]: {warning.Message}");
                }
                return render();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Conversion cancelled.");
            return 130;
        }
        catch (RequestFailedException ex)
        {
            Console.Error.WriteLine($"Azure request failed (HTTP {ex.Status}). Check service settings and credentials.");
            return 1;
        }
        catch (System.ClientModel.ClientResultException ex)
        {
            Console.Error.WriteLine($"Vision request failed (HTTP {ex.Status}). Check deployment settings and credentials.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex is ArgumentException or IOException
                ? ex.Message : $"Conversion failed ({ex.GetType().Name}). Check the input file and configuration.");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static Dictionary<string, string?> ParseArguments(string[] args)
    {
        var values = new Dictionary<string, string?>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--input":
                case "--output":
                    var key = args[i] == "--input" ? "Demo:InputPath" : "Demo:OutputPath";
                    if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException("A path is required after --input or --output.");
                    }
                    values[key] = args[i];
                    break;
                case "--skip-images":
                    values["Demo:SkipImages"] = "true";
                    break;
                case "--analyze-images":
                    values["Demo:AnalyzeImages"] = "true";
                    break;
                case "--ocr":
                    values["Demo:ExtractText"] = "true";
                    break;
                case "--pdf-ocr":
                    values["Demo:PdfOcr"] = "true";
                    break;
                case "--overwrite":
                    values["Demo:Overwrite"] = "true";
                    break;
                default:
                    throw new ArgumentException("Unknown argument. Use --help for supported options.");
            }
        }
        return values;
    }

    private static string Required(IConfiguration config, string key)
    {
        var value = config[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Missing configuration: {key}. Set it with dotnet user-secrets for DocumentParsers.Demo.");
        }
        return value;
    }

    private static Uri Endpoint(IConfiguration config, string key)
    {
        if (!Uri.TryCreate(Required(config, key), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException($"{key} must be an absolute HTTPS endpoint.");
        }
        return uri;
    }

    private static void Help()
    {
        Console.WriteLine("""
            DocumentParsers.Demo
              --input <file>       PDF, DOCX, PPTX or XLSX (or set Demo:InputPath)
              --output <file.md>   Default: input filename with .md extension
              --skip-images       Keep image comments only; skip image enrichment
              --analyze-images    Enable Azure OpenAI image descriptions
              --ocr               Also extract image text; requires --analyze-images
              --pdf-ocr           OCR PDF pages without native text using Azure prebuilt-read
              --overwrite         Replace an existing output file
              --help              Show this help

            Settings: appsettings.json < user secrets < DOCUMENTPARSERS_ environment variables < CLI.
            Paths are relative to the working directory. Ctrl+C cancels conversion.
            """);
    }
}
