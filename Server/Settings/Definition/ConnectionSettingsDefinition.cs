using Lidgren.Network;
using LmpCommon.Xml;
using System;
using System.Net;

namespace Server.Settings.Definition
{
    [Serializable]
    public class ConnectionSettingsDefinition
    {
        [XmlComment(Value = "The address the server listens on. If set to the unspecified IPv6 address [::], the server listens for both IPv6 and IPv4")]
        public string ListenAddress { get; set; } = IPAddress.Any.ToString();

        [XmlComment(Value = "UDP game port. Direct Internet hosting needs a reachable route and firewall rule. Master-server registration cannot guarantee NAT traversal. For CGNAT use a private network such as ZeroTier or a Playit UDP tunnel.")]
        public int Port { get; set; } = 8800;

        [XmlComment(Value = "Heartbeat interval in ms. MUST be lower than the ConnectionMsTimeout value.")]
        public int HearbeatMsInterval { get; set; } = 1000;

        [XmlComment(Value = "Connection timeout in ms. If no heartbeats are received after this interval, the client is disconnected.")]
        public int ConnectionMsTimeout { get; set; } = 30000;

        [XmlComment(Value = "Tries to use UPnP to open the ports in your router")]
        public bool Upnp { get; set; } = false;

        [XmlComment(Value = "UPnP timeout in ms for trying to open the ports")]
        public int UpnpMsTimeout { get; set; } = 5000;

        [XmlComment(Value = "Maximum UDP packet size in bytes. Default 1200 leaves room for VPN/tunnel overhead. Use the same value in client Settings.xml. Max value is 8192.")]
        public int MaximumTransmissionUnit { get; set; } = 1200;

        [XmlComment(Value = "Try to expand MTU size")]
        public bool AutoExpandMtu { get; set; } = false;
    }
}
