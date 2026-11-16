# Serilog.Sinks.Dnstap

A Serilog integration for DNS telemetry using [Dnstap.NET](https://github.com/DeltaZulu-OU/Dnstap.NET).

## Status

The current library supports typed `DnstapRecord` events, file-mode Frame Streams output, and a single TCP collector session. Fluent TCP registration defers connection until the first valid DNSTap event. TCP reconnect and batch replay are deliberately unsupported. Independent collector interoperability and deployment-level stress testing remain necessary.

## Serilog integration

Configure a logger with `WriteTo.DnstapFile(path)` to write a new Frame Streams file. Existing files are preserved: the default creation mode fails if the path already exists. To explicitly replace an existing file, set `overwrite: true`. The sink does not append another session to an existing capture. Parent directories must exist. For TCP, use `WriteTo.DnstapTcp(host, port, connectionTimeout: ..., operationTimeout: ...)`. Constructing a logger does not open a socket; the first eligible event starts a single connection and Frame Streams handshake. Connection timeout defaults to 10 seconds, and the duplex operation timeout defaults to 30 seconds. The extension registers the sink with Serilog's built-in batching and sets `RetryTimeLimit` to zero to avoid replaying a partially written session. A supplied `BatchingOptions` must also use a zero retry time limit, a positive batch size and flush interval, and a positive bounded queue limit (or `null` for an unbounded queue). Invalid batching settings are rejected before opening files or TCP connections. Dispose the Serilog logger to complete the file session.

The `DnstapRecord` property must contain an ordinary scalar `Dnstap.Dnstap` protobuf instance; other log events are ignored. For standard Serilog `ForContext()` calls, register `.Destructure.AsScalar<global::Dnstap.Dnstap>()` on `LoggerConfiguration` before creating the logger, or construct a raw `ScalarValue(record)` when adapting `LogEvent` objects. Serilog otherwise converts arbitrary objects into properties or text, losing the protobuf instance. Call Dnstap.NET's record factory to build records from DNS observation data.

## Direct batched adapter

`DnstapSink.CreateFile(path, overwrite: false)` opens an owned file destination without truncating existing files. `DnstapSink.ConnectTcpAsync(host, port)` connects immediately, asynchronously, to a duplex TCP collector, with a bounded connection deadline. `DnstapSink.CreateTcp(host, port)` returns a deferred-connection adapter suitable for an existing dispatcher. Both implement `IBatchedLogEventSink`, suitable for consumers such as LogForwarderApp with an existing dispatcher. Call `EmitBatchAsync()` directly and dispose after draining. Do not wrap the direct adapter in a second batching queue.

Each destination hosts one Frame Streams session. Session failures are terminal, including TCP connection and handshake failures. Callers should report and account for lost records rather than automatically replay. The direct sink exposes `WrittenRecords` (records accepted by the local stream), `IgnoredEvents` (events without a supported DNSTap record), and `FailedBatches` (connection, handshake or write failures). Terminal failures and disposal errors are written to Serilog `SelfLog`. These counts do not imply collector receipt or durable storage. Serilog's built-in retry window is disabled for both fluent methods. TCP is plaintext: use only a trusted or separately protected transport. Successful stream writes do not constitute per-record collector acknowledgments. Disposal completes the STOP handshake for a healthy session; concurrent calls await the same completion and observe its failure. It may block on unresponsive streams. Supplied streams remain open by default (`leaveOpen: true`); file and TCP factories own their streams. File-mode I/O and disposal currently have no timeout guarantee, so use a responsive destination and ensure the caller drains its dispatch queue before disposing.

## Dependency restore

`Dnstap.NET` version `0.1.0` is restored from [GitHub Packages](https://github.com/DeltaZulu-OU/Dnstap.NET/pkgs/nuget/Dnstap.NET). The `NuGet.Config` maps its package ID to the authenticated `deltazulu-github` feed. CI uses a `GITHUB_TOKEN` with `packages: read` and package-level Actions access. For local builds, authenticate with a token carrying `read:packages` and never commit credentials.

## Licensing

This repository is Apache-2.0. Its Dnstap.NET dependency is GPL-3.0-only. Distributing a combined application requires observing the applicable GPL obligations; Apache-2.0 licensing here does not relicense the dependency.
