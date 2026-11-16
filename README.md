# Serilog.Sinks.Dnstap

A Serilog integration for DNS telemetry using [Dnstap.NET](https://github.com/DeltaZulu-OU/Dnstap.NET).

**Under development:** the current branch establishes the typed protobuf record contract and its tests. File and TCP forwarding are not yet implemented.

## Dependency restore

The library references `Dnstap.NET` version `0.1.0`, published to [GitHub Packages](https://github.com/DeltaZulu-OU/Dnstap.NET/pkgs/nuget/Dnstap.NET).

`NuGet.Config` maps `Dnstap.NET` explicitly to the `deltazulu-github` source and maps other packages to NuGet.org. The CI restore step passes GitHub Actions' token as NuGet source credentials, with `packages: read` permission; the Dnstap.NET package must also grant this repository access under its package's **Manage Actions access** settings. A `401` or `403` restore error usually means missing package permissions or unsuitable credentials, not a missing package.

For local development, authenticate against the GitHub Packages NuGet source with a token that has `read:packages` access, and run `dotnet restore Serilog.Sinks.Dnstap.slnx --configfile NuGet.Config`. Do not commit credentials to the repository.

## Event contract

`DnstapEventProperty.Name` is `DnstapRecord`. The property must be a Serilog scalar containing a valid `Dnstap.Dnstap` protobuf envelope. Other events are ignored. The initial contract does not define transport behavior or delivery guarantees.

## Licensing

This repository is Apache-2.0. Its Dnstap.NET dependency is GPL-3.0-only. Distributing a combined application requires observing obligations of both licenses; Apache-2.0 on this repository does not relicense its dependencies.
