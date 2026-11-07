using LmpCommon.Locks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass]
    public class SpectatorLockTests
    {
        private static LockQuery Query(out LockStore store)
        {
            store = new LockStore();
            return new LockQuery(store);
        }

        private static void Spectate(LockStore store, string player) =>
            store.AddOrUpdateLock(new LockDefinition(LockType.Spectator, player));

        // The watched player sends the vessel position at the fast rate only while somebody else is spectating.
        [TestMethod]
        public void AnyOtherSpectatorLock_IgnoresOwnLock_NoticesOthers()
        {
            var query = Query(out var store);
            Assert.IsFalse(query.AnyOtherSpectatorLock("Vlad"), "nobody spectating");

            Spectate(store, "Vlad");
            Assert.IsFalse(query.AnyOtherSpectatorLock("Vlad"), "only my own spectator lock");

            Spectate(store, "Friend");
            Assert.IsTrue(query.AnyOtherSpectatorLock("Vlad"), "a friend is spectating too");
            Assert.IsTrue(query.AnyOtherSpectatorLock("Friend"), "from the friend's side Vlad is the other spectator");
        }

        [TestMethod]
        public void AnyOtherSpectatorLock_OnlyAnotherPlayerSpectating()
        {
            var query = Query(out var store);
            Spectate(store, "Friend");
            Assert.IsTrue(query.AnyOtherSpectatorLock("Vlad"));

            store.RemoveLock(new LockDefinition(LockType.Spectator, "Friend"));
            Assert.IsFalse(query.AnyOtherSpectatorLock("Vlad"), "spectator left");
        }
    }
}
