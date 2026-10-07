using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NoteEvolution.AI.Embeddings;
using NoteEvolution.AI.Model;
using NoteEvolution.Core.Storage;
using NoteEvolution.Core.Vaults;
using NoteEvolution.Pdf;
using NoteEvolution.UI.Components;
using NoteEvolution.UI.Editor;
using NoteEvolution.UI.Platform;
using NoteEvolution.UI.State;
using Photino.Blazor;
using Serilog;

namespace NoteEvolution.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var userData = PhotinoPlatformServices.DefaultUserDataDirectory;
        using var logSink = new VaultLogSink(Path.Combine(userData, "logs"));
        Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(logSink).CreateLogger();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception");

        // The model lives in the user profile; it is downloaded only when the user confirms it (AiModelDialog).
        var models = new ModelStore(userData);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        AppState? state = null;
        AiRuntime? ai = null;
        try
        {
            var builder = PhotinoBlazorAppBuilder.CreateDefault(args);
            builder.Services.AddLogging(logging => logging.ClearProviders().AddSerilog());
            builder.Services.AddLocalization();
            builder.Services.AddSingleton<PhotinoPlatformServices>();
            builder.Services.AddSingleton<IPlatformServices>(sp => sp.GetRequiredService<PhotinoPlatformServices>());
            builder.Services.AddSingleton(UiSettings.Load(userData));
            builder.Services.AddSingleton<AppState>();
            builder.Services.AddSingleton<IClock, SystemClock>();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<IPdfExporter, QuestPdfExporter>();
            builder.Services.AddSingleton(sp => new AiRuntime(
                models,
                new ModelDownloader(http, models),
                () => OnnxEmbedder.Load(models, ModelCatalog.E5Small),
                sp.GetRequiredService<ILogger<AiRuntime>>()));
            builder.Services.AddTransient<IEditorInterop, TipTapInterop>();
            builder.RootComponents.Add<Shell>("#app");

            var app = builder.Build();
            app.Services.GetRequiredService<PhotinoPlatformServices>().Window = app.MainWindow;

            // Once a vault is open, the log continues in its .noteevolution/logs folder.
            state = app.Services.GetRequiredService<AppState>();
            ai = app.Services.GetRequiredService<AiRuntime>();
            var appState = state;
            appState.Changed += () =>
            {
                if (appState.Session is { } session
                    && logSink.UseDirectory(Path.Combine(session.Vault.Root, VaultSettings.FolderName, "logs")))
                {
                    Log.Information("Vault opened: {Root}", session.Vault.Root);
                }
            };

            // The editor saves its pending text before the window closes (and before the session is disposed below).
            var closeGuard = new EditorCloseGuard(
                appState,
                work => app.WindowManager.Dispatcher.InvokeAsync(work),
                () => app.MainWindow.Close(),
                TimeProvider.System,
                app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<EditorCloseGuard>());
            app.MainWindow.RegisterWindowClosingHandler((_, _) => closeGuard.OnClosing());

            app.MainWindow
                .SetLogVerbosity(0)
                .SetTitle("NoteEvolution")
                .SetUseOsDefaultSize(false)
                .SetSize(1400, 900)
                .Center();

            Log.Information("NoteEvolution started");
            app.Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "NoteEvolution terminated unexpectedly");
            throw;
        }
        finally
        {
            // The session lets go of the shared embedder before the runtime disposes it.
            state?.Session?.Dispose();
            ai?.Dispose();
            Log.CloseAndFlush();
        }
    }
}
