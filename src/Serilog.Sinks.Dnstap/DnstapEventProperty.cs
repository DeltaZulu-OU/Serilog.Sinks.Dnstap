using Serilog.Events;

namespace Serilog.Sinks.Dnstap;

/// <summary>
/// Extracts typed DNSTap protobuf records from structured Serilog events.
/// Non-DNSTap events are ignored without interpreting formatted message text.
/// </summary>
public static class DnstapEventProperty
{
    public const string Name = "DnstapRecord";

    /// <summary>Finds a raw scalar without coercing structured data or rendered text.</summary>
    public static bool TryGetScalar(LogEvent logEvent, out object? value)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        if (logEvent.Properties.TryGetValue(Name, out var property) &&
            property is ScalarValue scalar && scalar.Value is not null)
        {
            value = scalar.Value;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Finds a DNSTap message envelope with a known event discriminator.
    /// Protocol-level validation remains the responsibility of Dnstap.NET.
    /// </summary>
    public static bool TryGetRecord(LogEvent logEvent, out global::Dnstap.Dnstap? record)
    {
        record = null;
        if (!TryGetScalar(logEvent, out var value))
        {
            return false;
        }

        if (value is not global::Dnstap.Dnstap candidate)
        {
            return false;
        }

        if (candidate.Type != global::Dnstap.Dnstap.Types.Type.Message ||
            candidate.Message is not { } message ||
            !Enum.IsDefined(message.Type))
        {
            return false;
        }

        record = candidate;
        return true;
    }
}
