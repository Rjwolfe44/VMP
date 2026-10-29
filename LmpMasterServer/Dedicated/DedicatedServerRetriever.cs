using LmpCommon;
using LmpCommon.Collection;
using LmpCommon.RepoRetrievers;
using LmpGlobal;
using LmpMasterServer.Lidgren;
using System;
using System.Net;
using System.Threading.Tasks;

namespace LmpMasterServer.Dedicated
{
    /// <summary>
    /// This class retrieves the dedicated servers stored in
    /// <see cref="RepoConstants.DedicatedServersListUrl"/>
    /// </summary>
    public static class DedicatedServerRetriever
    {
        private static readonly ConcurrentHashSet<IPEndPoint> DedicatedServers = new ConcurrentHashSet<IPEndPoint>();
        private static readonly TimeSpan RequestInterval = TimeSpan.FromMinutes(5);

        public static bool IsDedicatedServer(IPEndPoint endpoint)
        {
            return DedicatedServers.Contains(endpoint);
        }

        /// <summary>
        /// Download the dedicated server list from the <see cref="RepoConstants.DedicatedServersListUrl"/> and return the ones that are correctly written
        /// </summary>
        public static async Task RefreshDedicatedServersList()
        {
            while (MasterServer.RunServer)
            {
                try
                {
                    if (RepoListContentReader.TryReadNormalizedLines(RepoConstants.DedicatedServersListUrl, "DedicatedServersList.txt", out var servers))
                    {
                        DedicatedServers.Clear();

                        foreach (var server in servers)
                        {
                            try
                            {
                                DedicatedServers.Add(LunaNetUtils.CreateEndpointFromString(server));
                            }
                            catch (Exception)
                            {
                                //Ignore the bad server
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    //Ignored
                }

                await Task.Delay(RequestInterval).ConfigureAwait(false);
            }
        }
    }
}
