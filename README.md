# Serilog.Sinks.Dnstap

A [Serilog](https://serilog.net/) sink that forwards typed DNS observations as [DNSTap](https://dnstap.info/) Frame Streams records. TCP forwarding is the primary use; file output supports local capture.

## Requirements

- .NET 10, Serilog 4.x, and a DNSTap-compatible collector for TCP forwarding.
- Access to the authenticated DeltaZulu GitHub Packages feed for the sink and its `Dnstap.NET` dependency (`read:packages`). The repository's `NuGet.Config` illustrates source mapping.
- A trusted network or independently protected transport: this sink does not encrypt TCP connections.

Do not commit package-feed credentials.

## Send records to a TCP collector

The sink accepts **typed** `Dnstap.Dnstap` objects through Serilog's scalar `DnstapRecord` property. It does not parse ordinary text log messages.

```csharp
using System.Net;
using Dnstap;
using Serilog;
using Serilog.Sinks.Dnstap;

var logger = new LoggerConfiguration()
    .Destructure.AsScalar<global::Dnstap.Dnstap>()
    .WriteTo.DnstapTcp("127.0.0.1", 6000)
    .CreateLogger();

var record = new DnstapRecordFactory().Create(new DnsLogEntry
{
    EventType = Message.Types.Type.ClientQuery,
    ClientAddress = IPAddress.Parse("192.0.2.10"),
    ServerAddress = IPAddress.Parse("192.0.2.53"),
    ClientPort = 53000,
    ServerPort = 53,
    Transport = SocketProtocol.Udp,
    Timestamp = DateTimeOffset.UtcNow,
    QueryPacket = Convert.FromHexString(
        "000101000001000000000000076578616d706c6503636f6d0000010001")
});

logger.ForContext(DnstapEventProperty.Name, record)
    .Information("Observed DNS query");

await ((IAsyncDisposable)logger).DisposeAsync();
```

The sink connects on the first eligible event. After a TCP failure, a later event can reopen a session once the reconnect interval has elapsed (five seconds by default). Failed records are **not replayed**. Configure `connectionTimeout`, `operationTimeout` and `reconnectInterval` on `DnstapTcp()` if required.

Records are mutable protobuf objects. Do not modify or reuse one after logging it: Serilog may serialize it asynchronously.

## File output

Replace the TCP destination with `.WriteTo.DnstapFile("dns.fstrm")`.

The parent directory must exist. By default, `DnstapFile()` **overwrites** an existing file (`overwrite: true`); use `overwrite: false` to reject an existing destination. Each file contains one Frame Streams session. There is no built-in append or rotation support. Dispose the logger to emit the final STOP frame; flushing is not a disk-durability guarantee.

## Batching and delivery

The sink uses Serilog's native batching with no additional queue:

```csharp
var batching = new BatchingOptions
{
    BatchSizeLimit = 128,
    BufferingTimeLimit = TimeSpan.FromSeconds(1),
    QueueLimit = 4096,
    RetryTimeLimit = TimeSpan.Zero
};

var logger = new LoggerConfiguration()
    .Destructure.AsScalar<global::Dnstap.Dnstap>()
    .WriteTo.DnstapTcp("127.0.0.1", 6000, batchingOptions: batching)
    .CreateLogger();
```

`RetryTimeLimit` must be zero to avoid replaying frames of uncertain delivery. Native queue overflow may drop records. `WrittenRecords` counts frames accepted by the local stream, **not** records acknowledged or stored by the collector.

## Monitoring and shutdown

Capture the sink using `onSinkCreated` when you need counters:

```csharp
DnstapSink? sink = null;
var logger = new LoggerConfiguration()
    .Destructure.AsScalar<global::Dnstap.Dnstap>()
    .WriteTo.DnstapTcp("127.0.0.1", 6000, onSinkCreated: instance => sink = instance)
    .CreateLogger();
```

| Counter | Meaning |
| --- | --- |
| `SubmittedRecords` | Eligible records submitted through the fluent API |
| `WrittenRecords` | Frames accepted by the local output stream |
| `RejectedRecords` | Invalid or oversized records rejected before I/O |
| `IgnoredEvents` | Unsupported events encountered by direct batch calls |
| `DroppedRecords` | Failed records and inferred native batching queue losses |
| `FailedBatches` | Connection, handshake or write/flush failures |

Some queue drops are counted only at shutdown. Enable Serilog `SelfLog` for transport diagnostics. Failed file and caller-supplied streams cannot automatically reconnect.

Dispose or asynchronously close the logger after the final event (for global logging, `await Log.CloseAndFlushAsync()`). This drains queued events and completes the Frame Streams session. Synchronous disposal may block on I/O. Applications with their own batching can directly use `DnstapSink.CreateTcp()`, `ConnectTcpAsync()`, `CreateFile()`, `EmitBatchAsync()` and `DisposeAsync()`. Caller-supplied streams remain open by default; factory-created destinations are owned by the sink.

## Build from source

After authenticating to the DeltaZulu GitHub Packages feed:

```shell
dotnet restore Serilog.Sinks.Dnstap.slnx --configfile NuGet.Config --locked-mode
dotnet build Serilog.Sinks.Dnstap.slnx -c Release --no-restore
dotnet test tests/Serilog.Sinks.Dnstap.Tests/Serilog.Sinks.Dnstap.Tests.csproj -c Release --no-restore
```

## License

Serilog.Sinks.Dnstap is [Apache-2.0](LICENSE.txt). Its `Dnstap.NET` dependency is GPL-3.0-only. Redistributions of a combined application must comply with applicable GPL obligations.
