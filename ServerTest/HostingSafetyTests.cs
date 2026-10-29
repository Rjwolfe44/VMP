using LmpUpdater;
using Lidgren.Network;
using Server.Server;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.Utilities;
using System;
using System.IO;
using System.IO.Compression;

namespace ServerTest
{
    [TestClass]
    public class HostingSafetyTests
    {
        [TestMethod]
        public void ConnectionApprovalAcceptsCorrectPasswordAndRejectsMalformedHail()
        {
            var correct = new NetBuffer();
            correct.Write("test-password");
            correct.Position = 0;
            Assert.IsTrue(LidgrenServer.HasValidConnectionPassword(correct, "test-password"));
            Assert.IsFalse(LidgrenServer.HasValidConnectionPassword(new NetBuffer(), "test-password"));
            var malformed = new NetBuffer();
            malformed.Write((byte)0x80);
            malformed.Position = 0;
            Assert.IsFalse(LidgrenServer.HasValidConnectionPassword(malformed, "test-password"));
            var oversized = new NetBuffer();
            oversized.Write(new byte[1025]);
            oversized.Position = 0;
            Assert.IsFalse(LidgrenServer.HasValidConnectionPassword(oversized, "test-password"));
        }

        [DataTestMethod]
        [DataRow("../Config/GeneralSettings")]
        [DataRow("..\\Config\\GeneralSettings")]
        [DataRow("C:\\Windows\\test")]
        [DataRow("/tmp/test")]
        [DataRow("file:stream")]
        [DataRow("..")]
        [DataRow("CON")]
        [DataRow("com1.txt")]
        [DataRow("trailing.")]
        public void RemoteStorageNamesRejectPathsAndDevices(string name)
        {
            Assert.IsFalse(StoragePath.IsSafeName(name));
        }

        [DataTestMethod]
        [DataRow("Jebediah Kerman")]
        [DataRow("Mun lander v2.1")]
        [DataRow("Vlad_44")]
        public void RemoteStorageNamesAllowNormalNames(string name)
        {
            Assert.IsTrue(StoragePath.IsSafeName(name));
        }

        [DataTestMethod]
        [DataRow("../escape.txt")]
        [DataRow("..\\escape.txt")]
        [DataRow("C:/escape.txt")]
        [DataRow("/escape.txt")]
        public void UpdateExtractionRejectsTraversalBeforeWriting(string entryName)
        {
            WithArchive(entryName, (zip, output) =>
            {
                Assert.ThrowsException<InvalidDataException>(() => SafeZipExtractor.Extract(zip, output));
                Assert.IsFalse(File.Exists(Path.Combine(output, "VMPServer", "ok.txt")));
                Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(output), "escape.txt")));
            });
        }

        [TestMethod]
        public void UpdateExtractionPreservesNestedContent()
        {
            WithArchive("VMPClient/GameData/test.txt", (zip, output) =>
            {
                SafeZipExtractor.Extract(zip, output);
                Assert.AreEqual("test", File.ReadAllText(Path.Combine(output, "VMPClient", "GameData", "test.txt")));
            });
        }

        private static void WithArchive(string secondEntry, Action<string, string> action)
        {
            var temp = Path.Combine(Path.GetTempPath(), "VmpHostingTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var zip = Path.Combine(temp, "update.zip");
                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                {
                    using (var writer = new StreamWriter(archive.CreateEntry("VMPServer/ok.txt").Open())) writer.Write("ok");
                    using (var writer = new StreamWriter(archive.CreateEntry(secondEntry).Open())) writer.Write("test");
                }
                action(zip, Path.Combine(temp, "output"));
            }
            finally { Directory.Delete(temp, true); }
        }
    }
}
