using System.Buffers.Binary;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Serilog.Sinks.Dnstap.Tests;

[TestClass]
public sealed class DnstapSinkTests
{
    private static LogEvent Event(object? value) => new(
        DateTimeOffset.UtcNow, LogEventLevel.Information, null,
        new MessageTemplateParser().Parse("DNS event"),
        new[] { new LogEventProperty(DnstapEventProperty.Name, new ScalarValue(value)) });

    private static global::Dnstap.Dnstap Record() => new()
    {
        Type = global::Dnstap.Dnstap.Types.Type.Message,
        Message = new global::Dnstap.Message
        {
            Type = global::Dnstap.Message.Types.Type.ClientResponse
        }
    };

    private static List<byte[]> ReadFrames(byte[] data)
    {
        var frames = new List<byte[]>();
        var offset = 0;
        while (offset < data.Length)
        {
            Assert.IsGreaterThanOrEqualTo(4, data.Length - offset, "Truncated frame header.");
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4)));
            offset += 4;
            if (length == 0)
            {
                Assert.IsGreaterThanOrEqualTo(4, data.Length - offset, "Truncated control length.");
                length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4)));
                offset += 4;
                Assert.IsGreaterThanOrEqualTo(4, length, "Invalid control payload.");
                Assert.IsGreaterThanOrEqualTo(length, data.Length - offset, "Truncated control payload.");
                var payload = new byte[length + 1];
                payload[0] = 0; // control marker
                data.AsSpan(offset, length).CopyTo(payload.AsSpan(1));
                frames.Add(payload);
            }
            else
            {
                Assert.IsGreaterThanOrEqualTo(length, data.Length - offset, "Truncated data payload.");
                frames.Add(data.AsSpan(offset, length).ToArray());
            }

            offset += length;
        }
        return frames;
    }

    [TestMethod]
    public async Task TwoRecordsAreWrittenInOneFileSession()
    {
        using var stream = new MemoryStream();
        var sink = new DnstapSink(stream);
        await sink.EmitBatchAsync(new[] { Event(Record()), Event(Record()) });
        await sink.DisposeAsync();
        var frames = ReadFrames(stream.ToArray());
        Assert.HasCount(4, frames);
        Assert.AreEqual(2L, sink.WrittenRecords);
        Assert.AreEqual(0L, sink.IgnoredEvents);
        Assert.AreEqual(0L, sink.FailedBatches);
        Assert.AreEqual((byte)0, frames[0][0]);
        Assert.AreEqual(2u, BinaryPrimitives.ReadUInt32BigEndian(frames[0].AsSpan(1, 4))); // START
        Assert.AreEqual((byte)0, frames[3][0]);
        Assert.AreEqual(3u, BinaryPrimitives.ReadUInt32BigEndian(frames[3].AsSpan(1, 4))); // STOP
        for (var i = 1; i <= 2; i++)
        {
            var parsed = global::Dnstap.Dnstap.Parser.ParseFrom(frames[i]);
            Assert.AreEqual(global::Dnstap.Message.Types.Type.ClientResponse, parsed.Message.Type);
        }
    }

    [TestMethod]
    public async Task Dnstap020FactoryRecordRetainsMetadataAndBothTimestamps()
    {
        using var stream = new MemoryStream();
        await using (var sink = new DnstapSink(stream))
        {
            var factory = new global::Dnstap.DnstapRecordFactory("serilog-upgrade"u8.ToArray());
            var record = factory.Create(new global::Dnstap.DnsLogEntry
            {
                EventType = global::Dnstap.Message.Types.Type.ClientResponse,
                QueryTimestamp = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
                ResponseTimestamp = DateTimeOffset.FromUnixTimeSeconds(1_700_000_001),
                QueryZone = Convert.FromHexString("076578616d706c6503636f6d00"),
                ResponsePacket = new byte[] { 0, 0xff, 0xc0, 0x0c }
            });
            await sink.EmitBatchAsync(new[] { Event(record) });
            Assert.AreEqual(1L, sink.WrittenRecords);
            Assert.AreEqual(0L, sink.RejectedRecords);
        }
        var frames = ReadFrames(stream.ToArray());
        Assert.HasCount(3, frames);
        var decoded = global::Dnstap.Dnstap.Parser.ParseFrom(frames[1]);
        Assert.AreEqual("serilog-upgrade", decoded.Identity.ToStringUtf8());
        Assert.AreEqual(1_700_000_000UL, decoded.Message.QueryTimeSec);
        Assert.AreEqual(1_700_000_001UL, decoded.Message.ResponseTimeSec);
        Assert.AreSequenceEqual(Convert.FromHexString("076578616d706c6503636f6d00"), decoded.Message.QueryZone.ToByteArray());
        Assert.AreSequenceEqual(new byte[] { 0, 0xff, 0xc0, 0x0c }, decoded.Message.ResponseMessage.ToByteArray());
    }

    [TestMethod]
    public async Task UnrelatedEventsDoNotStartSession()
    {
        using var stream = new MemoryStream();
        var sink = new DnstapSink(stream);
        await sink.EmitBatchAsync(new[] { Event("unrelated"), Event(null) });
        await sink.DisposeAsync();
        Assert.AreEqual(0L, stream.Length);
        Assert.AreEqual(2L, sink.IgnoredEvents);
        Assert.AreEqual(0L, sink.WrittenRecords);
    }

    [TestMethod]
    public async Task InvalidRecordIsSkippedWithoutFaultingSession()
    {
        using var stream = new MemoryStream();
        var sink = new DnstapSink(stream);
        var invalid = Record();
        invalid.Message.Type = (global::Dnstap.Message.Types.Type)int.MaxValue;
        // Unknown message kinds are filtered, not forwarded and not treated as I/O faults.
        await sink.EmitBatchAsync(new[] { Event(invalid) });
        await sink.EmitBatchAsync(new[] { Event(Record()) });
        await sink.DisposeAsync();
        Assert.HasCount(3, ReadFrames(stream.ToArray()));
        Assert.AreEqual(1L, sink.IgnoredEvents);
        Assert.AreEqual(1L, sink.WrittenRecords);
    }

    [TestMethod]
    public async Task UninitializedRecordIsRejectedWithoutFaultingSession()
    {
        using var stream = new MemoryStream();
        await using var sink = new DnstapSink(stream);
        var uninitialized = new global::Dnstap.Dnstap
        {
            Message = new global::Dnstap.Message
            {
                Type = global::Dnstap.Message.Types.Type.ClientResponse
            }
        };
        await sink.EmitBatchAsync(new[] { Event(uninitialized), Event(Record()) });
        Assert.AreEqual(1L, sink.RejectedRecords);
        Assert.AreEqual(1L, sink.WrittenRecords);
        Assert.AreEqual(0L, sink.FailedBatches);
    }

    [TestMethod]
    public async Task OversizedRecordIsRejectedWithoutFaultingSession()
    {
        using var stream = new MemoryStream();
        await using var sink = new DnstapSink(stream);
        var oversized = Record();
        oversized.Message.QueryMessage = Google.Protobuf.ByteString.CopyFrom(new byte[100_000]);
        await sink.EmitBatchAsync(new[] { Event(oversized), Event(Record()) });
        Assert.AreEqual(1L, sink.RejectedRecords);
        Assert.AreEqual(1L, sink.WrittenRecords);
        Assert.AreEqual(0L, sink.FailedBatches);
    }

    private sealed class FlushObservedStream : MemoryStream
    {
        public int FlushCount { get; private set; }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            return base.FlushAsync(cancellationToken);
        }
    }

    [TestMethod]
    public async Task CompletedFileBatchFlushesBeforeSinkDisposal()
    {
        using var stream = new FlushObservedStream();
        var sink = new DnstapSink(stream);
        await sink.EmitBatchAsync(new[] { Event(Record()) });
        Assert.IsGreaterThan(0, stream.FlushCount,
            "File-mode records should be flushed before the STOP frame is written.");
        Assert.AreEqual(1L, sink.WrittenRecords);
        await sink.DisposeAsync();
        Assert.HasCount(3, ReadFrames(stream.ToArray()));
    }

    private sealed class FailedOutputStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            throw new IOException("Simulated output failure.");
    }

    [TestMethod]
    public async Task IoFailureFaultsSessionPermanently()
    {
        using var stream = new FailedOutputStream();
        var sink = new DnstapSink(stream);
        await Assert.ThrowsExactlyAsync<IOException>(() =>
            sink.EmitBatchAsync(new[] { Event(Record()) }));
        await sink.EmitBatchAsync(new[] { Event(Record()) });
        Assert.AreEqual(1L, sink.FailedBatches);
        Assert.AreEqual(0L, sink.WrittenRecords);
        Assert.AreEqual(2L, sink.DroppedRecords);
        await sink.DisposeAsync();
    }

    [TestMethod]
    public async Task FailedWriteAccountsForRemainingRecordsWithoutReplayingThem()
    {
        using var stream = new FailedOutputStream();
        await using var sink = new DnstapSink(stream) { SuppressTransportExceptions = true };

        // A failed write drops that record and any remaining DNS candidates.
        // An unrelated trailing event must not be misclassified as a lost record.
        await sink.EmitBatchAsync(new[]
        {
            Event(Record()), Event(Record()), Event("unrelated"), Event(Record())
        });
        Assert.AreEqual(3L, sink.DroppedRecords);
        Assert.AreEqual(1L, sink.FailedBatches);
        Assert.AreEqual(0L, sink.WrittenRecords);
        Assert.AreEqual(0L, sink.IgnoredEvents);

        // A permanently faulted caller-supplied stream rejects subsequent batches.
        await sink.EmitBatchAsync(new[] { Event(Record()), Event("unrelated") });
        Assert.AreEqual(4L, sink.DroppedRecords);
        Assert.AreEqual(1L, sink.FailedBatches);
    }

    private sealed class FailingCompletionStream : MemoryStream
    {
        public bool FailWrites { get; set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            FailWrites
                ? ValueTask.FromException(new IOException("Simulated STOP failure."))
                : base.WriteAsync(buffer, cancellationToken);
    }

    private sealed class DisposalObserverSink : ILogEventSink, IDisposable
    {
        public bool IsDisposed { get; private set; }
        public void Emit(LogEvent logEvent) { }
        public void Dispose() => IsDisposed = true;
    }

    [TestMethod]
    public async Task DirectDisposalPropagatesCompletionFailure()
    {
        using var stream = new FailingCompletionStream();
        var sink = new DnstapSink(stream);
        await sink.EmitBatchAsync(new[] { Event(Record()) });
        stream.FailWrites = true;

        await Assert.ThrowsExactlyAsync<IOException>(() => sink.DisposeAsync().AsTask());
        Assert.AreEqual(1L, sink.WrittenRecords);
    }

    [TestMethod]
    public async Task FluentDisposalFailureDoesNotSkipSubsequentSink()
    {
        using var stream = new FailingCompletionStream();
        var sink = new DnstapSink(stream) { SuppressTransportExceptions = true };
        var observer = new DisposalObserverSink();
        await sink.EmitBatchAsync(new[] { Event(Record()) });
        stream.FailWrites = true;

        var logger = new LoggerConfiguration()
            .WriteTo.Sink(sink, new BatchingOptions { RetryTimeLimit = TimeSpan.Zero })
            .WriteTo.Sink(observer)
            .CreateLogger();

        logger.Dispose();
        Assert.IsTrue(observer.IsDisposed, "A failed DNSTap STOP must not skip other sinks.");
        Assert.AreEqual(1L, sink.WrittenRecords);
        Assert.AreEqual(0L, sink.FailedBatches);
    }

    [TestMethod]
    public async Task MissingNestedMessageDoesNotFaultFollowingRecords()
    {
        using var stream = new MemoryStream();
        await using var sink = new DnstapSink(stream);
        var malformed = new global::Dnstap.Dnstap
        {
            Type = global::Dnstap.Dnstap.Types.Type.Message
        };

        await sink.EmitBatchAsync(new[] { Event(malformed), Event(Record()) });

        Assert.AreEqual(1L, sink.IgnoredEvents);
        Assert.AreEqual(1L, sink.WrittenRecords);
        Assert.AreEqual(0L, sink.FailedBatches);
    }

    [TestMethod]
    public async Task DisposalPreventsSubsequentWrites()
    {
        using var stream = new MemoryStream();
        var sink = new DnstapSink(stream);
        await sink.DisposeAsync();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => sink.EmitBatchAsync(new[] { Event(Record()) }));
    }

    [TestMethod]
    public async Task DefaultStreamOwnershipLeavesCallerStreamWritable()
    {
        using var stream = new MemoryStream();
        var sink = new DnstapSink(stream);
        await sink.EmitBatchAsync(new[] { Event(Record()) });
        await sink.DisposeAsync();
        Assert.IsTrue(stream.CanWrite);
        Assert.HasCount(3, ReadFrames(stream.ToArray()));
    }

    [TestMethod]
    public async Task ExplicitStreamOwnershipClosesCallerStream()
    {
        using var stream = new MemoryStream();
        var sink = new DnstapSink(stream, leaveOpen: false);
        await sink.EmitBatchAsync(new[] { Event(Record()) });
        await sink.DisposeAsync();
        Assert.IsFalse(stream.CanWrite);
    }

    private sealed class PausedWriteStream : MemoryStream
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _paused;

        public Task Entered => _entered.Task;
        public void Resume() => _resume.TrySetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _paused, 1) == 0)
            {
                _entered.TrySetResult();
                await _resume.Task.WaitAsync(cancellationToken);
            }

            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    [TestMethod]
    public async Task ConcurrentDisposalWaitsForActiveWriteAndProducesOneStop()
    {
        using var stream = new PausedWriteStream();
        var sink = new DnstapSink(stream);
        var emission = sink.EmitBatchAsync(new[] { Event(Record()) });
        try
        {
            await stream.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            var firstDisposal = sink.DisposeAsync().AsTask();
            var secondDisposal = sink.DisposeAsync().AsTask();
            Assert.IsFalse(firstDisposal.IsCompleted);
            Assert.IsFalse(secondDisposal.IsCompleted);
            stream.Resume();
            await emission.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(firstDisposal, secondDisposal).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.HasCount(3, ReadFrames(stream.ToArray()));
            Assert.AreEqual(1L, sink.WrittenRecords);
        }
        finally
        {
            stream.Resume();
            await sink.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task BlockedWriterAppliesBackpressureToConcurrentDirectBatches()
    {
        using var stream = new PausedWriteStream();
        await using var sink = new DnstapSink(stream);
        var first = sink.EmitBatchAsync(new[] { Event(Record()) });
        try
        {
            await stream.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = Enumerable.Range(0, 8)
                .Select(_ => sink.EmitBatchAsync(new[] { Event(Record()) }))
                .ToArray();
            Assert.IsTrue(pending.All(task => !task.IsCompleted),
                "Batches should wait for the active writer rather than be silently dropped.");
            Assert.AreEqual(0L, sink.WrittenRecords);
            stream.Resume();
            await Task.WhenAll(pending.Append(first)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(9L, sink.WrittenRecords);
            Assert.AreEqual(0L, sink.FailedBatches);
        }
        finally
        {
            stream.Resume();
        }
    }

    [TestMethod]
    public async Task ConcurrentBatchesAreSerializedAndCounted()
    {
        using var stream = new MemoryStream();
        await using var sink = new DnstapSink(stream);
        await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => sink.EmitBatchAsync(new[] { Event(Record()) })));
        Assert.AreEqual(4L, sink.WrittenRecords);
        Assert.AreEqual(0L, sink.FailedBatches);
    }

    [TestMethod]
    public async Task RepeatedDisposeIsAllowed()
    {
        using var stream = new MemoryStream();
        var sink = new DnstapSink(stream);
        await sink.EmitBatchAsync(new[] { Event(Record()) });
        await sink.DisposeAsync();
        await sink.DisposeAsync();
        Assert.HasCount(3, ReadFrames(stream.ToArray()));
    }

    [TestMethod]
    public async Task EmptyBatchIsHarmless()
    {
        using var stream = new MemoryStream();
        var sink = new DnstapSink(stream);
        await sink.EmitBatchAsync(Array.Empty<LogEvent>());
        await sink.OnEmptyBatchAsync();
        await sink.DisposeAsync();
        Assert.AreEqual(0L, stream.Length);
    }
}
