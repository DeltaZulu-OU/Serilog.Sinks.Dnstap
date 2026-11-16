using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Serilog.Events;
using Serilog.Parsing;

namespace Serilog.Sinks.Dnstap.Tests;

[TestClass]
public sealed class DnstapTcpTests
{
    private static LogEvent ResponseEvent() => new(
        DateTimeOffset.UtcNow, LogEventLevel.Information, null,
        new MessageTemplateParser().Parse("DNS response"),
        new[]
        {
            new LogEventProperty(DnstapEventProperty.Name, new ScalarValue(
                new global::Dnstap.Dnstap
                {
                    Type = global::Dnstap.Dnstap.Types.Type.Message,
                    Message = new global::Dnstap.Message
                    {
                        Type = global::Dnstap.Message.Types.Type.ClientResponse
                    }
                }))
        });

    private static async Task<(bool IsControl, byte[] Payload)> ReceiveFrameAsync(Stream stream)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        var control = length == 0;
        if (control)
        {
            await stream.ReadExactlyAsync(header);
            length = BinaryPrimitives.ReadUInt32BigEndian(header);
        }

        if (length > 65536)
        {
            throw new InvalidDataException("Unexpectedly large test frame.");
        }

        var payload = new byte[checked((int)length)];
        await stream.ReadExactlyAsync(payload);
        return (control, payload);
    }

    private static async Task SendControlAsync(Stream stream, uint type)
    {
        var frame = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4, 4), 4);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8, 4), type);
        await stream.WriteAsync(frame);
    }

    [TestMethod]
    public async Task TcpSessionNegotiatesWritesAndFinishes()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptTcpClientAsync();
                using var stream = accepted.GetStream();
                var ready = await ReceiveFrameAsync(stream);
                Assert.IsTrue(ready.IsControl);
                Assert.AreEqual(4u, BinaryPrimitives.ReadUInt32BigEndian(ready.Payload));

                await SendControlAsync(stream, 1); // ACCEPT

                var start = await ReceiveFrameAsync(stream);
                Assert.IsTrue(start.IsControl);
                Assert.AreEqual(2u, BinaryPrimitives.ReadUInt32BigEndian(start.Payload));

                var data = await ReceiveFrameAsync(stream);
                Assert.IsFalse(data.IsControl);
                var record = global::Dnstap.Dnstap.Parser.ParseFrom(data.Payload);
                Assert.AreEqual(global::Dnstap.Message.Types.Type.ClientResponse, record.Message.Type);

                var stop = await ReceiveFrameAsync(stream);
                Assert.IsTrue(stop.IsControl);
                Assert.AreEqual(3u, BinaryPrimitives.ReadUInt32BigEndian(stop.Payload));

                await SendControlAsync(stream, 5); // FINISH
            });

            var sink = await DnstapSink.ConnectTcpAsync("127.0.0.1", port,
                operationTimeout: TimeSpan.FromSeconds(5));
            try
            {
                await sink.EmitBatchAsync(new[] { ResponseEvent() });
            }
            finally
            {
                await sink.DisposeAsync();
            }

            await server.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task TcpConfigurationRejectsInvalidEndpoint()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            DnstapSink.ConnectTcpAsync(" ", 1));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            DnstapSink.ConnectTcpAsync("localhost", 0));
    }
}
