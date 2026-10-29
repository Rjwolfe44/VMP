using Lidgren.Network;
using LmpCommon.Enums;
using LmpCommon.LogUpload;
using LmpCommon.Message;
using LmpCommon.Message.Base;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.LogUpload;
using LmpCommon.Message.Interface;
using LmpCommon.Message.Server;
using LmpCommon.Message.Types;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;

namespace LmpCommonTest
{
    [TestClass]
    public class LogUploadMessageTests
    {
        private static readonly ClientMessageFactory CliFactory = new ClientMessageFactory();
        private static readonly ServerMessageFactory SrvFactory = new ServerMessageFactory();
        private static readonly NetClient Peer = new NetClient(new NetPeerConfiguration("LOGUPLOADTESTS"));

        private static IMessageBase RoundTrip(IMessageBase msg, FactoryBase factory)
        {
            var expectedSize = msg.GetMessageSize();
            var outgoing = Peer.CreateMessage(expectedSize);
            msg.Serialize(outgoing);
            Assert.IsTrue(expectedSize >= outgoing.LengthBytes, $"GetMessageSize {expectedSize} < real {outgoing.LengthBytes}");

            var bytes = outgoing.ReadBytes(outgoing.LengthBytes);
            msg.Recycle();
            return factory.Deserialize(Incoming(bytes), Environment.TickCount);
        }

        private static NetIncomingMessage Incoming(byte[] bytes)
        {
            var incoming = Peer.CreateIncomingMessage(NetIncomingMessageType.Data, bytes);
            incoming.LengthBytes = bytes.Length;
            return incoming;
        }

        /// <summary>A raw message with the standard headers, for crafting malformed payloads.</summary>
        private static NetOutgoingMessage RawHeader(ushort messageType, LogUploadMessageType subType, int uploadId)
        {
            var raw = Peer.CreateMessage();
            raw.Write(messageType);
            raw.Write((ushort)subType);
            raw.WritePadBits();
            raw.Write(0L); // sent time
            raw.Write((ushort)0);
            raw.Write((ushort)0);
            raw.Write((ushort)0);
            raw.WritePadBits();
            raw.Write(uploadId);
            return raw;
        }

        private static byte[] Bytes(NetOutgoingMessage raw)
        {
            return raw.ReadBytes(raw.LengthBytes);
        }

        [TestMethod]
        public void BeginRoundTrips()
        {
            var data = CliFactory.CreateNewMessageData<LogUploadBeginMsgData>();
            data.UploadId = 123456;
            data.FileKind = LogUploadFileKind.ClientLogsZip;
            data.TotalBytes = 4_100_000;
            data.ChunkSize = LogUploadLimits.ClientChunkBytes;
            data.ChunkCount = 251;
            var msg = CliFactory.CreateNew<LogUploadCliMsg>(data);
            Assert.AreEqual(LogUploadLimits.ClientChannel, msg.Channel);
            Assert.AreEqual(NetDeliveryMethod.ReliableOrdered, msg.NetDeliveryMethod);

            var result = (IClientMessageBase)RoundTrip(msg, CliFactory);
            Assert.AreEqual(ClientMessageType.LogUpload, result.MessageType);
            var begin = (LogUploadBeginMsgData)result.Data;
            Assert.AreEqual(LogUploadMessageType.Begin, begin.LogUploadMessageType);
            Assert.AreEqual(123456, begin.UploadId);
            Assert.AreEqual(LogUploadFileKind.ClientLogsZip, begin.FileKind);
            Assert.AreEqual(4_100_000, begin.TotalBytes);
            Assert.AreEqual(LogUploadLimits.ClientChunkBytes, begin.ChunkSize);
            Assert.AreEqual(251, begin.ChunkCount);
            Assert.AreEqual(LogUploadReason.Manual, begin.Reason, "an upload nobody labelled is a manual one");
        }

        [DataTestMethod]
        [DataRow(LogUploadReason.Manual)]
        [DataRow(LogUploadReason.Stall)]
        [DataRow(LogUploadReason.Errors)]
        [DataRow(LogUploadReason.Crash)]
        [DataRow(LogUploadReason.Requested)]
        [DataRow(LogUploadReason.Test)]
        public void BeginCarriesTheReason(LogUploadReason reason)
        {
            var data = CliFactory.CreateNewMessageData<LogUploadBeginMsgData>();
            data.UploadId = 9;
            data.FileKind = LogUploadFileKind.ClientLogsZip;
            data.TotalBytes = 1_000_000;
            data.ChunkSize = LogUploadLimits.ClientChunkBytes;
            data.ChunkCount = 62;
            data.Reason = reason;
            var msg = CliFactory.CreateNew<LogUploadCliMsg>(data);

            var begin = (LogUploadBeginMsgData)RoundTrip(msg, CliFactory).Data;
            Assert.AreEqual(reason, begin.Reason);
            Assert.AreEqual(9, begin.UploadId);
            Assert.AreEqual(62, begin.ChunkCount);
        }

        [TestMethod]
        public void ARecycledBeginMessageDoesNotKeepTheOldReason()
        {
            var first = CliFactory.CreateNewMessageData<LogUploadBeginMsgData>();
            first.Reason = LogUploadReason.Crash;
            first.FileKind = LogUploadFileKind.ClientLogsZip;
            first.TotalBytes = 200_000;
            first.ChunkSize = LogUploadLimits.ClientChunkBytes;
            first.ChunkCount = 13;
            var msg = CliFactory.CreateNew<LogUploadCliMsg>(first);
            RoundTrip(msg, CliFactory);

            var second = CliFactory.CreateNewMessageData<LogUploadBeginMsgData>();
            second.FileKind = LogUploadFileKind.ClientLogsZip;
            second.TotalBytes = 200_000;
            second.ChunkSize = LogUploadLimits.ClientChunkBytes;
            second.ChunkCount = 13;
            var msg2 = CliFactory.CreateNew<LogUploadCliMsg>(second);
            var begin = (LogUploadBeginMsgData)RoundTrip(msg2, CliFactory).Data;
            Assert.AreEqual(LogUploadReason.Manual, begin.Reason);
        }

        [TestMethod]
        public void TheReasonHelpersAgreeWithEachOther()
        {
            Assert.IsFalse(LogUploadReason.Manual.IsAutomatic());
            Assert.AreEqual(string.Empty, LogUploadReason.Manual.FileSuffix());
            foreach (LogUploadReason reason in Enum.GetValues(typeof(LogUploadReason)))
            {
                Assert.IsTrue(reason.IsKnown(), reason.ToString());
                if (reason == LogUploadReason.Manual) continue;
                Assert.IsTrue(reason.IsAutomatic(), reason.ToString());
                Assert.AreEqual("-" + reason.ToString().ToLowerInvariant(), reason.FileSuffix());
            }

            Assert.IsFalse(((LogUploadReason)99).IsKnown());
        }

        [TestMethod]
        public void ChunkRoundTripsOnlyTheUsedPartOfThePooledBuffer()
        {
            var payload = new byte[LogUploadLimits.ClientChunkBytes];
            new Random(5).NextBytes(payload);

            var data = CliFactory.CreateNewMessageData<LogUploadChunkMsgData>();
            data.UploadId = 77;
            data.ChunkIndex = 3;
            data.ChunkCount = 10;
            data.TotalBytes = 150_000;
            data.Data = new byte[payload.Length + 5000];
            Array.Copy(payload, data.Data, payload.Length);
            data.NumBytes = payload.Length;
            var msg = CliFactory.CreateNew<LogUploadCliMsg>(data);

            var result = (LogUploadChunkMsgData)RoundTrip(msg, CliFactory).Data;
            Assert.AreEqual(77, result.UploadId);
            Assert.AreEqual(3, result.ChunkIndex);
            Assert.AreEqual(10, result.ChunkCount);
            Assert.AreEqual(150_000, result.TotalBytes);
            Assert.AreEqual(payload.Length, result.NumBytes);
            CollectionAssert.AreEqual(payload, result.Data.Take(result.NumBytes).ToArray());
        }

        [TestMethod]
        public void MaximumSizeChunkRoundTrips()
        {
            var payload = new byte[LogUploadLimits.MaxChunkBytes];
            new Random(9).NextBytes(payload);
            var data = CliFactory.CreateNewMessageData<LogUploadChunkMsgData>();
            data.UploadId = 1;
            data.ChunkCount = 1;
            data.TotalBytes = payload.Length;
            data.Data = payload;
            data.NumBytes = payload.Length;

            var result = (LogUploadChunkMsgData)RoundTrip(CliFactory.CreateNew<LogUploadCliMsg>(data), CliFactory).Data;
            CollectionAssert.AreEqual(payload, result.Data.Take(result.NumBytes).ToArray());
        }

        [TestMethod]
        public void SerializingAnOversizeChunkFails()
        {
            var data = CliFactory.CreateNewMessageData<LogUploadChunkMsgData>();
            data.Data = new byte[LogUploadLimits.MaxChunkBytes + 1];
            data.NumBytes = data.Data.Length;
            var msg = CliFactory.CreateNew<LogUploadCliMsg>(data);
            Assert.ThrowsException<Exception>(() => msg.Serialize(Peer.CreateMessage()));
        }

        [TestMethod]
        public void CancelRoundTrips()
        {
            var data = CliFactory.CreateNewMessageData<LogUploadCancelMsgData>();
            data.UploadId = 42;
            var result = RoundTrip(CliFactory.CreateNew<LogUploadCliMsg>(data), CliFactory).Data as LogUploadCancelMsgData;
            Assert.IsNotNull(result);
            Assert.AreEqual(42, result.UploadId);
        }

        [TestMethod]
        public void AcceptedRoundTripsOnAnUnorderedReliableChannel()
        {
            var data = SrvFactory.CreateNewMessageData<LogUploadAcceptedMsgData>();
            data.UploadId = 9;
            var msg = SrvFactory.CreateNew<LogUploadSrvMsg>(data);
            Assert.AreEqual(NetDeliveryMethod.ReliableUnordered, msg.NetDeliveryMethod);
            Assert.AreEqual(0, msg.Channel);

            var result = (IServerMessageBase)RoundTrip(msg, SrvFactory);
            Assert.AreEqual(ServerMessageType.LogUpload, result.MessageType);
            Assert.AreEqual(9, ((LogUploadAcceptedMsgData)result.Data).UploadId);
        }

        [TestMethod]
        public void DoneRoundTrips()
        {
            var data = SrvFactory.CreateNewMessageData<LogUploadDoneMsgData>();
            data.UploadId = 10;
            data.StoredBytes = 4_299_000_123L;
            data.StoredName = "Vlad_20261024-153012Z.zip";
            var result = (LogUploadDoneMsgData)RoundTrip(SrvFactory.CreateNew<LogUploadSrvMsg>(data), SrvFactory).Data;
            Assert.AreEqual(10, result.UploadId);
            Assert.AreEqual(4_299_000_123L, result.StoredBytes);
            Assert.AreEqual("Vlad_20261024-153012Z.zip", result.StoredName);
        }

        [TestMethod]
        public void RefusedRoundTripsAndLongDetailIsTruncated()
        {
            var data = SrvFactory.CreateNewMessageData<LogUploadRefusedMsgData>();
            data.UploadId = 11;
            data.Reason = LogUploadRefusalReason.TooMany;
            data.Detail = "You can send logs 3 times per hour.";
            var result = (LogUploadRefusedMsgData)RoundTrip(SrvFactory.CreateNew<LogUploadSrvMsg>(data), SrvFactory).Data;
            Assert.AreEqual(11, result.UploadId);
            Assert.AreEqual(LogUploadRefusalReason.TooMany, result.Reason);
            Assert.AreEqual("You can send logs 3 times per hour.", result.Detail);

            var longData = SrvFactory.CreateNewMessageData<LogUploadRefusedMsgData>();
            longData.UploadId = 12;
            longData.Reason = LogUploadRefusalReason.Busy;
            longData.Detail = new string('x', 5000);
            var longResult = (LogUploadRefusedMsgData)RoundTrip(SrvFactory.CreateNew<LogUploadSrvMsg>(longData), SrvFactory).Data;
            Assert.AreEqual(LogUploadLimits.MaxReplyTextLength, longResult.Detail.Length);
        }

        [DataTestMethod]
        [DataRow(LogUploadLimits.MaxChunkBytes + 1, 16)]
        [DataRow(int.MaxValue, 16)]
        [DataRow(-1, 16)]
        [DataRow(100, 10)]
        public void ChunkDeserializationRejectsBadLengthsBeforeAllocating(int declared, int present)
        {
            var raw = RawHeader((ushort)ClientMessageType.LogUpload, LogUploadMessageType.Chunk, 1);
            raw.Write(0); // chunk index
            raw.Write(1); // chunk count
            raw.Write(present); // total bytes
            raw.Write(declared);
            raw.Write(new byte[present]);
            var bytes = Bytes(raw);
            Assert.ThrowsException<InvalidOperationException>(() => CliFactory.Deserialize(Incoming(bytes), 0));
        }

        [TestMethod]
        public void OverlongServerTextIsRejectedOnRead()
        {
            var raw = RawHeader((ushort)ServerMessageType.LogUpload, LogUploadMessageType.Refused, 1);
            raw.Write((byte)LogUploadRefusalReason.Busy);
            raw.Write(new string('y', LogUploadLimits.MaxReplyTextLength + 1));
            var bytes = Bytes(raw);
            Assert.ThrowsException<InvalidOperationException>(() => SrvFactory.Deserialize(Incoming(bytes), 0));
        }

        [TestMethod]
        public void ClientFactoryDoesNotAcceptServerOnlySubtypes()
        {
            foreach (var serverOnly in new[] { LogUploadMessageType.Accepted, LogUploadMessageType.Done, LogUploadMessageType.Refused })
            {
                var bytes = Bytes(RawHeader((ushort)ClientMessageType.LogUpload, serverOnly, 1));
                Assert.ThrowsException<Exception>(() => CliFactory.Deserialize(Incoming(bytes), 0), serverOnly.ToString());
            }

            foreach (var clientOnly in new[] { LogUploadMessageType.Begin, LogUploadMessageType.Chunk, LogUploadMessageType.Cancel })
            {
                var bytes = Bytes(RawHeader((ushort)ServerMessageType.LogUpload, clientOnly, 1));
                Assert.ThrowsException<Exception>(() => SrvFactory.Deserialize(Incoming(bytes), 0), clientOnly.ToString());
            }
        }
    }
}
