using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Serilog;
using Serilog.Configuration;
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
    public async Task DeferredTcpConfigurationDoesNotConnectBeforeFirstRecord()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await using (var sink = DnstapSink.CreateTcp("127.0.0.1", port))
            {
                await sink.EmitBatchAsync(Array.Empty<LogEvent>());
            }

            Assert.IsFalse(listener.Pending(), "Empty batches must not open TCP sockets.");
            using (var logger = new LoggerConfiguration()
                .WriteTo.DnstapTcp("127.0.0.1", port).CreateLogger())
            {
                logger.Information("A normal, unrelated log event");
            }
            Assert.IsFalse(listener.Pending(), "Unrelated events must not open TCP sockets.");
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task DeferredTcpSessionNegotiatesOneConnection()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptTcpClientAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5));
                using var stream = accepted.GetStream();
                var ready = await ReceiveFrameAsync(stream);
                Assert.IsTrue(ready.IsControl);
                Assert.AreEqual(4u, BinaryPrimitives.ReadUInt32BigEndian(ready.Payload));
                await SendControlAsync(stream, 1);

                var start = await ReceiveFrameAsync(stream);
                Assert.AreEqual(2u, BinaryPrimitives.ReadUInt32BigEndian(start.Payload));

                for (var i = 0; i < 2; i++)
                {
                    var data = await ReceiveFrameAsync(stream);
                    Assert.IsFalse(data.IsControl);
                    Assert.AreEqual(global::Dnstap.Message.Types.Type.ClientResponse,
                        global::Dnstap.Dnstap.Parser.ParseFrom(data.Payload).Message.Type);
                }

                var stop = await ReceiveFrameAsync(stream);
                Assert.AreEqual(3u, BinaryPrimitives.ReadUInt32BigEndian(stop.Payload));
                await SendControlAsync(stream, 5);
            });

            await using (var sink = DnstapSink.CreateTcp("127.0.0.1", port,
                connectionTimeout: TimeSpan.FromSeconds(5),
                operationTimeout: TimeSpan.FromSeconds(5)))
            {
                Assert.IsFalse(listener.Pending());
                await sink.EmitBatchAsync(new[] { ResponseEvent() });
                await sink.EmitBatchAsync(new[] { ResponseEvent() });
            }

            await server.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(listener.Pending(), "A successful session must not reconnect.");
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task FluentTcpLoggerWritesWithoutConnectingDuringConstruction()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptTcpClientAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5));
                using var stream = accepted.GetStream();
                var ready = await ReceiveFrameAsync(stream);
                Assert.AreEqual(4u, BinaryPrimitives.ReadUInt32BigEndian(ready.Payload));
                await SendControlAsync(stream, 1);
                var start = await ReceiveFrameAsync(stream);
                Assert.AreEqual(2u, BinaryPrimitives.ReadUInt32BigEndian(start.Payload));
                var data = await ReceiveFrameAsync(stream);
                Assert.IsFalse(data.IsControl);
                Assert.AreEqual(global::Dnstap.Message.Types.Type.ClientResponse,
                    global::Dnstap.Dnstap.Parser.ParseFrom(data.Payload).Message.Type);
                var stop = await ReceiveFrameAsync(stream);
                Assert.AreEqual(3u, BinaryPrimitives.ReadUInt32BigEndian(stop.Payload));
                await SendControlAsync(stream, 5);
            });

            var logger = new LoggerConfiguration()
                .Destructure.AsScalar<global::Dnstap.Dnstap>()
                .WriteTo.DnstapTcp("127.0.0.1", port,
                    connectionTimeout: TimeSpan.FromSeconds(5),
                    operationTimeout: TimeSpan.FromSeconds(5))
                .CreateLogger();
            try
            {
                Assert.IsFalse(listener.Pending());
                var record = new global::Dnstap.Dnstap
                {
                    Type = global::Dnstap.Dnstap.Types.Type.Message,
                    Message = new global::Dnstap.Message
                    {
                        Type = global::Dnstap.Message.Types.Type.ClientResponse
                    }
                };
                logger.ForContext(DnstapEventProperty.Name, record)
                    .Information("Observed DNS response");
            }
            finally
            {
                logger.Dispose();
            }

            await server.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task CollectorHandshakeTimeoutIsTerminalWithoutReconnect()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await using var sink = DnstapSink.CreateTcp("127.0.0.1", port,
                operationTimeout: TimeSpan.FromMilliseconds(200));
            var emission = sink.EmitBatchAsync(new[] { ResponseEvent() });
            using var accepted = await listener.AcceptTcpClientAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            // The TCP collector never sends ACCEPT, so the writer times out.
            await Assert.ThrowsExactlyAsync<TimeoutException>(() => emission);
            await sink.EmitBatchAsync(new[] { ResponseEvent() });
            Assert.AreEqual(2L, sink.DroppedRecords);
            Assert.AreEqual(1L, sink.FailedBatches);
            Assert.IsFalse(listener.Pending(), "A session must not reconnect before the cooldown.");
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task RefusedConnectionCanRecoverWithoutReplayingFailedEvents()
    {
        var temporary = new TcpListener(IPAddress.Loopback, 0);
        temporary.Start();
        var port = ((IPEndPoint)temporary.LocalEndpoint).Port;
        temporary.Stop();

        await using var sink = DnstapSink.CreateTcp("127.0.0.1", port,
            connectionTimeout: TimeSpan.FromSeconds(2),
            operationTimeout: TimeSpan.FromSeconds(3),
            reconnectInterval: TimeSpan.FromMilliseconds(50));

        try
        {
            await sink.EmitBatchAsync(new[] { ResponseEvent() });
            Assert.Fail("Expected a connection refusal or timeout.");
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException)
        {
            // Connection refusal is platform-dependent: Windows may report a timeout.
        }
        Assert.AreEqual(1L, sink.DroppedRecords);
        await sink.EmitBatchAsync(new[] { ResponseEvent() });
        Assert.AreEqual(2L, sink.DroppedRecords);
        Assert.AreEqual(1L, sink.FailedBatches);

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            var server = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptTcpClientAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5));
                using var stream = accepted.GetStream();
                var ready = await ReceiveFrameAsync(stream);
                Assert.AreEqual(4u, BinaryPrimitives.ReadUInt32BigEndian(ready.Payload));
                await SendControlAsync(stream, 1);
                var start = await ReceiveFrameAsync(stream);
                Assert.AreEqual(2u, BinaryPrimitives.ReadUInt32BigEndian(start.Payload));
                var data = await ReceiveFrameAsync(stream);
                Assert.IsFalse(data.IsControl);
                Assert.AreEqual(global::Dnstap.Message.Types.Type.ClientResponse,
                    global::Dnstap.Dnstap.Parser.ParseFrom(data.Payload).Message.Type);
                var stop = await ReceiveFrameAsync(stream);
                Assert.AreEqual(3u, BinaryPrimitives.ReadUInt32BigEndian(stop.Payload));
                await SendControlAsync(stream, 5);
            });

            await Task.Delay(100);
            await sink.EmitBatchAsync(new[] { ResponseEvent() });
            Assert.AreEqual(1L, sink.WrittenRecords);
            Assert.AreEqual(2L, sink.DroppedRecords);
            await sink.DisposeAsync();
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task SlowHandshakeWithinDeadlineSucceedsWithoutReconnecting()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5));
                using var stream = client.GetStream();
                var ready = await ReceiveFrameAsync(stream);
                Assert.AreEqual(4u, BinaryPrimitives.ReadUInt32BigEndian(ready.Payload));
                await Task.Delay(TimeSpan.FromMilliseconds(200));
                await SendControlAsync(stream, 1);
                var start = await ReceiveFrameAsync(stream);
                Assert.AreEqual(2u, BinaryPrimitives.ReadUInt32BigEndian(start.Payload));
                var data = await ReceiveFrameAsync(stream);
                Assert.IsFalse(data.IsControl);
                var stop = await ReceiveFrameAsync(stream);
                Assert.AreEqual(3u, BinaryPrimitives.ReadUInt32BigEndian(stop.Payload));
                await SendControlAsync(stream, 5);
            });

            await using (var sink = DnstapSink.CreateTcp("127.0.0.1", port,
                operationTimeout: TimeSpan.FromSeconds(3)))
            {
                await sink.EmitBatchAsync(new[] { ResponseEvent() })
                    .WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual(1L, sink.WrittenRecords);
                Assert.AreEqual(0L, sink.FailedBatches);
            }

            await server.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(listener.Pending());
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public void FluentTcpConfigurationRejectsReplayAndInvalidTimeouts()
    {
        var config = new LoggerConfiguration();
        Assert.ThrowsExactly<ArgumentException>(() =>
            config.WriteTo.DnstapTcp("localhost", 53,
                new BatchingOptions { RetryTimeLimit = TimeSpan.FromSeconds(1) }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            config.WriteTo.DnstapTcp("localhost", 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            config.WriteTo.DnstapTcp("localhost", 53, connectionTimeout: TimeSpan.Zero));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            config.WriteTo.DnstapTcp("localhost", 53, operationTimeout: TimeSpan.Zero));
    }

    [TestMethod]
    public void InvalidNativeBatchSettingsAreRejectedForTcp()
    {
        var options = new BatchingOptions { RetryTimeLimit = TimeSpan.Zero, QueueLimit = 0 };
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new LoggerConfiguration().WriteTo.DnstapTcp("127.0.0.1", 53, options));
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
