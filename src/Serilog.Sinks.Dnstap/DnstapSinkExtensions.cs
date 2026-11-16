using Serilog;
using Serilog.Configuration;
using Serilog.Events;

namespace Serilog.Sinks.Dnstap;

/// <summary>Registers DNSTap file and TCP forwarding with Serilog's native batching infrastructure.</summary>
public static class DnstapSinkExtensions
{
    /// <summary>
    /// Writes typed DNSTap records into one Frame Streams file.
    /// The built-in Serilog batch retry window is disabled to prevent replaying
    /// records from a partially written session after an ambiguous failure.
    /// </summary>
    /// <summary>
    /// Registers TCP forwarding. The connection starts asynchronously on the first DNSTap record,
    /// not when constructing the logger. Connection and handshake failures are terminal.
    /// </summary>
    public static LoggerConfiguration DnstapTcp(
        this LoggerSinkConfiguration configuration, string host, int port,
        BatchingOptions? batchingOptions = null,
        LogEventLevel restrictedToMinimumLevel = LogEventLevel.Verbose,
        TimeSpan? connectionTimeout = null, TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = ValidateBatching(batchingOptions);
        var sink = DnstapSink.CreateTcp(host, port, connectionTimeout, operationTimeout);
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

    private static BatchingOptions ValidateBatching(BatchingOptions? batchingOptions)
    {
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
        return options;
    }

    public static LoggerConfiguration DnstapFile(
        this LoggerSinkConfiguration configuration, string path,
        BatchingOptions? batchingOptions = null,
        LogEventLevel restrictedToMinimumLevel = LogEventLevel.Verbose,
        bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var options = ValidateBatching(batchingOptions);
        var sink = DnstapSink.CreateFile(path, overwrite);
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
