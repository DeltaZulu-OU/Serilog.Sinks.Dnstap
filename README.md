# Serilog.Sinks.Dnstap

A Serilog integration for DNS telemetry using [Dnstap.NET](https://github.com/DeltaZulu-OU/Dnstap.NET).

## Status

Development version: supports a typed `DnstapRecord` Serilog event property, file-mode Frame Streams output and a single TCP collector connection. TCP reconnect and automatic batch replay are intentionally unsupported. Validation is ongoing; do not interpret successful writes as per-record collector acknowledgments.

## Direct sink API

`DnstapSink.CreateFile(path)` opens a Frame Streams file owned by the sink. `DnstapSink.ConnectTcpAsync(host, port)` establishes one duplex TCP session. Both implement Serilog's `IBatchedLogEventSink`. Applications with an existing dispatcher can call `EmitBatchAsync()` directly, then dispose the sink after draining the dispatcher. A regular Serilog application can use `LoggerSinkConfiguration.Sink(IBatchedLogEventSink, BatchingOptions)`, but the built-in Serilog batching layer may retry failures; its retry behavior must be evaluated before use with this terminal-failure transport.

Unrelated log events are skipped. Events containing a `ScalarValue` named `DnstapRecord` with a `Dnstap.Dnstap` instance are forwarded. Use Dnstap.NET's record factory to construct protobuf messages from DNS observation data.

Each file or TCP connection carries exactly one Frame Streams session. Complete and dispose a healthy session to emit STOP. A faulted session is not reopened. TCP is plaintext; deploy it only on a suitably trusted or protected transport. Do not assume a successful write establishes collector durability.

## Dependency restore

The library references `Dnstap.NET` version `0.1.0` from [GitHub Packages](https://github.com/DeltaZulu-OU/Dnstap.NET/pkgs/nuget/Dnstap.NET). The repository `NuGet.Config` maps the exact package ID to the authenticated `deltazulu-github` feed.

CI grants `packages: read`, but the package also needs to grant this repository access in **Manage Actions access**. For local development, authenticate with a token having `read:packages` access; never commit credentials.

## Licensing

This repository is Apache-2.0. Its Dnstap.NET dependency is GPL-3.0-only. Distribution of a combined application must respect the relevant GPL obligations; licensing this repository under Apache-2.0 does not relicense its dependencies.
