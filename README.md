# Serilog.Sinks.Dnstap

A Serilog integration for DNS telemetry using [Dnstap.NET](https://github.com/DeltaZulu-OU/Dnstap.NET).

## Status

The current library supports typed `DnstapRecord` events, file-mode Frame Streams output, and a persistent TCP collector session. TCP reconnect and batch replay are deliberately unsupported. Independent collector interoperability and deployment-level stress testing remain necessary.

## Serilog integration

Configure a logger with `WriteTo.DnstapFile(path)` to write a Frame Streams file. The extension registers the sink with Serilog's built-in batching and sets `RetryTimeLimit` to zero to avoid replaying a partially written session. A supplied `BatchingOptions` must also use a zero retry time limit. Dispose the Serilog logger to complete the file session.

The `DnstapRecord` property must contain an ordinary scalar `Dnstap.Dnstap` protobuf instance; other log events are ignored. For standard Serilog `ForContext()` calls, register `.Destructure.AsScalar<global::Dnstap.Dnstap>()` on `LoggerConfiguration` before creating the logger, or construct a raw `ScalarValue(record)` when adapting `LogEvent` objects. Serilog otherwise converts arbitrary objects into properties or text, losing the protobuf instance. Call Dnstap.NET's record factory to build records from DNS observation data.

## Direct batched adapter

`DnstapSink.CreateFile(path)` opens an owned file destination. `DnstapSink.ConnectTcpAsync(host, port)` connects once to a duplex TCP collector. Both implement `IBatchedLogEventSink`, suitable for consumers such as LogForwarderApp with an existing dispatcher. Call `EmitBatchAsync()` directly and dispose after draining. Do not wrap the direct adapter in a second batching queue.

Each destination hosts one Frame Streams session. Session failures are terminal, so callers should report and account for lost records rather than automatically replay. TCP is plaintext: use only a trusted or separately protected transport. Successful stream writes do not constitute per-record collector acknowledgments. Disposal completes the STOP handshake for a healthy session; it may block on unresponsive streams.

## Dependency restore

`Dnstap.NET` version `0.1.0` is restored from [GitHub Packages](https://github.com/DeltaZulu-OU/Dnstap.NET/pkgs/nuget/Dnstap.NET). The `NuGet.Config` maps its package ID to the authenticated `deltazulu-github` feed. CI uses a `GITHUB_TOKEN` with `packages: read` and package-level Actions access. For local builds, authenticate with a token carrying `read:packages` and never commit credentials.

## Licensing

This repository is Apache-2.0. Its Dnstap.NET dependency is GPL-3.0-only. Distributing a combined application requires observing the applicable GPL obligations; Apache-2.0 licensing here does not relicense the dependency.
