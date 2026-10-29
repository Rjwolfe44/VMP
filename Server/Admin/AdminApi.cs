using LmpCommon;
using Server.Context;
using Server.Events;
using Server.Log;
using Server.Settings;
using Server.Settings.Structures;
using System;

namespace Server.Admin
{
    /// <summary>Starts and stops the admin API together with the server (see <see cref="AdminApiServer"/>). Separate from the public status site on port 8900.</summary>
    public static class AdminApi
    {
        private static AdminApiServer _server;

        static AdminApi() => ExitEvent.ServerClosing += Stop;

        public static void Start()
        {
            var settings = AdminApiSettings.SettingsStore;
            if (!settings.EnableAdminApi) return;

            try
            {
                if (settings.Port < 1 || settings.Port > 65535)
                {
                    LunaLog.Error($"[AdminApi] Port {settings.Port} is not valid, the admin API stays off. See the adminApi section of Config/vmp-server.json");
                    return;
                }

                if (LunaNetUtils.IsTcpPortInUse(settings.Port))
                {
                    LunaLog.Error($"[AdminApi] Port {settings.Port} is already in use, the admin API stays off. See the adminApi section of Config/vmp-server.json");
                    return;
                }

                var token = AdminApiToken.LoadOrCreate(ServerContext.ConfigDirectory, out var created);
                if (created) LunaLog.Normal("[AdminApi] created a new token in Config/" + AdminApiToken.FileName);

                var file = SettingsFile.Current;
                var handler = new AdminApiHandler(new ServerAdminApiBackend(), token, e => LunaLog.Error($"[AdminApi] request failed: {e}"),
                    new SettingsAdmin(file.Sections, file.Save, LunaLog.Normal), null, line => LunaLog.Warning($"[AdminApi] {line}"));
                _server = new AdminApiServer(handler, e => LunaLog.Error($"[AdminApi] connection error: {e.Message}"));
                _server.Start(settings.Port);
                LunaLog.Normal($"[AdminApi] listening on 127.0.0.1:{_server.Port} (local only, bearer token required)");
            }
            catch (Exception e)
            {
                _server = null;
                LunaLog.Error($"[AdminApi] could not start on port {settings.Port}, it stays off: {e.Message} " +
                              "(if the port is reserved by Windows, check 'netsh int ipv4 show excludedportrange protocol=tcp' and set another Port in the adminApi section of Config/vmp-server.json)");
            }
        }

        public static void Stop()
        {
            try
            {
                _server?.Dispose();
                _server = null;
            }
            catch (Exception e)
            {
                LunaLog.Error($"[AdminApi] could not stop: {e.Message}");
            }
        }
    }
}
