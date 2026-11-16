using Serilog;
using Serilog.Configuration;
using Serilog.Events;

namespace Serilog.Sinks.Dnstap;

/// <summary>Registers DNSTap file forwarding with Serilog's native batching infrastructure.</summary>
public static class DnstapSinkExtensions
{
    /// <summary>
    /// Writes typed DNSTap records into one Frame Streams file.
    /// The built-in Serilog batch retry window is disabled to prevent replaying
    /// records from a partially written session after an ambiguous failure.
    /// </summary>
    public static LoggerConfiguration DnstapFile(
        this LoggerSinkConfiguration configuration, string path,
        BatchingOptions? batchingOptions = null,
        LogEventLevel restrictedToMinimumLevel = LogEventLevel.Verbose)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var options = batchingOptions ?? new BatchingOptions
        {
            RetryTimeLimit = TimeSpan.Zero
        };

        if (options.RetryTimeLimit != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "DNSTap does not support automatic batch replay; RetryTimeLimit must be zero.",
                nameof(batchingOptions));
        }

        var sink = DnstapSink.CreateFile(path);
        try
        {
            return configuration.Sink(sink, options, restrictedToMinimumLevel);
        }
        catch
        {
            sink.Dispose();
            throw;
        }
    }
}
