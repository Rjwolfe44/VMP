using Lidgren.Network;
using LmpCommon.Message;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Server;
using LmpCommon.Sync;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace LmpCommonTest
{
    [TestClass]
    public class VesselPositionSyncTests
    {
        private static readonly ServerMessageFactory Factory = new ServerMessageFactory();
        private static readonly NetClient Client = new NetClient(new NetPeerConfiguration("TESTS"));

        private const int DefaultSendMs = 50;

        [TestMethod]
        public void PositionRenderOffset_UsesSenderPingOnly_SameForAllObservers()
        {
            const float senderPing = 0.12f;

            var observerLowLatency = VesselPositionSyncMath.PositionRenderOffsetSec(senderPing, DefaultSendMs);
            var observerHighLatency = VesselPositionSyncMath.PositionRenderOffsetSec(senderPing, DefaultSendMs);

            Assert.AreEqual(observerLowLatency, observerHighLatency, 1e-6f);
            Assert.AreEqual(0.12f, observerLowLatency, 1e-6f);
        }

        [TestMethod]
        public void PositionRenderOffset_EnforcesAdaptiveFloor()
        {
            // 50ms send interval → floor max(100ms, 100ms) = 100ms
            Assert.AreEqual(0.100f, VesselPositionSyncMath.PositionRenderOffsetSec(0.01f, 50), 1e-6f);

            // 200ms send interval → floor max(100ms, 400ms) = 400ms
            Assert.AreEqual(0.400f, VesselPositionSyncMath.PositionRenderOffsetSec(0.05f, 200), 1e-6f);
        }

        [TestMethod]
        public void PositionRenderOffset_ClampsToMax()
        {
            Assert.AreEqual(2.5f, VesselPositionSyncMath.PositionRenderOffsetSec(9f, DefaultSendMs), 1e-6f);
        }

        [TestMethod]
        public void TryAcceptSequence_RejectsOutOfOrderAndDuplicates()
        {
            var state = new VesselPositionRecvState();

            Assert.IsTrue(VesselPositionSyncMath.TryAcceptSequence(1, ref state));
            Assert.IsFalse(VesselPositionSyncMath.TryAcceptSequence(1, ref state));
            Assert.IsFalse(VesselPositionSyncMath.TryAcceptSequence(0, ref state));
            Assert.IsTrue(VesselPositionSyncMath.TryAcceptSequence(2, ref state));
        }

        [TestMethod]
        public void TryAcceptSequence_AcceptsUshortWraparound()
        {
            var state = new VesselPositionRecvState { HasSequenceNumber = true, LastSequenceNumber = ushort.MaxValue };

            Assert.IsTrue(VesselPositionSyncMath.TryAcceptSequence(0, ref state));
            Assert.AreEqual((ushort)0, state.LastSequenceNumber);
        }

        [TestMethod]
        public void ApplyOutgoingDelta_ForcesFullEveryNMessages()
        {
            var state = new VesselPositionSentState();
            var msg = CreateSampleMessage();

            VesselPositionSyncMath.ApplyOutgoingDelta(msg, ref state);
            Assert.AreEqual(PositionDeltaFields.All, msg.DeltaFields);

            for (var i = 0; i < 28; i++)
            {
                VesselPositionSyncMath.ApplyOutgoingDelta(msg, ref state);
                Assert.AreNotEqual(PositionDeltaFields.All, msg.DeltaFields);
            }

            VesselPositionSyncMath.ApplyOutgoingDelta(msg, ref state);
            Assert.AreNotEqual(PositionDeltaFields.All, msg.DeltaFields);
            Assert.AreEqual((ushort)30, msg.SequenceNumber);

            VesselPositionSyncMath.ApplyOutgoingDelta(msg, ref state);
            Assert.AreEqual(PositionDeltaFields.All, msg.DeltaFields);
            Assert.AreEqual((ushort)31, msg.SequenceNumber);
        }

        [TestMethod]
        public void ApplyOutgoingDelta_IncludesOrbitWhenForcedForProximity()
        {
            var state = new VesselPositionSentState();
            var msg = CreateSampleMessage();
            VesselPositionSyncMath.ApplyOutgoingDelta(msg, ref state, forceIncludeOrbit: false);
            Assert.AreEqual((ushort)1, msg.SequenceNumber);

            msg.Orbit[5] += 1e-8;
            VesselPositionSyncMath.ApplyOutgoingDelta(msg, ref state, forceIncludeOrbit: true);
            Assert.IsTrue((msg.DeltaFields & PositionDeltaFields.Orbit) != 0);
        }

        [TestMethod]
        public void ReconstructIncomingDelta_RoundTripsPartialMessage()
        {
            var sentState = new VesselPositionSentState();
            var recvState = new VesselPositionRecvState();
            var full = CreateSampleMessage();
            VesselPositionSyncMath.ApplyOutgoingDelta(full, ref sentState, forceIncludeOrbit: true);
            recvState.CopyFrom(full);

            var partial = CreateSampleMessage();
            partial.LatLonAlt[0] += 0.001;
            partial.VelocityVector[1] += 0.5;
            partial.SequenceNumber = (ushort)(full.SequenceNumber + 1);
            VesselPositionSyncMath.ApplyOutgoingDelta(partial, ref sentState, forceIncludeOrbit: false);

            var wire = CloneMessage(partial);
            VesselPositionSyncMath.ReconstructIncomingDelta(wire, ref recvState);

            Assert.AreEqual(partial.LatLonAlt[0], wire.LatLonAlt[0], 1e-12);
            Assert.AreEqual(partial.VelocityVector[1], wire.VelocityVector[1], 1e-12);
            Assert.AreEqual(full.BodyIndex, wire.BodyIndex);
            Assert.AreEqual(full.Orbit[2], wire.Orbit[2], 1e-12);
            Assert.AreEqual(full.SrfRelRotation[3], wire.SrfRelRotation[3], 1e-6f);
        }

        [TestMethod]
        public void ComputeTimeDifference_MatchesRenderOffset()
        {
            const double ut = 1000.0;
            const double gameTime = 999.88;
            const float senderPing = 0.12f;
            var offset = VesselPositionSyncMath.PositionRenderOffsetSec(senderPing, DefaultSendMs);

            var diff = VesselPositionSyncMath.ComputeTimeDifference(ut, gameTime, senderPing, DefaultSendMs);
            Assert.AreEqual(ut - gameTime - offset, diff, 1e-9);
        }

        [TestMethod]
        public void ComputeInterpolationFix_ReturnsZeroBelowOneFrame()
        {
            Assert.AreEqual(0, VesselPositionSyncMath.ComputeInterpolationFixSec(0.01, 0.02), 1e-9);
        }

        [TestMethod]
        public void ComputeInterpolationFix_ScalesWithError()
        {
            const double dt = 0.02;
            Assert.AreEqual(dt, VesselPositionSyncMath.ComputeInterpolationFixSec(0.03, dt), 1e-9);
            Assert.AreEqual(dt * 2, VesselPositionSyncMath.ComputeInterpolationFixSec(0.07, dt), 1e-9);
        }

        [TestMethod]
        public void IsPositionUpdateTooOld_DropsStaleSnapshots()
        {
            const double ut = 100.0f;
            const float senderPing = 0.1f;
            var offset = VesselPositionSyncMath.PositionRenderOffsetSec(senderPing, DefaultSendMs);

            Assert.IsTrue(VesselPositionSyncMath.IsPositionUpdateTooOld(ut - offset - 0.01, senderPing, ut, DefaultSendMs));
            Assert.IsFalse(VesselPositionSyncMath.IsPositionUpdateTooOld(ut - offset + 0.01, senderPing, ut, DefaultSendMs));
        }

        [TestMethod]
        public void DeltaSerialization_RoundTripsThroughWireFormat()
        {
            var sentState = new VesselPositionSentState();
            var recvState = new VesselPositionRecvState();

            var msg = CreateSampleMessage();
            VesselPositionSyncMath.ApplyOutgoingDelta(msg, ref sentState, forceIncludeOrbit: true);
            recvState.CopyFrom(msg);

            msg = CreateSampleMessage();
            msg.LatLonAlt[2] += 10;
            VesselPositionSyncMath.ApplyOutgoingDelta(msg, ref sentState);

            var srv = Factory.CreateNew<VesselSrvMsg, VesselPositionMsgData>();
            CopyMessageData(msg, (VesselPositionMsgData)srv.Data);

            var lidgrenSend = Client.CreateMessage(srv.GetMessageSize());
            srv.Serialize(lidgrenSend);
            var data = lidgrenSend.ReadBytes(lidgrenSend.LengthBytes);
            var lidgrenRecv = Client.CreateIncomingMessage(NetIncomingMessageType.Data, data);
            lidgrenRecv.LengthBytes = lidgrenSend.LengthBytes;

            var parsed = Factory.Deserialize(lidgrenRecv, Environment.TickCount);
            var parsedData = parsed.Data as VesselPositionMsgData;
            Assert.IsNotNull(parsedData);
            VesselPositionSyncMath.ReconstructIncomingDelta(parsedData, ref recvState);
            Assert.AreEqual(msg.LatLonAlt[2], parsedData.LatLonAlt[2], 1e-9);
            Assert.AreEqual(sentState.Orbit[2], parsedData.Orbit[2], 1e-9);
        }

        private static VesselPositionMsgData CreateSampleMessage()
        {
            var msg = Factory.CreateNewMessageData<VesselPositionMsgData>();
            msg.VesselId = Guid.NewGuid();
            msg.PingSec = 0.1f;
            msg.BodyIndex = 1;
            msg.BodyName = "Kerbin";
            msg.SubspaceId = 0;
            msg.HeightFromTerrain = 120f;
            msg.LatLonAlt[0] = 10;
            msg.LatLonAlt[1] = 20;
            msg.LatLonAlt[2] = 80000;
            msg.VelocityVector[0] = 100;
            msg.VelocityVector[1] = 50;
            msg.VelocityVector[2] = 0;
            msg.NormalVector[2] = 1;
            msg.SrfRelRotation[3] = 1f;
            msg.Orbit[0] = 45;
            msg.Orbit[1] = 0.01;
            msg.Orbit[2] = 700000;
            msg.Orbit[5] = 1.5;
            msg.Orbit[6] = 100;
            msg.Orbit[7] = 1;
            return msg;
        }

        private static void CopyMessageData(VesselPositionMsgData source, VesselPositionMsgData target)
        {
            target.VesselId = source.VesselId;
            target.DeltaFields = source.DeltaFields;
            target.SequenceNumber = source.SequenceNumber;
            target.PingSec = source.PingSec;
            target.BodyIndex = source.BodyIndex;
            target.BodyName = source.BodyName;
            target.SubspaceId = source.SubspaceId;
            target.HeightFromTerrain = source.HeightFromTerrain;
            target.Landed = source.Landed;
            target.Splashed = source.Splashed;
            target.HackingGravity = source.HackingGravity;
            Array.Copy(source.LatLonAlt, target.LatLonAlt, 3);
            Array.Copy(source.VelocityVector, target.VelocityVector, 3);
            Array.Copy(source.NormalVector, target.NormalVector, 3);
            Array.Copy(source.SrfRelRotation, target.SrfRelRotation, 4);
            Array.Copy(source.AngVelocityVector, target.AngVelocityVector, 3);
            Array.Copy(source.Orbit, target.Orbit, 8);
        }

        private static VesselPositionMsgData CloneMessage(VesselPositionMsgData source)
        {
            var msg = Factory.CreateNewMessageData<VesselPositionMsgData>();
            msg.DeltaFields = source.DeltaFields;
            msg.SequenceNumber = source.SequenceNumber;
            msg.VesselId = source.VesselId;
            msg.PingSec = source.PingSec;
            msg.BodyIndex = source.BodyIndex;
            msg.BodyName = source.BodyName;
            msg.SubspaceId = source.SubspaceId;
            msg.HeightFromTerrain = source.HeightFromTerrain;
            msg.Landed = source.Landed;
            msg.Splashed = source.Splashed;
            msg.HackingGravity = source.HackingGravity;
            Array.Copy(source.LatLonAlt, msg.LatLonAlt, 3);
            Array.Copy(source.VelocityVector, msg.VelocityVector, 3);
            Array.Copy(source.NormalVector, msg.NormalVector, 3);
            Array.Copy(source.SrfRelRotation, msg.SrfRelRotation, 4);
            Array.Copy(source.AngVelocityVector, msg.AngVelocityVector, 3);
            Array.Copy(source.Orbit, msg.Orbit, 8);
            return msg;
        }
    }
}
