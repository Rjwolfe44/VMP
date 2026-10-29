using LmpCommon.Stats;
using LmpCommon.Time;
using Server.Client;
using Server.Command.Command.Base;
using Server.Log;
using System.Linq;

namespace Server.Command.Command
{
    public class ConnectionStatsCommand : SimpleCommand
    {
        public override bool Execute(string commandArgs)
        {
            //Do some shit here.
            long bytesSentTotal = 0;
            long bytesReceivedTotal = 0;
            LunaLog.Normal("Connection stats:");
            LunaLog.Normal($"Nist Time Difference: {LunaNetworkTime.TimeDifference.TotalMilliseconds} ms");
            foreach (var client in ClientRetriever.GetAuthenticatedClients())
            {
                bytesSentTotal += client.BytesSent;
                bytesReceivedTotal += client.BytesReceived;
                LunaLog.Normal(
                    $"Player '{client.PlayerName}', sent: {client.BytesSent}, received: {client.BytesReceived}");
            }
            LunaLog.Normal($"Server sent: {bytesSentTotal}, received: {bytesReceivedTotal}");

            LunaLog.Normal("Sent by message type since the server started:");
            foreach (var row in VmpTrafficStats.GetSentTotals().Take(15))
            {
                LunaLog.Normal($"  {row.Name}: {VmpTrafficStats.FormatBytes(row.Bytes)} in {row.Messages} messages");
            }

            LunaLog.Normal("Received by message type since the server started:");
            foreach (var row in VmpTrafficStats.GetReceivedTotals().Take(15))
            {
                LunaLog.Normal($"  {row.Name}: {VmpTrafficStats.FormatBytes(row.Bytes)} in {row.Messages} messages");
            }

            LunaLog.Normal("PersistentSync snapshots (decoded size -> bytes on the wire):");
            foreach (var row in VmpTrafficStats.GetSnapshotTotals())
            {
                var ratio = row.WireBytes > 0 ? (double)row.RawBytes / row.WireBytes : 0;
                LunaLog.Normal($"  {row.Domain}: {row.Snapshots} sent, {VmpTrafficStats.FormatBytes(row.RawBytes)} -> {VmpTrafficStats.FormatBytes(row.WireBytes)} ({ratio:0.#}x; zstd+dict {row.ZstdDictionary}, zstd {row.Zstd}, quicklz {row.QuickLz}, raw {row.Raw})");
            }

            return true;
        }
    }
}
