using System;
using System.IO;
using System.Threading;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.Grafana.Loki;
using Microsoft.Extensions.Configuration;
using Serilog.Configuration;

namespace Damselfly.Core.Utils;

public class Logging
{
    private const string template = "[{Timestamp:HH:mm:ss.fff}-{TraceId}-{Level:u3}] {Message:lj}{NewLine}{Exception}";

    private static readonly LoggingLevelSwitch consoleLogLevel = new();
    private static readonly LoggingLevelSwitch logLevel = new();

    public static ILogger Logger { get; set; }
    public static string LogFolder { get; private set; }

    /// <summary>
    ///     True if verbose logging is enabled
    /// </summary>
    public static bool Verbose { get; set; } = false;

    /// <summary>
    ///     True if trace logging is enabled
    /// </summary>
    public static bool Trace { get; set; } = false;

    /// <summary>
    ///     Initialise logging and add the thread enricher.
    /// </summary>
    /// <returns></returns>
    public static void InitLogConfiguration(LoggerConfiguration logConfig, IConfigurationManager configManager)
    {
        try
        {
            var logFolder = configManager["DamselflyConfiguration:LogPath"];
            logFolder = Path.Combine(logFolder, "logs");
            LogFolder = logFolder;
            var lokiUrl = configManager["DamselflyConfiguration:LokiUrl"];

            if ( !Directory.Exists(logFolder) )
            {
                Console.WriteLine($"Creating log folder {logFolder}");
                Directory.CreateDirectory(logFolder);
            }

            logLevel.MinimumLevel = LogEventLevel.Information;
            consoleLogLevel.MinimumLevel = LogEventLevel.Information;

            if ( Verbose )
                logLevel.MinimumLevel = LogEventLevel.Verbose;

            if ( Trace )
                logLevel.MinimumLevel = LogEventLevel.Debug;

            var logFilePattern = Path.Combine(logFolder, "Damselfly-.log");
            var appLabel = "Damselfly";
#if DEBUG  
            appLabel += "-DEBUG";
#endif
            logConfig.Enrich.With([new TraceIdEnricher()])
                .WriteTo.Console(outputTemplate: template,
                    levelSwitch: consoleLogLevel)
                .WriteTo.File(logFilePattern,
                    outputTemplate: template,
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: 104857600,
                    retainedFileCountLimit: 10,
                    levelSwitch: logLevel)
                .WriteTo.GrafanaLoki(lokiUrl,
                    restrictedToMinimumLevel: LogEventLevel.Information, 
                    labels: [new LokiLabel { Key = "app", Value = appLabel }])
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
                ;
        }
        catch ( Exception ex )
        {
            Console.WriteLine($"Unable to initialise logs: {ex}");
        }
    }


    /// <summary>
    ///     Allow runtime toggling of debug logs
    /// </summary>
    /// <param name="newLevel"></param>
    public static void ChangeLogLevel(LogEventLevel newLevel)
    {
        if ( newLevel != logLevel.MinimumLevel )
        {
            logLevel.MinimumLevel = newLevel;

            Logger.Information("LogLevel: {0}", logLevel.MinimumLevel);
        }
    }

    public static void LogError(string fmt, params object[] args)
    {
        Logger?.Error(fmt, args);
    }

    public static void LogWarning(string fmt, params object[] args)
    {
        Logger?.Warning(fmt, args);
    }

    public static void LogVerbose(string fmt, params object[] args)
    {
        Logger?.Verbose(fmt, args);
    }

    public static void LogTrace(string fmt, params object[] args)
    {
        Logger?.Debug(fmt, args);
    }

    public static void Log(string fmt, params object[] args)
    {
        Logger?.Information(fmt, args);
    }

    public class TraceIdEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("TraceId", logEvent.TraceId));
        }
    }
}