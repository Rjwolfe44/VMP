using LmpCommon.Xml;
using System;

namespace Server.Settings.Definition
{
    [Serializable]
    public class AdminApiSettingsDefinition
    {
        [XmlComment(Value = "Enable the local admin API (JSON over HTTP) used by the VMP server manager. It only ever listens on 127.0.0.1 and every request needs the bearer token stored in Config/admin-token.txt. To reach it from another PC, forward the port through an SSH tunnel.")]
        public bool EnableAdminApi { get; set; } = true;

        [XmlComment(Value = "TCP port of the admin API on 127.0.0.1. It is never opened to the network, so no router or firewall change is needed.")]
        public int Port { get; set; } = 18900;
    }
}
