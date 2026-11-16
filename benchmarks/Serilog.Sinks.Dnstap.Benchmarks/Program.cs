using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Serilog.Events;
using Serilog.Parsing;
using Serilog.Sinks.Dnstap;

if (args.Length is not 3 || (args[0] != "file" && args[0] != "tcp") ||
    !int.TryParse(args[1], out var count) || count <= 0 ||
    !int.TryParse(args[2], out var batchSize) || batchSize <= 0)
{
    Console.Error.WriteLine("Usage: dotnet run -c Release -- file|tcp COUNT BATCH_SIZE");
    return 2;
}

var template = new MessageTemplateParser().Parse("DNS event");
var dnsRecord = new global::Dnstap.Dnstap
{
    Type = global::Dnstap.Dnstap.Types.Type.Message,
    Message = new global::Dnstap.Message
    {
        Type = global::Dnstap.Message.Types.Type.ClientResponse
    }
};
var logEvent = new LogEvent(DateTimeOffset.UnixEpoch, LogEventLevel.Information,
    null, template,
    new[] { new LogEventProperty(DnstapEventProperty.Name, new ScalarValue(dnsRecord)) });
var fullBatch = Enumerable.Repeat(logEvent, batchSize).ToArray();

var path = Path.Combine(Path.GetTempPath(), $"serilog-dnstap-benchmark-{Guid.NewGuid():N}.fstrm");
TcpListener? listener = null;
Task<int>? receiver = null;
DnstapSink? sink = null;
long outputBytes = 0;
try
{
    if (args[0] == "file")
    {
        sink = DnstapSink.CreateFile(path);
    }
    else
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = listener;
        receiver = ReceiveAsync(server);
        sink = DnstapSink.CreateTcp("127.0.0.1",
            ((IPEndPoint)server.LocalEndpoint).Port,
            connectionTimeout: TimeSpan.FromSeconds(5),
            operationTimeout: TimeSpan.FromSeconds(15));
    }

    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    var elapsed = Stopwatch.StartNew();
    for (var remaining = count; remaining > 0;)
    {
        var n = Math.Min(batchSize, remaining);
        await sink.EmitBatchAsync(n == batchSize ? fullBatch : fullBatch[..n]);
        remaining -= n;
    }
    await sink.DisposeAsync();
    elapsed.Stop();
    var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

    outputBytes = args[0] == "file"
        ? new FileInfo(path).Length
        : await receiver!.WaitAsync(TimeSpan.FromSeconds(20));
    if (sink.WrittenRecords != count)
        throw new InvalidOperationException("Record accounting mismatch.");

    Console.WriteLine($"mode={args[0]} count={count} batch={batchSize} seconds={elapsed.Elapsed.TotalSeconds:F3} records_per_sec={count / elapsed.Elapsed.TotalSeconds:F1} allocated_bytes_per_record={(double)allocated / count:F1} output_bytes={outputBytes} failed_batches={sink.FailedBatches}");
    return 0;
}
finally
{
    if (sink is not null)
        await sink.DisposeAsync();
    listener?.Stop();
    if (File.Exists(path))
        File.Delete(path);
}

static async Task<int> ReceiveAsync(TcpListener listener)
{
    using var client = await listener.AcceptTcpClientAsync();
    using var stream = client.GetStream();
    var totalBytes = 0;
    var frame = await ReadFrameAsync(stream);
    if (!frame.control || BinaryPrimitives.ReadUInt32BigEndian(frame.payload) != 4u)
        throw new InvalidDataException("Expected READY.");
    await SendControlAsync(stream, 1);
    frame = await ReadFrameAsync(stream);
    if (!frame.control || BinaryPrimitives.ReadUInt32BigEndian(frame.payload) != 2u)
        throw new InvalidDataException("Expected START.");

    while (true)
    {
        frame = await ReadFrameAsync(stream);
        if (frame.control)
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(frame.payload) != 3u)
                throw new InvalidDataException("Expected STOP.");
            await SendControlAsync(stream, 5);
            break;
        }
        totalBytes += frame.payload.Length + 4;
    }
    return totalBytes;
}

static async Task<(bool control, byte[] payload)> ReadFrameAsync(Stream stream)
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
    if (length > 1_048_576)
        throw new InvalidDataException("Oversized frame.");
    var bytes = new byte[checked((int)length)];
    await stream.ReadExactlyAsync(bytes);
    return (control, bytes);
}

static async Task SendControlAsync(Stream stream, uint type)
{
    var frame = new byte[12];
    BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4, 4), 4);
    BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8, 4), type);
    await stream.WriteAsync(frame);
}
