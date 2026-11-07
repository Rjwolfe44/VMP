namespace LmpCommon.Locks
{
    /// <summary>
    /// Class that retrieve locks specific for spectators
    /// </summary>
    public partial class LockQuery
    {
        /// <summary>
        /// Checks if given user is spectating
        /// </summary>
        public bool SpectatorLockExists(string playerName)
        {
            return LockStore.SpectatorLocks.ContainsKey(playerName);
        }

        /// <summary>
        /// True when somebody other than <paramref name="playerName"/> holds a spectator lock, i.e. is watching a vessel.
        /// Cheap and allocation free: the send-rate code asks this several times per physics frame.
        /// </summary>
        public bool AnyOtherSpectatorLock(string playerName)
        {
            var locks = LockStore.SpectatorLocks;
            if (locks.IsEmpty) return false;
            if (locks.Count > 1) return true;
            return playerName == null || !locks.ContainsKey(playerName);
        }

        /// <summary>
        /// Gets the spectator lock for given player if it exists, otherwise null
        /// </summary>
        public LockDefinition GetSpectatorLock(string playerName)
        {
            return LockStore.SpectatorLocks.TryGetValue(playerName, out var spectatorLock) ? spectatorLock : null;
        }
    }
}
