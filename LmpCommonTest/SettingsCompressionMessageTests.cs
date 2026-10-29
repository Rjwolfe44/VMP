using Lidgren.Network;
using LmpCommon.Compression;
using LmpCommon.Message;
using LmpCommon.Message.Data.Settings;
using LmpCommon.Message.Server;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;

namespace LmpCommonTest
{
    /// <summary>The server's compression choice travels at the end of the settings reply; an older layout without it must read as "not sent".</summary>
    [TestClass]
    public class SettingsCompressionMessageTests
    {
        private static readonly ServerMessageFactory Factory = new ServerMessageFactory();
        private static readonly NetClient Peer = new NetClient(new NetPeerConfiguration("COMPRESSIONSETTINGSTESTS"));

        private static byte[] Serialize(int clientLevel, int minSize, string identifier = "")
        {
            var data = Factory.CreateNewMessageData<SettingsReplyMsgData>();
            data.ConsoleIdentifier = identifier;
            data.LaunchPadSlotCount = 3;
            data.LaunchPadKsceMaxPluginFileVersion = "1.2.3";
            data.CompressionClientLevel = clientLevel;
            data.CompressionMinSizeBytes = minSize;
            var message = Factory.CreateNew<SetingsSrvMsg>(data);
            var expected = message.GetMessageSize();
            var outgoing = Peer.CreateMessage(expected);
            message.Serialize(outgoing);
            Assert.IsTrue(expected >= outgoing.LengthBytes, $"GetMessageSize {expected} < real {outgoing.LengthBytes}");
            var bytes = outgoing.ReadBytes(outgoing.LengthBytes);
            message.Recycle();
            return bytes;
        }

        private static SettingsReplyMsgData Read(byte[] bytes)
        {
            var incoming = Peer.CreateIncomingMessage(NetIncomingMessageType.Data, bytes);
            incoming.LengthBytes = bytes.Length;
            return (SettingsReplyMsgData)Factory.Deserialize(incoming, Environment.TickCount).Data;
        }

        [TestMethod]
        public void TheCompressionChoiceRoundTrips()
        {
            var reply = Read(Serialize(7, 300, "console"));
            Assert.AreEqual(7, reply.CompressionClientLevel);
            Assert.AreEqual(300, reply.CompressionMinSizeBytes);
            Assert.AreEqual("console", reply.ConsoleIdentifier);
            Assert.AreEqual(3, reply.LaunchPadSlotCount);
            Assert.AreEqual("1.2.3", reply.LaunchPadKsceMaxPluginFileVersion);
        }

        [TestMethod]
        public void AReplyWithoutTheFieldsLeavesTheSafeDefaults()
        {
            var full = Serialize(7, 300);
            var older = full.Take(full.Length - 8).ToArray(); // the layout of a server that does not know the two fields

            var reply = Read(older);
            Assert.AreEqual(0, reply.CompressionClientLevel, "0 means the server did not say");
            Assert.AreEqual(-1, reply.CompressionMinSizeBytes, "-1 means the server did not say");
            Assert.AreEqual(3, reply.LaunchPadSlotCount, "everything before the new fields is read as before");
            Assert.AreEqual("1.2.3", reply.LaunchPadKsceMaxPluginFileVersion);

            var config = new CompressionConfig();
            config.ApplyFromServer(reply.CompressionClientLevel, reply.CompressionMinSizeBytes);
            Assert.AreEqual(CompressionLimits.DefaultClientLevel, config.ClientLevel);
            Assert.AreEqual(CompressionLimits.DefaultMinSizeBytes, config.MinSizeBytes);
        }

        [TestMethod]
        public void OnlyHalfOfTheNewFieldsIsTreatedAsMissing_AndARecycledMessageKeepsNothing()
        {
            Read(Serialize(9, 1000)); // fills (and recycles) a message with real values first

            var full = Serialize(6, 200);
            var half = Read(full.Take(full.Length - 4).ToArray());
            Assert.AreEqual(0, half.CompressionClientLevel);
            Assert.AreEqual(-1, half.CompressionMinSizeBytes);

            var again = Read(full);
            Assert.AreEqual(6, again.CompressionClientLevel);
            Assert.AreEqual(200, again.CompressionMinSizeBytes);
        }

        [TestMethod]
        public void HostileValuesAreCarriedAsTheyAreAndBoundedByTheClientConfig()
        {
            var reply = Read(Serialize(int.MaxValue, int.MinValue));
            Assert.AreEqual(int.MaxValue, reply.CompressionClientLevel);
            var config = new CompressionConfig();
            config.ApplyFromServer(reply.CompressionClientLevel, reply.CompressionMinSizeBytes);
            Assert.AreEqual(9, config.ClientLevel);
            Assert.AreEqual(CompressionLimits.DefaultMinSizeBytes, config.MinSizeBytes);
        }
    }
}
