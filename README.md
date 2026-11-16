# Serilog.Sinks.Dnstap

A [Serilog](https://serilog.net/) sink for forwarding DNS observations in the [DNSTap](https://dnstap.info/) format. It uses [Dnstap.NET](https://github.com/DeltaZulu-OU/Dnstap.NET) to encode protobuf messages and write [Frame Streams](https://github.com/farsightsec/fstrm) to a file or a TCP collector.

**Availability:** This package is intended for internal evaluation and source builds. It is not yet distributed for general package consumption. The `Dnstap.NET` dependency is currently hosted on an authenticated DeltaZulu GitHub Packages feed; cloning this repository alone does not remove that restore requirement.

## Requirements

- .NET 10.
- Serilog 4.x and Dnstap.NET 0.1.0 (resolved through the project dependencies).
- For TCP forwarding, a DNSTap-compatible Frame Streams collector. The TCP connection is unencrypted; use a trusted network or an independently protected transport.
- For local source builds, authorized `read:packages` access to the DeltaZulu GitHub Packages NuGet feed. Do not store access tokens in the repository.

## Usage

This sink accepts **typed DNSTap records**, not ordinary log messages or text-formatted DNS logs. Attach a `Dnstap.Dnstap` protobuf object as the scalar Serilog property `DnstapRecord`. Events without this property are not forwarded.

### Send to a TCP collector

```csharp
using System.Net;
using Dnstap;
using Serilog;
using Serilog.Sinks.Dnstap;

var logger = new LoggerConfiguration()
    .Destructure.AsScalar<global::Dnstap.Dnstap>()
    .WriteTo.DnstapTcp("127.0.0.1", 6000)
    .CreateLogger();

var dnsRecord = DnstapRecordFactory.Create(new DnsLogEntry
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

logger.ForContext(DnstapEventProperty.Name, dnsRecord)
    .Information("Observed DNS query");

await ((IAsyncDisposable)logger).DisposeAsync();
```

The connection is established on the first eligible DNSTap event, not when constructing the logger. The sink initiates a Frame Streams handshake. After a transport failure, a configured TCP sink may establish a **new** session once the reconnect interval has elapsed (five seconds by default). It never replays events that failed or arrived during the outage. An immediately connected or caller-supplied stream cannot reconnect automatically. Configure `connectionTimeout`, `operationTimeout`, and `reconnectInterval` on `DnstapTcp()` when necessary.

### Write to a file

Replace `.WriteTo.DnstapTcp("127.0.0.1", 6000)` in the example above with `.WriteTo.DnstapFile("dns.fstrm")`.

The file must not already exist; `DnstapFile()` refuses to overwrite it by default. Set `overwrite: true` only when replacement is intentional. The parent directory must exist. Each output file contains one Frame Streams session; the sink does not append or rotate files. File batches are flushed to the stream, but an abrupt shutdown may leave the stream without its final STOP frame, and flushing does not guarantee disk durability.

### Batching

The fluent extensions use Serilog's native `BatchingOptions`, rather than adding another queue. The default retry window is zero to avoid replaying frames whose delivery is uncertain.

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

The batch size and buffering interval must be positive. A specified queue limit must be positive, and `RetryTimeLimit` must be zero. Unrelated application events are filtered before entering the sink's batching queue. A bounded queue can still discard DNSTap events when full; it does not block the producing thread to guarantee delivery. For a production TCP collector, select a queue limit and observe `DroppedRecords`, `FailedBatches`, and recovery behavior under load.

**Record lifetime:** Protobuf records are mutable. Do not modify, reuse, or return a record to a pool after logging it. Serilog may serialize it later on its batching worker.

## Monitoring and failure handling

For fluent configuration, retain a reference to the sink through `onSinkCreated`:

```csharp
DnstapSink? dnstapSink = null;

var logger = new LoggerConfiguration()
    .Destructure.AsScalar<global::Dnstap.Dnstap>()
    .WriteTo.DnstapTcp(
        "127.0.0.1", 6000,
        onSinkCreated: sink => dnstapSink = sink)
    .CreateLogger();
```

The sink exposes the following counters:

| Counter | Meaning |
| --- | --- |
| `SubmittedRecords` | Eligible events accepted by fluent registration before Serilog batching |
| `WrittenRecords` | Records written to the local output stream; **not** collector acknowledgments |
| `RejectedRecords` | Invalid or oversized protobuf records rejected locally |
| `IgnoredEvents` | Unsupported events encountered by the direct batched API |
| `DroppedRecords` | Records lost after transport failures, plus unresolved native queue drops reconciled at logger disposal |
| `FailedBatches` | Connection, handshake, write, or file-flush failures |

Native batching queue losses are not necessarily visible immediately. `DroppedRecords` is reconciled against submitted records during logger disposal. Diagnostics for transport and shutdown failures are also emitted through Serilog's `SelfLog`. None of these counters guarantees that a collector stored a record.

Invalid DNSTap records are rejected without permanently faulting a healthy session. A failed file or caller-supplied stream is not reopened. Configure application monitoring accordingly.

## Shutdown and direct integration

Dispose the logger after producing the last event so its batching queue can drain and the Frame Streams session can finish. For the global Serilog logger, prefer `await Log.CloseAndFlushAsync()`. Synchronous shutdown can block while file I/O or the TCP STOP/FINISH exchange completes. Caller-provided file streams have no fixed I/O timeout.

Applications that already own batching and dispatch can use `DnstapSink.CreateFile(path)`, `DnstapSink.CreateTcp(host, port)`, or `await DnstapSink.ConnectTcpAsync(host, port)` directly. Call `EmitBatchAsync()`, drain the application's own queue, then `DisposeAsync()`. Avoid adding a second batching queue.

Stream ownership is explicit: direct constructors leave supplied streams open by default (`leaveOpen: true`), while file and TCP factories own their destinations.

## Building from source

Clone this repository, provide authorized credentials for the `deltazulu-github` source defined in `NuGet.Config`, then run:

```shell
dotnet restore Serilog.Sinks.Dnstap.slnx --configfile NuGet.Config
dotnet build Serilog.Sinks.Dnstap.slnx -c Release --no-restore
dotnet test tests/Serilog.Sinks.Dnstap.Tests/Serilog.Sinks.Dnstap.Tests.csproj -c Release --no-restore
```

A token must have `read:packages` permission and access to the Dnstap.NET package. NuGet authentication is environment-specific; do not commit secrets or credentials to configuration files. See [Dnstap.NET](https://github.com/DeltaZulu-OU/Dnstap.NET) for the protocol library.

## License

Serilog.Sinks.Dnstap is licensed under [Apache-2.0](LICENSE.txt). Its Dnstap.NET dependency is licensed under GPL-3.0-only. Redistributing a combined application requires compliance with the applicable GPL obligations; this repository's Apache-2.0 license does not change the dependency's license.
