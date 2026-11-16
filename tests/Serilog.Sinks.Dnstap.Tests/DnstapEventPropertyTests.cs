using Serilog.Events;
using Serilog.Parsing;

namespace Serilog.Sinks.Dnstap.Tests;

[TestClass]
public sealed class DnstapEventPropertyTests
{
    private static LogEvent EventWith(LogEventProperty? property = null)
    {
        var properties = property is null ? Array.Empty<LogEventProperty>() : new[] { property };
        return new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null,
            new MessageTemplateParser().Parse("DNS event"), properties);
    }

    private static LogEvent WithValue(object? value) =>
        EventWith(new LogEventProperty(DnstapEventProperty.Name, new ScalarValue(value)));

    [TestMethod]
    public void MissingRecordIsIgnored()
    {
        Assert.IsFalse(DnstapEventProperty.TryGetRecord(EventWith(), out var record));
        Assert.IsNull(record);
    }

    [TestMethod]
    public void NullRecordIsIgnored()
    {
        Assert.IsFalse(DnstapEventProperty.TryGetRecord(WithValue(null), out _));
    }

    [TestMethod]
    public void ScalarRecordIsPreservedByReference()
    {
        var value = new object();
        Assert.IsTrue(DnstapEventProperty.TryGetScalar(WithValue(value), out var output));
        Assert.AreSame(value, output);
    }

    [TestMethod]
    public void StructuredRecordIsNotCoerced()
    {
        var property = new LogEventProperty(DnstapEventProperty.Name,
            new StructureValue(Array.Empty<LogEventProperty>()));
        Assert.IsFalse(DnstapEventProperty.TryGetRecord(EventWith(property), out _));
    }

    [TestMethod]
    public void ForeignScalarIsIgnored()
    {
        Assert.IsFalse(DnstapEventProperty.TryGetRecord(WithValue("protobuf bytes"), out _));
    }

    [TestMethod]
    public void ValidMessageIsPreservedByReference()
    {
        var record = new global::Dnstap.Dnstap
        {
            Type = global::Dnstap.Dnstap.Types.Type.Message,
            Message = new global::Dnstap.Message { Type = global::Dnstap.Message.Types.Type.ClientResponse }
        };
        Assert.IsTrue(DnstapEventProperty.TryGetRecord(WithValue(record), out var output));
        Assert.AreSame(record, output);
    }

    [TestMethod]
    public void MissingNestedMessageIsIgnored()
    {
        var record = new global::Dnstap.Dnstap { Type = global::Dnstap.Dnstap.Types.Type.Message };
        Assert.IsFalse(DnstapEventProperty.TryGetRecord(WithValue(record), out _));
    }

    [TestMethod]
    public void UnknownEventTypeIsIgnored()
    {
        var record = new global::Dnstap.Dnstap
        {
            Type = global::Dnstap.Dnstap.Types.Type.Message,
            Message = new global::Dnstap.Message { Type = (global::Dnstap.Message.Types.Type)int.MaxValue }
        };
        Assert.IsFalse(DnstapEventProperty.TryGetRecord(WithValue(record), out _));
    }

    [TestMethod]
    public void NullLogEventFailsExplicitly()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => DnstapEventProperty.TryGetRecord(null!, out _));
    }
}
