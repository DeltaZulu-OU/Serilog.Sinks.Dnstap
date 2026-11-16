using System.Buffers.Binary;
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
    public async Task UnrelatedEventsDoNotStartSession()
    {
        using var stream = new MemoryStream();
        var sink = new DnstapSink(stream);
        await sink.EmitBatchAsync(new[] { Event("unrelated"), Event(null) });
        await sink.DisposeAsync();
        Assert.AreEqual(0L, stream.Length);
    }

    [TestMethod]
    public async Task InvalidRecordFaultsSessionPermanently()
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
