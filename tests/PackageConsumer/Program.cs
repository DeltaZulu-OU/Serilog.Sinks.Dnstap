using System.Net;
using Dnstap;
using Serilog;
using Serilog.Sinks.Dnstap;

if (args.Length is not (2 or 3) ||
    (args[0] != "file" && args[0] != "tcp") ||
    (args[0] == "file" && args.Length != 2) ||
    (args[0] == "tcp" && args.Length != 3))
{
    Console.Error.WriteLine("Usage: PackageConsumer file PATH | PackageConsumer tcp HOST PORT");
    return 2;
}

var query = Convert.FromHexString("000101000001000000000000076578616d706c6503636f6d0000010001");
var record = DnstapRecordFactory.Create(new DnsLogEntry
{
    EventType = Message.Types.Type.ClientQuery,
    ClientAddress = IPAddress.Parse("192.0.2.10"),
    ServerAddress = IPAddress.Parse("192.0.2.53"),
    ClientPort = 53000,
    ServerPort = 53,
    Transport = SocketProtocol.Udp,
    Timestamp = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
    QueryPacket = query
});
record.Identity = Google.Protobuf.ByteString.CopyFromUtf8("serilog-dnstap-consumer");

var loggerConfig = new LoggerConfiguration().Destructure.AsScalar<global::Dnstap.Dnstap>();
if (args[0] == "file")
{
    loggerConfig.WriteTo.DnstapFile(args[1]);
}
else
{
    if (!int.TryParse(args[2], out var port) || port is < 1 or > 65535)
        throw new ArgumentOutOfRangeException(nameof(args), "Invalid port.");

    loggerConfig.WriteTo.DnstapTcp(args[1], port,
        connectionTimeout: TimeSpan.FromSeconds(5),
        operationTimeout: TimeSpan.FromSeconds(5));
}

using (var logger = loggerConfig.CreateLogger())
{
    logger.ForContext(DnstapEventProperty.Name, record).Information("Observed DNS query");
}

Console.WriteLine($"Package consumer forwarded one record through {args[0]} mode.");
return 0;
