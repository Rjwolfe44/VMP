using LmpCommon.Collection;
using LmpCommon.Time;
using LmpGlobal;
using System;
using System.Net;
using System.Threading.Tasks;

namespace LmpCommon.RepoRetrievers
{
    /// <summary>
    /// This class retrieves the ips of master servers that are stored in:
    /// <see cref="RepoConstants.MasterServersListUrl"/>
    /// </summary>
    public static class MasterServerRetriever
    {
        private static readonly ConcurrentHashSet<IPEndPoint> MasterServersEndpoints = new ConcurrentHashSet<IPEndPoint>();
        public static ConcurrentHashSet<IPEndPoint> MasterServers
        {
            get
            {
                if (_lastRequestTime == DateTime.MinValue)
                {
                    //Run syncronously if it's the first time
                    RefreshMasterServersList();
                    _lastRequestTime = LunaComputerTime.UtcNow;
                }
                else if (LunaComputerTime.UtcNow - _lastRequestTime > MaxRequestInterval)
                {
                    Task.Run(RefreshMasterServersList);
                    _lastRequestTime = LunaComputerTime.UtcNow;
                }

                return MasterServersEndpoints;
            }
        }

        private static readonly TimeSpan MaxRequestInterval = TimeSpan.FromMinutes(15);
        private static DateTime _lastRequestTime = DateTime.MinValue;

        private static void RefreshMasterServersList()
        {
            if (!RepoListContentReader.TryReadNormalizedLines(RepoConstants.MasterServersListUrl, "MasterServersList.txt", out var servers))
                return;

            MasterServersEndpoints.Clear();

            foreach (var server in servers)
            {
                try
                {
                    MasterServersEndpoints.Add(LunaNetUtils.CreateEndpointFromString(server));
                }
                catch (Exception)
                {
                    //Ignore the bad server
                }
            }
        }
    }
}
