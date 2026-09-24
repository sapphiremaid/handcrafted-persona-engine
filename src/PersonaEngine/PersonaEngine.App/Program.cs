using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PersonaEngine.Lib;
using PersonaEngine.Lib.Bootstrapper;
using PersonaEngine.Lib.Core;
using Serilog;
using Whisper.net.LibraryLoader;

namespace PersonaEngine.App;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        LoggingConfiguration.ConfigureSerilog();
        LoggingConfiguration.InstallGlobalExceptionHandlers();

        // Register <BaseDir>/native as an extra DLL search directory for loose
        // native libs relocated by the publish target. This only needs to happen
        // once and has no dependency on ML backend assets.
        NativeLibraryLoader.RegisterNativeSearchDirectory();

        // ── Bootstrap ────────────────────────────────────────────────────────────
        // Run the asset bootstrapper before any subsystem that depends on models
        // or native runtimes. On failure we exit with a non-zero code so launchers
        // can surface the error; on success we continue into the main DI graph.
        // Bootstrap runs before IConfiguration is built, so it sees only CLI args
        // and the embedded manifest — any future bootstrap setting must be a CLI
        // flag, not an appsettings.json entry.
        //
        // Dev escape hatch: when --skip-bootstrap is passed or the env var
        // PERSONAENGINE_SKIP_BOOTSTRAP is truthy (set automatically by
        // Properties/launchSettings.json in VS / Rider / `dotnet run`), we skip
        // the runner entirely. Assets under Resources/ must then already be
        // populated out-of-band — downstream subsystems will surface clear errors
        // if they aren't, and downstream subsystems will surface clearer errors.
        var parsedArgs = CommandLineArgs.Parse(args);
        if (ShouldSkipBootstrap(parsedArgs, out var skipReason))
        {
            Log.Warning(
                "Skipping bootstrap ({Reason}). Assets under Resources/ must already be populated.",
                skipReason
            );
        }
        else if (!await RunBootstrapAsync(parsedArgs).ConfigureAwait(false))
        {
            await Log.CloseAndFlushAsync();
            return 1;
        }

        // -- Native ML runtime setup -------------------------------------------------
        // Whisper prefers Vulkan on Intel GPUs and falls back to CPU if Vulkan cannot initialize.
        RuntimeOptions.RuntimeLibraryOrder =
        [
            RuntimeLibrary.Vulkan,
            RuntimeLibrary.Cpu,
        ];

        // Qwen3 expressive TTS uses LLamaSharp's Vulkan backend in this fork.
        NativeLibraryLoader.PreloadLlamaBackend();
        LoggingConfiguration.BridgeLlamaLogging();

        // Initialize ONNX Runtime logging after DirectML is available.
        LoggingConfiguration.SuppressOnnxRuntimeWarnings();

        // ── Configuration + validation ───────────────────────────────────────────
        IConfiguration config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        if (!StartupValidator.Run(config))
        {
            await Log.CloseAndFlushAsync();
            Console.WriteLine();
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey(intercept: true);
            return 1;
        }

        // ── Application composition ──────────────────────────────────────────────
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog());
        services.AddMetrics();
        services.AddApp(config);

        await using var serviceProvider = services.BuildServiceProvider();
        var window = serviceProvider.GetRequiredService<AvatarApp>();
        window.Run();

        return 0;
    }

    /// <summary>
    ///     Env var consulted to skip the bootstrapper without a CLI flag. Any of
    ///     <c>1</c>, <c>true</c>, <c>yes</c> (case-insensitive, whitespace-trimmed) enables skip.
    ///     Set automatically by <c>Properties/launchSettings.json</c> so VS / Rider /
    ///     <c>dotnet run</c> never trigger the bootstrapper in a dev checkout.
    /// </summary>
    private const string SkipBootstrapEnvVar = "PERSONAENGINE_SKIP_BOOTSTRAP";

    /// <summary>
    ///     Resolves the dev-mode skip signal from CLI args or environment.
    ///     <paramref name="reason" /> is the human-readable source of the signal, suitable
    ///     for a single log line; it is only meaningful when the method returns <c>true</c>.
    /// </summary>
    private static bool ShouldSkipBootstrap(CommandLineArgs args, out string reason)
    {
        if (args.SkipBootstrap)
        {
            reason = "--skip-bootstrap flag";
            return true;
        }

        var raw = Environment.GetEnvironmentVariable(SkipBootstrapEnvVar)?.Trim();
        if (IsTruthy(raw))
        {
            reason = $"{SkipBootstrapEnvVar}={raw}";
            return true;
        }

        reason = string.Empty;
        return false;

        static bool IsTruthy(string? value) =>
            value is not null
            && (
                value.Equals("1", StringComparison.Ordinal)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            );
    }

    /// <summary>
    ///     Runs the asset bootstrapper in an isolated DI scope. Returns <c>true</c> when
    ///     the bootstrap pipeline completes successfully or there's nothing to do.
    /// </summary>
    private static async Task<bool> RunBootstrapAsync(CommandLineArgs parsedArgs)
    {
        var bootstrapServices = new ServiceCollection();
        bootstrapServices.AddBootstrapper(parsedArgs.NonInteractive);
        // Wire MEL through the static Serilog logger so bootstrap-time diagnostics
        // (per-asset download failures, provider retries, plan details) reach the
        // console sink. Without this, ILogger<T> in the bootstrap graph no-ops and
        // the only operator-visible signal is the final "Bootstrap failed" FTL line.
        bootstrapServices.AddLogging(b => b.AddSerilog(dispose: false));

        await using var bootstrapProvider = bootstrapServices.BuildServiceProvider();
        var runner = bootstrapProvider.GetRequiredService<BootstrapRunner>();
        var result = await runner
            .RunAsync(parsedArgs.Bootstrap, CancellationToken.None)
            .ConfigureAwait(false);

        if (result.Success)
        {
            return true;
        }

        Log.Fatal("Bootstrap failed: {Reason}", result.ErrorMessage);
        Console.Error.WriteLine($"Bootstrap failed: {result.ErrorMessage}");
        return false;
    }
}
