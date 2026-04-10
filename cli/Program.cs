using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text;

namespace LittleHelper;

class Program
{
    /// <summary>
    /// Resolve the bundled skills directory, checking both dev and deployment locations.
    /// </summary>
    private static string ResolveBundledSkillsDir()
    {
        var assemblyDir = AppContext.BaseDirectory;
        var devPath = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", "skills"));
        if (Directory.Exists(devPath))
            return devPath;
        return Path.GetFullPath(Path.Combine(assemblyDir, "skills"));
    }

    static async Task<int> Main(string[] args)
    {
        // The prompt is a positional argument (everything after options)
        var promptArgument = new Argument<string[]>(
            name: "prompt",
            description: "The task prompt. Required unless using a subcommand.")
        { Arity = ArgumentArity.ZeroOrMore };

        // Define CLI options
        var modelOption = new Option<string>(
            aliases: new[] { "--model", "-m" },
            description: "Model name or 'provider/model' (e.g., qwen3:14b, ollama/qwen3:8b)",
            getDefaultValue: () => "");

        var endpointOption = new Option<string>(
            aliases: new[] { "--endpoint", "-e" },
            description: "Model API endpoint (overrides config file)",
            getDefaultValue: () => "");

        var directoryOption = new Option<string>(
            aliases: new[] { "--dir", "-d" },
            description: "Working directory",
            getDefaultValue: () => Directory.GetCurrentDirectory());

        var contextOption = new Option<int>(
            aliases: new[] { "--context", "-c" },
            description: "Max context tokens (0 = use config default)",
            getDefaultValue: () => 0);

        var stepsOption = new Option<int>(
            aliases: new[] { "--max-steps", "-s" },
            description: "Maximum agent steps",
            getDefaultValue: () => 30);

        var blockDestructiveOption = new Option<bool>(
            aliases: new[] { "--block-destructive", "-b" },
            description: "Block destructive commands",
            getDefaultValue: () => false);

        var temperatureOption = new Option<double>(
            aliases: new[] { "--temperature", "-t" },
            description: "Model sampling temperature (0 = use config default)",
            getDefaultValue: () => 0);

        // Root command
        var rootCommand = new RootCommand("little_helper — A lean agent harness for local models");
        rootCommand.AddArgument(promptArgument);
        rootCommand.AddOption(modelOption);
        rootCommand.AddOption(endpointOption);
        rootCommand.AddOption(directoryOption);
        rootCommand.AddOption(contextOption);
        rootCommand.AddOption(stepsOption);
        rootCommand.AddOption(blockDestructiveOption);
        rootCommand.AddOption(temperatureOption);
        rootCommand.TreatUnmatchedTokensAsErrors = false;

        // 'skills' subcommand
        var skillsCommand = new Command("skills", "List available skills");
        skillsCommand.SetHandler(() => HandleSkillsCommand());
        rootCommand.AddCommand(skillsCommand);

        // 'models' subcommand
        var modelsCommand = new Command("models", "List or configure models");
        var modelsInitOption = new Option<bool>(
            aliases: new[] { "--init", "-i" },
            description: "Create default ~/.little_helper/models.json");
        modelsCommand.AddOption(modelsInitOption);
        modelsCommand.SetHandler((bool init) => HandleModelsCommand(init), modelsInitOption);
        rootCommand.AddCommand(modelsCommand);

        // Main handler
        rootCommand.SetHandler(async (InvocationContext context) =>
        {
            var modelArg = context.ParseResult.GetValueForOption(modelOption)!;
            var endpointArg = context.ParseResult.GetValueForOption(endpointOption)!;
            var dir = context.ParseResult.GetValueForOption(directoryOption)!;
            var maxContext = context.ParseResult.GetValueForOption(contextOption);
            var maxSteps = context.ParseResult.GetValueForOption(stepsOption);
            var blockDestructive = context.ParseResult.GetValueForOption(blockDestructiveOption);
            var temperatureArg = context.ParseResult.GetValueForOption(temperatureOption);
            var promptParts = context.ParseResult.GetValueForArgument(promptArgument);

            var modelConfig = ModelConfig.Load();
            var resolved = ConfigResolver.Resolve(modelArg, endpointArg, maxContext, temperatureArg, modelConfig);

            var exitCode = await RunAgent(
                resolved, dir, maxSteps, blockDestructive, promptParts);
            context.ExitCode = exitCode;
        });

        return await rootCommand.InvokeAsync(args);
    }

    static void HandleSkillsCommand()
    {
        var skills = new SkillDiscovery();
        skills.Discover(Directory.GetCurrentDirectory(), ResolveBundledSkillsDir());

        Console.WriteLine("Available skills:");
        Console.WriteLine();

        if (skills.Skills.Count == 0)
        {
            Console.WriteLine("  No skills found.");
            Console.WriteLine();
            Console.WriteLine("Skills are discovered from:");
            Console.WriteLine("  ~/.little_helper/skills/     (user-level)");
            Console.WriteLine("  .little_helper/skills/       (project-level)");
            return;
        }

        foreach (var skill in skills.Skills)
        {
            Console.WriteLine($"  {skill.Name}");
            Console.WriteLine($"    {skill.Description}");
            Console.WriteLine($"    Location: {skill.FilePath}");
            Console.WriteLine();
        }
    }

    static void HandleModelsCommand(bool init)
    {
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".little_helper", "models.json");

        if (init)
        {
            if (File.Exists(configPath))
            {
                Console.WriteLine($"Config already exists: {configPath}");
                Console.WriteLine("Edit it directly to add providers and models.");
                return;
            }

            ModelConfig.CreateDefault().Save();
            Console.WriteLine($"Created default config: {configPath}");
            Console.WriteLine();
            Console.WriteLine("Edit it to add your models and providers.");
            return;
        }

        var config = ModelConfig.Load();
        var models = config.GetAllModels();

        Console.WriteLine("Configured models:");
        Console.WriteLine();

        if (models.Count == 0)
        {
            Console.WriteLine("  No models configured.");
            Console.WriteLine();
            Console.WriteLine("Run 'little_helper models --init' to create a default config,");
            Console.WriteLine("or create ~/.little_helper/models.json manually.");
            Console.WriteLine();
            Console.WriteLine("Usage: little_helper -m <model-id>");
            Console.WriteLine("       little_helper -m <provider>/<model-id>");
            return;
        }

        foreach (var (provider, modelId, name, ctxWindow, apiType) in models)
        {
            var isDefault = modelId == config.DefaultModel ? " (default)" : "";
            var apiTag = apiType != "openai" ? $" [{apiType}]" : "";
            Console.WriteLine($"  {provider}/{modelId}{isDefault}{apiTag}");
            Console.WriteLine($"    Name: {name}");
            Console.WriteLine($"    Context: {ctxWindow} tokens");
            Console.WriteLine();
        }

        Console.WriteLine($"Default model: {config.DefaultModel}");
        Console.WriteLine();
        Console.WriteLine("Config file: ~/.little_helper/models.json");
    }

    static async Task<int> RunAgent(
        ResolvedConfig resolved, string dir, int maxSteps, bool blockDestructive,
        string[] promptParts)
    {
        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine($"Error: Directory not found: {dir}");
            return 1;
        }

        // Build prompt from positional arguments or stdin
        string prompt;
        if (promptParts.Length > 0)
        {
            prompt = string.Join(" ", promptParts);
        }
        else if (!Console.IsInputRedirected)
        {
            Console.Error.WriteLine("Usage: little_helper [options] <prompt>");
            Console.Error.WriteLine("       echo 'prompt' | little_helper [options]");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Commands: models (list/configure), skills (list available)");
            Console.Error.WriteLine("Examples: little_helper -m qwen3:8b 'write a hello world'");
            return 1;
        }
        else
        {
            var sb = new StringBuilder();
            string? line;
            while ((line = Console.ReadLine()) != null)
                sb.AppendLine(line);
            prompt = sb.ToString().Trim();
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            Console.Error.WriteLine("Error: No prompt provided");
            return 1;
        }

        var config = new AgentConfig(
            ModelEndpoint: resolved.Endpoint, ModelName: resolved.ModelId,
            MaxContextTokens: resolved.ContextWindow, MaxSteps: maxSteps,
            MaxRetries: 2, StallThreshold: 5,
            WorkingDirectory: Path.GetFullPath(dir), Temperature: resolved.Temperature,
            ApiKey: resolved.ApiKey, ExtraHeaders: resolved.Headers);

        Console.WriteLine("little_helper v0.1.0");
        Console.WriteLine($"Model: {resolved.ModelId}");
        Console.WriteLine($"Endpoint: {resolved.Endpoint}");
        Console.WriteLine($"API: {resolved.ApiType}");
        if (!string.IsNullOrEmpty(resolved.ApiKey))
            Console.WriteLine("Auth: ***");
        Console.WriteLine($"Working dir: {config.WorkingDirectory}");
        Console.WriteLine();

        try
        {
            var skills = new SkillDiscovery();
            skills.Discover(config.WorkingDirectory, ResolveBundledSkillsDir());

            using IModelClient modelClient = CreateModelClient(resolved);
            ToolSchemas.RegisterAll(modelClient);

            var toolExecutor = new ToolExecutor(config.WorkingDirectory, blockDestructive);
            using var logger = new SessionLogger(config.ModelName, config.WorkingDirectory);
            var agent = new Agent(config, modelClient, toolExecutor, skills, logger);
            var cts = new CancellationTokenSource();

            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                Console.WriteLine("\n[Cancelled by user]");
            };

            var result = await agent.RunAsync(prompt, cts.Token);

            Console.WriteLine();
            Console.WriteLine(new string('=', 50));

            if (result.Success)
                Console.WriteLine($"Result:\n{result.Output}");
            else
                Console.WriteLine($"Agent stopped:\n{result.Output}");

            if (result.FilesChanged.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Files changed:");
                foreach (var file in result.FilesChanged)
                    Console.WriteLine($"  {file}");
            }

            Console.WriteLine();
            Console.WriteLine($"Tokens used: {modelClient.TotalTokensUsed}");
            if (modelClient.ThinkingLog.Count > 0)
            {
                Console.WriteLine($"Thinking tokens: ~{modelClient.TotalThinkingTokens} ({modelClient.ThinkingLog.Count} steps)");
            }
            Console.WriteLine($"Session log: {logger.LogPath}");
            return result.Success ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\n[Operation cancelled]");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (ex.InnerException != null)
                Console.Error.WriteLine($"  -> {ex.InnerException.Message}");
            return 1;
        }
    }

    /// <summary>Create the appropriate model client based on API type.</summary>
    static IModelClient CreateModelClient(ResolvedConfig resolved) => resolved.ApiType switch
    {
        "anthropic" => new AnthropicClient(
            resolved.Endpoint, resolved.ModelId, resolved.Temperature,
            resolved.ApiKey, resolved.Headers),
        _ => new ModelClient(
            resolved.Endpoint, resolved.ModelId, resolved.Temperature,
            resolved.ApiKey, resolved.Headers)
    };
}