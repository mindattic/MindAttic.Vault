using System.Text;
using MindAttic.Log;
using MindAttic.Log.Extensions;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Paths;
using Serilog;
using Serilog.Core;

namespace MindAttic.Vault.Desktop;

/// <summary>
/// MindAttic.Log, the workspace's one logging schema: Vault.Desktop writes to the standalone <c>MindAttic.Log.db</c>
/// Vault assigns it. Messages carry app ids, field names and key names, never a key or setting value; a
/// <see cref="NamedKey"/> that reaches a template destructures to its name only.
/// </summary>
internal static class DesktopLog
{
    public const string Application = "MindAttic.Vault.Desktop";

    public static void Start() => Serilog.Log.Logger = Configure(new LoggerConfiguration(), VaultPaths.LoggingDbPath(Application)).CreateLogger();

    /// <summary>The logger configuration the app uses, writing to <paramref name="dbPath"/> (tests point it at a temp folder).</summary>
    public static LoggerConfiguration Configure(LoggerConfiguration configuration, string dbPath) =>
        configuration
            .MinimumLevel.Information()
            .Enrich.WithProperty("Component", "Desktop")
            .Destructure.ByTransforming<NamedKey>(k => new { k.Name, k.Disabled })
            .WriteToMindAtticLog(new MindAtticLogOptions { Application = Application, DbPath = dbPath });

    /// <summary>A logger whose entries land in the log's Category column as <paramref name="category"/>.</summary>
    public static ILogger For(string category) => Serilog.Log.ForContext(Constants.SourceContextPropertyName, category);

    public static void Stop() => Serilog.Log.CloseAndFlush();

    /// <summary>
    /// An exception as type, message and stack only (inner exceptions too), so a crash log never dumps
    /// an exception's Data or a view model's state.
    /// </summary>
    public static string Describe(Exception? ex)
    {
        if (ex is null) return "(no exception)";
        var text = new StringBuilder();
        for (var (e, depth) = (ex, 0); e is not null && depth < 8; e = e.InnerException, depth++)
        {
            if (depth > 0) text.AppendLine("--- inner ---");
            text.Append(e.GetType().FullName).Append(": ").AppendLine(e.Message);
            if (e.StackTrace is { } stack) text.AppendLine(stack);
        }
        return text.ToString();
    }
}
