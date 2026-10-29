using Lidgren.Network;
using LmpCommon;
using LmpCommon.Enums;
using LmpCommon.Message;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Handshake;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;

if (args.Length < 3)
{
    Console.Error.WriteLine("VmpProbe <host> <port> <GeneralSettings.xml> [accept|password|build|empty|malformed|oversize]");
    return 2;
}
var expected = args.Length > 3 ? args[3] : "accept";
if (!new[] { "accept", "password", "build", "empty", "malformed", "oversize" }.Contains(expected)) return 2;
var rejectedPassword = expected is "password" or "empty" or "malformed" or "oversize";
var password = XDocument.Load(args[2]).Root?.Element("Password")?.Value ?? "";
if (expected == "password") password = "intentionally-invalid-probe-password";
var config = new NetPeerConfiguration("VMP")
{
    LocalAddress = IPAddress.IPv6Any, DualStack = true,
    MaximumTransmissionUnit = 1200, AutoExpandMTU = false,
    ConnectionTimeout = 30, MaximumHandshakeAttempts = 5,
    PingInterval = 1
};
var peer = new NetClient(config);
var factory = new ClientMessageFactory();
var incomingFactory = new ServerMessageFactory();
var timer = Stopwatch.StartNew();
try
{
    peer.Start();
    var hail = peer.CreateMessage();
    if (expected == "malformed") hail.Write(new byte[] { 0xff, 0xff, 0xff, 0xff, 0x7f });
    else if (expected == "oversize") hail.Write(new byte[1025]);
    else if (expected != "empty") hail.Write(password);
    var endpoints = Dns.GetHostAddresses(args[0]);
    var endpoint = endpoints.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? endpoints.First();
    peer.Connect(new IPEndPoint(endpoint, int.Parse(args[1])), hail);
    while (timer.Elapsed < TimeSpan.FromSeconds(45))
    {
        var packet = peer.ReadMessage();
        if (packet == null) { Thread.Sleep(5); continue; }
        try
        {
            if (packet.MessageType == NetIncomingMessageType.StatusChanged)
            {
                var status = (NetConnectionStatus)packet.ReadByte();
                var reason = packet.ReadString();
                if (status == NetConnectionStatus.Disconnected)
                {
                    var passed = (rejectedPassword && reason.Contains("Invalid password", StringComparison.OrdinalIgnoreCase)) ||
                                 (expected == "build" && reason.Contains("Exact build mismatch", StringComparison.OrdinalIgnoreCase));
                    Console.WriteLine(JsonSerializer.Serialize(new { host = args[0], port = args[1], expected, passed, status = reason, elapsedMs = timer.ElapsedMilliseconds }));
                    return passed ? 0 : 1;
                }
                if (status == NetConnectionStatus.Connected)
                {
                    var data = factory.CreateNewMessageData<HandshakeRequestMsgData>();
                    data.PlayerName = "Probe_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    data.UniqueIdentifier = Guid.NewGuid().ToString();
                    data.ProtocolForkId = SessionAdmission.LocalProtocolForkId;
                    data.ExactClientBuild = expected == "build" ? "incompatible-probe-build" : SessionAdmission.LocalExactBuild;
                    var message = factory.CreateNew<HandshakeCliMsg>(data);
                    var outgoing = peer.CreateMessage();
                    message.Serialize(outgoing);
                    peer.SendMessage(outgoing, message.NetDeliveryMethod, message.Channel);
                    peer.FlushSendQueue();
                }
            }
            else if (packet.MessageType == NetIncomingMessageType.Data)
            {
                var message = incomingFactory.Deserialize(packet, DateTime.UtcNow.Ticks);
                if (message.Data is HandshakeReplyMsgData reply)
                {
                    var passed = expected == "build" ? reply.Response == HandshakeReply.ProtocolBuildMismatch
                        : expected == "accept" && reply.Response == HandshakeReply.HandshookSuccessfully;
                    Console.WriteLine(JsonSerializer.Serialize(new { host = args[0], port = args[1], expected, passed, response = reply.Response.ToString(), elapsedMs = timer.ElapsedMilliseconds, mtu = 1200, modControl = reply.ModControl }));
                    return passed ? 0 : 1;
                }
            }
        }
        finally { peer.Recycle(packet); }
    }
    Console.WriteLine(JsonSerializer.Serialize(new { host = args[0], expected, passed = false, status = "UDP/VMP handshake timed out" }));
    return 1;
}
finally
{
    peer.Shutdown("Network audit complete");
    Thread.Sleep(250);
}
