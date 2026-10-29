using LmpCommon.Stats;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Admin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace ServerTest
{
    [TestClass]
    public class AdminHardeningTests
    {
        private const string Token = "hardening-test-token-0123456789abcdef";
        private const string Wrong = "WRONG-TOKEN-should-not-be-logged";
        private DateTime _now;
        private List<string> _warnings;
        private CountingBackend _backend;
        private AdminApiHandler _handler;

        [TestInitialize]
        public void Init()
        {
            _now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
            _warnings = new List<string>();
            _backend = new CountingBackend();
            _handler = new AdminApiHandler(_backend, Token, null, null, () => _now, _warnings.Add);
        }

        private sealed class CountingBackend : IAdminApiBackend
        {
            public int Calls;
            public StatusInfo GetStatus() => new StatusInfo();
            public NetworkInfo GetNetwork() => new NetworkInfo();
            public List<ClientInfo> GetClients()
            {
                Calls++;
                return new List<ClientInfo>();
            }

            public List<string> ReadLog(int lines) => new List<string>();
            public ReportsInfo GetReports() => new ReportsInfo();
            public List<BackupFileEntry> ListBackups() => new List<BackupFileEntry>();
            public Task<BackupCreateInfo> CreateBackupAsync() => Task.FromResult<BackupCreateInfo>(null);
            public void Say(string text) { }
            public bool Kick(string name, string reason) => false;
        }

        private int Status(string token, IPAddress remote = null)
        {
            var request = new AdminApiRequest { Method = "GET", Path = "/api/v1/clients", Authorization = token == null ? null : "Bearer " + token, RemoteAddress = remote ?? IPAddress.Loopback };
            return _handler.HandleAsync(request).GetAwaiter().GetResult().StatusCode;
        }

        private void Wait(double seconds) => _now = _now.AddSeconds(seconds);

        [TestMethod]
        public void TenFailuresInAMinute_LockOutEveryRequest_For30Seconds_WithoutCheckingTheToken()
        {
            for (var i = 0; i < 10; i++)
            {
                Assert.AreEqual(401, Status(Wrong), "failure " + (i + 1));
                Wait(1);
            }

            var callsBefore = _backend.Calls;
            Assert.AreEqual(429, Status(Token), "even the right token is not looked at while locked out");
            Assert.AreEqual(429, Status(Wrong));
            Assert.AreEqual(callsBefore, _backend.Calls);

            Wait(28); // the 10th failure was at +9 s, so the lock runs until +39 s and we are at +38 s
            Assert.AreEqual(429, Status(Token));
            Wait(1);
            Assert.AreEqual(200, Status(Token));
            Assert.AreEqual(401, Status(Wrong), "a new window starts after the lock");
        }

        [TestMethod]
        public void NineFailures_DoNotLock_AndFailuresSpreadOverTimeDoNotAdd_Up()
        {
            for (var i = 0; i < 9; i++) Status(Wrong);
            Assert.AreEqual(200, Status(Token));

            Wait(61);
            for (var i = 0; i < 40; i++)
            {
                Assert.AreEqual(401, Status(Wrong), "one failure every 7 seconds never reaches 10 within a minute");
                Wait(7);
            }

            Assert.AreEqual(200, Status(Token));
            Assert.AreEqual(0, _warnings.Count);
        }

        [TestMethod]
        public void FailuresAreCounted_ForAMissingOrMalformedHeaderToo_ButNotForOtherPeers()
        {
            for (var i = 0; i < 9; i++) Status(i % 2 == 0 ? null : Wrong);
            Assert.AreEqual(403, Status(Wrong, IPAddress.Parse("192.0.2.10")), "a non-loopback peer is refused before anything is counted");
            Assert.AreEqual(200, Status(Token));
            Assert.AreEqual(401, Status(null));
            Assert.AreEqual(429, Status(Token));
        }

        [TestMethod]
        public void Warning_IsLoggedOncePerMinute_WithCountsAndNeverATokenValue()
        {
            for (var i = 0; i < 10; i++) Status(Wrong);
            Assert.AreEqual(1, _warnings.Count);
            StringAssert.Contains(_warnings[0], "10 failed admin API authentications");

            for (var second = 5; second <= 25; second += 5)
            {
                Wait(5);
                Assert.AreEqual(429, Status(Wrong)); // 5 refused requests while locked
            }

            Assert.AreEqual(1, _warnings.Count, "no second line within a minute");

            Wait(6); // 31 s after the start: unlocked; the second burst locks again, still inside the minute
            for (var i = 0; i < 10; i++) Assert.AreEqual(401, Status(Wrong));
            Assert.AreEqual(1, _warnings.Count);
            Wait(14); // 45 s
            Assert.AreEqual(429, Status(Wrong));
            Wait(15); // 60 s after the first line, still locked (until 61 s)
            Assert.AreEqual(429, Status(Wrong));

            Assert.AreEqual(2, _warnings.Count);
            StringAssert.Contains(_warnings[1], "10 failed admin API authentications");
            StringAssert.Contains(_warnings[1], "7 requests refused with 429");
            Assert.IsFalse(_warnings.Any(w => w.Contains(Wrong) || w.Contains(Token) || w.Contains("Bearer")));
        }
    }
}
