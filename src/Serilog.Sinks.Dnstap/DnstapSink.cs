using Serilog.Core;
using Serilog.Events;

namespace Serilog.Sinks.Dnstap;

/// <summary>
/// Writes DNSTap protobuf observations as Frame Streams records to one file-mode stream.
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
    {
        ArgumentNullException.ThrowIfNull(stream);
        _writer = new global::Dnstap.DnstapWriter(stream, leaveOpen: leaveOpen);
    }

    /// <summary>Opens a new Frame Streams file. The sink owns the file handle.</summary>
    public static DnstapSink CreateFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write,
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

    /// <summary>Completes a healthy Frame Streams session and releases the owned stream.</summary>
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
