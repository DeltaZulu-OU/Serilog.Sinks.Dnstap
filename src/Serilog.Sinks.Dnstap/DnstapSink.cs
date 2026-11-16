using System.Net.Sockets;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;

namespace Serilog.Sinks.Dnstap;

/// <summary>
/// Writes DNSTap protobuf observations as Frame Streams records to one session.
/// The caller or Serilog batching layer owns batch timing; this sink has no additional queue.
/// </summary>
public sealed class DnstapSink : IBatchedLogEventSink, IDisposable, IAsyncDisposable
{
    private global::Dnstap.DnstapWriter? _writer;
    private readonly string? _tcpHost;
    private readonly int _tcpPort;
    private readonly TimeSpan? _operationTimeout;
    private readonly TimeSpan _connectionTimeout;
    private static readonly TimeSpan DefaultConnectionTimeout = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;
    private bool _started;
    private bool _faulted;
    private long _writtenRecords;
    private long _ignoredEvents;
    private long _failedBatches;
    private readonly object _disposeSync = new();
    private Task? _disposeTask;

    /// <summary>Records whose frames were accepted by the local stream, not acknowledged by a collector.</summary>
    public long WrittenRecords => Interlocked.Read(ref _writtenRecords);

    /// <summary>Log events without a supported DNSTap record; never forwarded.</summary>
    public long IgnoredEvents => Interlocked.Read(ref _ignoredEvents);

    /// <summary>Batches that failed during connection, handshake or write.</summary>
    public long FailedBatches => Interlocked.Read(ref _failedBatches);

    /// <param name="stream">Writable destination for a complete Frame Streams file session.</param>
    /// <param name="leaveOpen">Whether the supplied stream remains open after sink disposal.</param>
    public DnstapSink(Stream stream, bool leaveOpen = true)
        : this(stream, global::Dnstap.DnstapStreamMode.File, leaveOpen)
    {
    }

    /// <summary>Uses a caller-supplied file or connected duplex stream.</summary>
    public DnstapSink(Stream stream, global::Dnstap.DnstapStreamMode mode,
        bool leaveOpen = true, TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _writer = new global::Dnstap.DnstapWriter(stream, mode,
            leaveOpen: leaveOpen, operationTimeout: operationTimeout);
    }

    private DnstapSink(string host, int port, TimeSpan connectionTimeout, TimeSpan? operationTimeout)
    {
        _tcpHost = host;
        _tcpPort = port;
        _connectionTimeout = connectionTimeout;
        _operationTimeout = operationTimeout;
    }

    private static void ValidateTcpEndpoint(string host, int port, TimeSpan connectionTimeout, TimeSpan? operationTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        if (connectionTimeout <= TimeSpan.Zero || connectionTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(connectionTimeout));
        }

        if (operationTimeout is { } timeout &&
            (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue))
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }
    }

    /// <summary>
    /// Configures one TCP session, connecting on the first eligible event rather than
    /// during logger construction. No implicit reconnection or batch replay is performed.
    /// </summary>
    public static DnstapSink CreateTcp(string host, int port,
        TimeSpan? connectionTimeout = null, TimeSpan? operationTimeout = null)
    {
        var deadline = connectionTimeout ?? DefaultConnectionTimeout;
        ValidateTcpEndpoint(host, port, deadline, operationTimeout);
        return new DnstapSink(host, port, deadline, operationTimeout);
    }

    /// <summary>Opens a new Frame Streams file, refusing to replace existing data by default.</summary>
    /// <param name="path">Destination file; parent directory must already exist.</param>
    /// <param name="overwrite">Explicitly allow truncating an existing file. Never appends a second session.</param>
    public static DnstapSink CreateFile(string path, bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var stream = new FileStream(path, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write,
            FileShare.Read, bufferSize: 65536, options: FileOptions.Asynchronous);
        try
        {
            return new DnstapSink(stream, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Connects once to a TCP collector and owns the resulting socket stream.
    /// No automatic reconnect or replay is attempted after failure.
    /// </summary>
    public static async Task<DnstapSink> ConnectTcpAsync(
        string host, int port, TimeSpan? operationTimeout = null,
        CancellationToken cancellationToken = default,
        TimeSpan? connectionTimeout = null)
    {
        var deadline = connectionTimeout ?? DefaultConnectionTimeout;
        ValidateTcpEndpoint(host, port, deadline, operationTimeout);
        var writer = await OpenTcpWriterAsync(host, port, deadline, operationTimeout, cancellationToken)
            .ConfigureAwait(false);
        return new DnstapSink(writer);
    }

    private DnstapSink(global::Dnstap.DnstapWriter writer) => _writer = writer;

    private static async Task<global::Dnstap.DnstapWriter> OpenTcpWriterAsync(
        string host, int port, TimeSpan connectionTimeout,
        TimeSpan? operationTimeout, CancellationToken cancellationToken = default)
    {
        using var timeout = new CancellationTokenSource(connectionTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token, cancellationToken);
        var client = new TcpClient();
        try
        {
            try
            {
                await client.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (
                timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("DNSTap TCP connection timed out.", ex);
            }

            // The owned NetworkStream closes the socket when the writer is disposed.
            return new global::Dnstap.DnstapWriter(client.GetStream(),
                global::Dnstap.DnstapStreamMode.Duplex,
                leaveOpen: false, operationTimeout: operationTimeout);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Skips unrelated Serilog events. Errors during initialization or forwarding are terminal:
    /// a partially written Frame Streams session must not be replayed or silently reopened.
    /// </summary>
    public async Task EmitBatchAsync(IReadOnlyCollection<LogEvent> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_faulted)
            {
                throw new InvalidOperationException("DNSTap session is faulted; reconnect or recreate explicitly.");
            }

            foreach (var logEvent in batch)
            {
                if (!DnstapEventProperty.TryGetRecord(logEvent, out var record))
                {
                    Interlocked.Increment(ref _ignoredEvents);
                    continue;
                }

                try
                {
                    if (_writer is null)
                    {
                        _writer = await OpenTcpWriterAsync(_tcpHost!, _tcpPort,
                            _connectionTimeout, _operationTimeout).ConfigureAwait(false);
                    }

                    if (!_started)
                    {
                        await _writer.StartAsync().ConfigureAwait(false);
                        _started = true;
                    }

                    await _writer.WriteAsync(record!).ConfigureAwait(false);
                    Interlocked.Increment(ref _writtenRecords);
                }
                catch (Exception ex)
                {
                    _faulted = true;
                    Interlocked.Increment(ref _failedBatches);
                    SelfLog.WriteLine("DNSTap sink entered terminal failure after {0} locally written records: {1}",
                        WrittenRecords, ex);
                    throw;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task OnEmptyBatchAsync() => Task.CompletedTask;

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// Completes the Frame Streams session and releases owned resources.
    /// Concurrent disposals await the same operation, including any failure.
    /// File-mode I/O may stall indefinitely when the supplied stream does not make progress.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_writer is not null)
            {
                await _writer.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            SelfLog.WriteLine("DNSTap sink disposal failed: {0}", ex);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }
}
