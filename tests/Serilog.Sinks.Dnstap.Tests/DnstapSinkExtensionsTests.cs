using Serilog;
using Serilog.Configuration;

namespace Serilog.Sinks.Dnstap.Tests;

[TestClass]
public sealed class DnstapSinkExtensionsTests
{
    [TestMethod]
    public void NonzeroReplayWindowIsRejected()
    {
        var configuration = new LoggerConfiguration();
        var options = new BatchingOptions { RetryTimeLimit = TimeSpan.FromSeconds(1) };
        Assert.ThrowsExactly<ArgumentException>(() =>
            configuration.WriteTo.DnstapFile("ignored.fstrm", options));
    }

    [TestMethod]
    public void LoggerExtensionWritesAValidFileSession()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".fstrm");
        try
        {
            var record = new global::Dnstap.Dnstap
            {
                Type = global::Dnstap.Dnstap.Types.Type.Message,
                Message = new global::Dnstap.Message
                {
                    Type = global::Dnstap.Message.Types.Type.ClientResponse
                }
            };
            using (var logger = new LoggerConfiguration().WriteTo.DnstapFile(path).CreateLogger())
            {
                logger.ForContext(DnstapEventProperty.Name, record).Information("Observed DNS response");
            }

            var data = File.ReadAllBytes(path);
            Assert.IsGreaterThan(0, data.Length);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
