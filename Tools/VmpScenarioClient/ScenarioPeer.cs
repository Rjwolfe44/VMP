using Lidgren.Network;
using LmpCommon;
using LmpCommon.Message;
using LmpCommon.Message.Client;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Data.Handshake;
using LmpCommon.Message.Data.Scenario;
using LmpCommon.Enums;
using System.Diagnostics;
using System.Net;
using System.Text;

/// <summary>
/// One simulated player: a Lidgren connection that does the same handshake and the same scenario Request / Data
/// messages as the real client, built with the shared message factories (so compression is the real codec).
/// </summary>
sealed class ScenarioPeer : IDisposable
{
    private readonly NetClient _peer;
    private readonly ClientMessageFactory _out = new ClientMessageFactory();
    private readonly ServerMessageFactory _in = new ServerMessageFactory();
    private readonly Dictionary<string, string> _modules = new Dictionary<string, string>();
    private int _scenarioReplies;
    private HandshakeReply? _handshake;
    private bool _connected;
    private string _disconnect;

    public string Name { get; }

    /// <summary>The scenario modules the server sent in its last reply (module name to text).</summary>
    public IReadOnlyDictionary<string, string> Modules => _modules;

    /// <summary>What this client's game would hold locally; starts as the server copy, tests overwrite it before uploading.</summary>
    public string LocalKerbalKonstructs;

    public ScenarioPeer(string name)
    {
        Name = name;
        _peer = new NetClient(new NetPeerConfiguration("VMP")
        {
            LocalAddress = IPAddress.IPv6Any,
            DualStack = true,
            MaximumTransmissionUnit = 1200,
            AutoExpandMTU = false,
            ConnectionTimeout = 30,
            MaximumHandshakeAttempts = 5,
            PingInterval = 1
        });
    }

    /// <summary>Connect and complete the handshake. Returns an error text, or null on success.</summary>
    public string ConnectAndHandshake(string host, int port, TimeSpan timeout)
    {
        _peer.Start();
        var hail = _peer.CreateMessage();
        hail.Write(""); // server password (none on the local test server)
        var ip = Dns.GetHostAddresses(host).First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        _peer.Connect(new IPEndPoint(ip, port), hail);

        if (!PumpUntil(() => _connected || _disconnect != null, timeout) || !_connected)
            return "connect failed: " + (_disconnect ?? "timeout");

        var data = _out.CreateNewMessageData<HandshakeRequestMsgData>();
        data.PlayerName = Name;
        data.UniqueIdentifier = Guid.NewGuid().ToString();
        data.ProtocolForkId = SessionAdmission.LocalProtocolForkId;
        data.ExactClientBuild = SessionAdmission.LocalExactBuild;
        Send(_out.CreateNew<HandshakeCliMsg>(data));

        if (!PumpUntil(() => _handshake != null || _disconnect != null, timeout))
            return "handshake timeout";
        return _handshake == HandshakeReply.HandshookSuccessfully ? null : $"handshake refused: {_handshake} {_disconnect}";
    }

    /// <summary>Send ScenarioCliMsg/Request and wait for the server's scenario reply. Returns an error text or null.</summary>
    public string FetchScenarios(TimeSpan timeout)
    {
        var before = _scenarioReplies;
        Send(_out.CreateNew<ScenarioCliMsg, ScenarioRequestMsgData>());
        if (!PumpUntil(() => _scenarioReplies > before, timeout)) return "no scenario reply within " + timeout.TotalSeconds + " s";
        _modules.TryGetValue(ScenarioScript.Module, out LocalKerbalKonstructs);
        return LocalKerbalKonstructs == null ? "reply did not contain " + ScenarioScript.Module : null;
    }

    /// <summary>Upload one module the way ScenarioMessageSender.SendScenarioModuleData does.</summary>
    public void UploadModule(string module, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var data = _out.CreateNewMessageData<ScenarioDataMsgData>();
        data.ScenariosData = new[] { new ScenarioInfo { Module = module, Data = bytes, NumBytes = bytes.Length } };
        data.ScenarioCount = 1;
        Send(_out.CreateNew<ScenarioCliMsg>(data));
    }

    /// <summary>Keep reading for the given time (lets queued packets go out and drains replies).</summary>
    public void Idle(TimeSpan time) => PumpUntil(() => false, time);

    private void Send(IMessageBase message)
    {
        var outgoing = _peer.CreateMessage();
        message.Serialize(outgoing);
        _peer.SendMessage(outgoing, message.NetDeliveryMethod, message.Channel);
        _peer.FlushSendQueue();
    }

    private bool PumpUntil(Func<bool> done, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            if (done()) return true;
            var packet = _peer.ReadMessage();
            if (packet == null) { Thread.Sleep(5); continue; }
            try { Handle(packet); }
            finally { _peer.Recycle(packet); }
        }
        return done();
    }

    private void Handle(NetIncomingMessage packet)
    {
        if (packet.MessageType == NetIncomingMessageType.StatusChanged)
        {
            var status = (NetConnectionStatus)packet.ReadByte();
            var reason = packet.ReadString();
            if (status == NetConnectionStatus.Connected) _connected = true;
            else if (status == NetConnectionStatus.Disconnected) _disconnect = reason;
        }
        else if (packet.MessageType == NetIncomingMessageType.Data)
        {
            IMessageBase message;
            try { message = _in.Deserialize(packet, DateTime.UtcNow.Ticks); }
            catch (Exception e) { Console.WriteLine($"  [{Name}] undecodable server message ignored: {e.Message}"); return; }

            if (message.Data is HandshakeReplyMsgData reply) _handshake = reply.Response;
            else if (message.Data is ScenarioDataMsgData scenarios)
            {
                _modules.Clear();
                for (var i = 0; i < scenarios.ScenarioCount; i++)
                    _modules[scenarios.ScenariosData[i].Module] = Encoding.UTF8.GetString(scenarios.ScenariosData[i].Data, 0, scenarios.ScenariosData[i].NumBytes);
                _scenarioReplies++;
            }
        }
    }

    public void Dispose()
    {
        _peer.Shutdown("scenario test done");
        Thread.Sleep(150);
    }
}
