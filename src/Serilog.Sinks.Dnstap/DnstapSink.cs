using System.Net.Sockets;
using Serilog.Core;
using Serilog.Events;

namespace Serilog.Sinks.Dnstap;

/// <summary>
/// Writes DNSTap protobuf observations as Frame Streams records to one session.
/// The caller or Serilog batching layer owns batch timing; this sink has no additional queue.
/// </summary>
public sealed class DnstapSink : IBatchedLogEventSink, IDisposable, IAsyncDisposable
{
    private readonly global::Dnstap.DnstapWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;
    private bool _started;
    private bool _faulted;

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
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            // NetworkStream.Dispose closes the underlying socket; the sink owns the stream.
            return new DnstapSink(client.GetStream(), global::Dnstap.DnstapStreamMode.Duplex,
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
                    continue;
                }

                try
                {
                    if (!_started)
                    {
                        await _writer.StartAsync().ConfigureAwait(false);
                        _started = true;
                    }

                    await _writer.WriteAsync(record!).ConfigureAwait(false);
                }
                catch
                {
                    _faulted = true;
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
    /// Completes a healthy Frame Streams session before releasing an owned stream.
    /// File-mode operations are not time-bounded; callers must supply a responsive stream.
    /// Concurrent calls to DisposeAsync are safe but subsequent calls may return early.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
