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
    public void InvalidBatchOptionsFailBeforeCreatingFile()
    {
        var invalid = new BatchingOptions[]
        {
            new() { RetryTimeLimit = TimeSpan.FromSeconds(1) },
            new() { RetryTimeLimit = TimeSpan.Zero, BatchSizeLimit = 0 },
            new() { RetryTimeLimit = TimeSpan.Zero, BufferingTimeLimit = TimeSpan.Zero },
            new() { RetryTimeLimit = TimeSpan.Zero, QueueLimit = 0 }
        };

        for (var i = 0; i < invalid.Length; i++)
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".fstrm");
            var batching = invalid[i];
            if (i == 0)
            {
                Assert.ThrowsExactly<ArgumentException>(() =>
                    new LoggerConfiguration().WriteTo.DnstapFile(path, batching));
            }
            else
            {
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                    new LoggerConfiguration().WriteTo.DnstapFile(path, batching));
            }
            Assert.IsFalse(File.Exists(path), "Invalid configuration created an output file.");
        }
    }

    [TestMethod]
    public void ValidCustomBatchOptionsAreAcceptedForBothTransports()
    {
        var batch = new BatchingOptions
        {
            RetryTimeLimit = TimeSpan.Zero,
            BatchSizeLimit = 1,
            BufferingTimeLimit = TimeSpan.FromMilliseconds(100),
            QueueLimit = 5,
            EagerlyEmitFirstEvent = false
        };
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".fstrm");
        try
        {
            using (var logger = new LoggerConfiguration()
                .WriteTo.DnstapFile(path, batch).CreateLogger())
            {
            }
            using (var logger = new LoggerConfiguration()
                .WriteTo.DnstapTcp("127.0.0.1", 53, batch).CreateLogger())
            {
            }
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public void ExistingFileIsPreservedWhenOverwriteDisabled()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".fstrm");
        var original = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        File.WriteAllBytes(path, original);
        try
        {
            Assert.ThrowsExactly<IOException>(() => DnstapSink.CreateFile(path, overwrite: false));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
            Assert.ThrowsExactly<IOException>(() =>
                new LoggerConfiguration().WriteTo.DnstapFile(path, overwrite: false));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task DirectFileFactoryOverwritesByDefault()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".fstrm");
        var original = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        File.WriteAllBytes(path, original);
        try
        {
            await using (var sink = DnstapSink.CreateFile(path))
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
    public void FluentFileSinkOverwritesByDefault()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".fstrm");
        File.WriteAllBytes(path, new byte[] { 1 });
        try
        {
            using (var logger = new LoggerConfiguration()
                .WriteTo.DnstapFile(path).CreateLogger())
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
    public void UnrelatedLogTrafficNeverEntersTheDnstapBatchingQueue()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".fstrm");
        DnstapSink? captured = null;
        try
        {
            var options = new BatchingOptions
            {
                RetryTimeLimit = TimeSpan.Zero,
                BatchSizeLimit = 1,
                QueueLimit = 1
            };
            var dnsRecord = new global::Dnstap.Dnstap
            {
                Type = global::Dnstap.Dnstap.Types.Type.Message,
                Message = new global::Dnstap.Message
                {
                    Type = global::Dnstap.Message.Types.Type.ClientResponse
                }
            };

            using (var logger = new LoggerConfiguration()
                .Destructure.AsScalar<global::Dnstap.Dnstap>()
                .WriteTo.DnstapFile(path, options, onSinkCreated: sink => captured = sink)
                .CreateLogger())
            {
                for (var i = 0; i < 1000; i++)
                    logger.Information("Ordinary event {Index}", i);

                logger.ForContext(DnstapEventProperty.Name, dnsRecord)
                    .Information("DNS response");
            }

            Assert.IsNotNull(captured);
            Assert.AreEqual(1L, captured.WrittenRecords);
            Assert.AreEqual(0L, captured.IgnoredEvents);
            Assert.AreEqual(0L, captured.DroppedRecords);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [TestMethod]
    public void BoundedNativeQueueLossesAreReconciledAfterLoggerShutdown()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".fstrm");
        DnstapSink? captured = null;
        const int submitted = 1000;
        try
        {
            var options = new BatchingOptions
            {
                RetryTimeLimit = TimeSpan.Zero,
                BatchSizeLimit = 1,
                QueueLimit = 1,
                EagerlyEmitFirstEvent = false,
                BufferingTimeLimit = TimeSpan.FromMilliseconds(100)
            };
            var dnsRecord = new global::Dnstap.Dnstap
            {
                Type = global::Dnstap.Dnstap.Types.Type.Message,
                Message = new global::Dnstap.Message
                {
                    Type = global::Dnstap.Message.Types.Type.ClientResponse
                }
            };

            using (var logger = new LoggerConfiguration()
                .Destructure.AsScalar<global::Dnstap.Dnstap>()
                .WriteTo.DnstapFile(path, options, onSinkCreated: sink => captured = sink)
                .CreateLogger())
            {
                for (var i = 0; i < submitted; i++)
                    logger.ForContext(DnstapEventProperty.Name, dnsRecord)
                        .Information("DNS event {Index}", i);
            }

            Assert.IsNotNull(captured);
            Assert.AreEqual((long)submitted, captured.SubmittedRecords);
            Assert.AreEqual((long)submitted,
                captured.WrittenRecords + captured.RejectedRecords +
                captured.IgnoredEvents + captured.DroppedRecords);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [TestMethod]
    public void FluentTcpRegistrationExposesDiagnosticSink()
    {
        DnstapSink? captured = null;
        using (var logger = new LoggerConfiguration()
            .WriteTo.DnstapTcp("127.0.0.1", 53, onSinkCreated: sink => captured = sink)
            .CreateLogger())
        {
            Assert.IsNotNull(captured);
            Assert.AreEqual(0L, captured.WrittenRecords);
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
