using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace WorkTrace.Infrastructure.Logging;

public static class LoggingSetup
{
    /// <summary>
    /// Adds a daily rolling file log (14 files retained) under <paramref name="logsDirectory"/>.
    /// Application code logs through <see cref="Microsoft.Extensions.Logging.ILogger{T}"/> only.
    /// Never log task titles, notes or screenshot contents.
    /// </summary>
    public static ILoggingBuilder AddWorkTraceFileLogging(this ILoggingBuilder builder, string logsDirectory)
    {
        Directory.CreateDirectory(logsDirectory);

        var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(logsDirectory, "worktrace-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        return builder.AddSerilog(logger, dispose: true);
    }
}
