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
    public void ExistingFileIsPreservedByDefault()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".fstrm");
        var original = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        File.WriteAllBytes(path, original);
        try
        {
            Assert.ThrowsExactly<IOException>(() => DnstapSink.CreateFile(path));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
            Assert.ThrowsExactly<IOException>(() =>
                new LoggerConfiguration().WriteTo.DnstapFile(path));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task ExplicitOverwriteReplacesExistingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".fstrm");
        var original = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        File.WriteAllBytes(path, original);
        try
        {
            await using (var sink = DnstapSink.CreateFile(path, overwrite: true))
            {
                // Opening the file is enough to demonstrate intentional truncation.
            }
            Assert.AreEqual(0L, new FileInfo(path).Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void FluentOverwriteRequiresExplicitOptIn()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".fstrm");
        File.WriteAllBytes(path, new byte[] { 1 });
        try
        {
            using (var logger = new LoggerConfiguration()
                .WriteTo.DnstapFile(path, overwrite: true).CreateLogger())
            {
            }
            Assert.AreEqual(0L, new FileInfo(path).Length);
        }
        finally
        {
            File.Delete(path);
        }
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
            using (var logger = new LoggerConfiguration().Destructure.AsScalar<global::Dnstap.Dnstap>()
                .WriteTo.DnstapFile(path).CreateLogger())
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
