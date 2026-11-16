using Serilog;
using Serilog.Configuration;
using Serilog.Events;

namespace Serilog.Sinks.Dnstap;

/// <summary>Registers DNSTap file and TCP forwarding with Serilog's native batching infrastructure.</summary>
public static class DnstapSinkExtensions
{
    /// <summary>
    /// Registers TCP forwarding. The connection starts asynchronously on the first DNSTap record,
    /// not when constructing the logger. Failed TCP sessions reopen after a cooldown
    /// without retrying or replaying any failed records.
    /// </summary>
    public static LoggerConfiguration DnstapTcp(
        this LoggerSinkConfiguration configuration, string host, int port,
        BatchingOptions? batchingOptions = null,
        LogEventLevel restrictedToMinimumLevel = LogEventLevel.Verbose,
        TimeSpan? connectionTimeout = null, TimeSpan? operationTimeout = null,
        TimeSpan? reconnectInterval = null,
        Action<DnstapSink>? onSinkCreated = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = ValidateBatching(batchingOptions);
        var sink = DnstapSink.CreateTcp(host, port, connectionTimeout, operationTimeout, reconnectInterval);
        sink.SuppressTransportExceptions = true;
        try
        {
            onSinkCreated?.Invoke(sink);
            return configuration.Conditional(
                e =>
                {
                    if (e.Level < restrictedToMinimumLevel ||
                        !DnstapEventProperty.TryGetScalar(e, out var value) ||
                        value is not global::Dnstap.Dnstap)
                        return false;
                    sink.RecordSubmission();
                    return true;
                },
                writeTo => writeTo.Sink(sink, options, restrictedToMinimumLevel));
        }
        catch
        {
            sink.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Validates native Serilog batching settings before creating any destination.
    /// Native batching owns the only queue and retry scheduler.
    /// </summary>
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

        if (options.BatchSizeLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchingOptions),
                "BatchSizeLimit must be greater than zero.");
        }

        if (options.BufferingTimeLimit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(batchingOptions),
                "BufferingTimeLimit must be greater than zero.");
        }

        if (options.QueueLimit is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchingOptions),
                "QueueLimit must be greater than zero when specified.");
        }

        return options;
    }

    /// <summary>
    /// Registers a file-mode DNSTap sink. Existing files are replaced by default; set overwrite to false to preserve them.
    /// Serilog's batching queue is used with no automatic retries.
    /// </summary>
    public static LoggerConfiguration DnstapFile(
        this LoggerSinkConfiguration configuration, string path,
        BatchingOptions? batchingOptions = null,
        LogEventLevel restrictedToMinimumLevel = LogEventLevel.Verbose,
        bool overwrite = true,
        Action<DnstapSink>? onSinkCreated = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var options = ValidateBatching(batchingOptions);
        var sink = DnstapSink.CreateFile(path, overwrite);
        sink.SuppressTransportExceptions = true;
        try
        {
            onSinkCreated?.Invoke(sink);
            return configuration.Conditional(
                e =>
                {
                    if (e.Level < restrictedToMinimumLevel ||
                        !DnstapEventProperty.TryGetScalar(e, out var value) ||
                        value is not global::Dnstap.Dnstap)
                        return false;
                    sink.RecordSubmission();
                    return true;
                },
                writeTo => writeTo.Sink(sink, options, restrictedToMinimumLevel));
        }
        catch
        {
            sink.Dispose();
            throw;
        }
    }
}
