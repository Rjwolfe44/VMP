using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Vessel
{
    /// <summary>
    /// Bitmask indicating which field groups are present in a <see cref="VesselPositionMsgData"/> payload.
    /// Fields whose bit is NOT set are omitted from the wire; the receiver fills them in from its
    /// last-received snapshot for that vessel.  Defaults to <see cref="All"/> so that messages created
    /// without an explicit delta calculation are fully self-contained.
    /// </summary>
    [Flags]
    public enum PositionDeltaFields : ushort
    {
        None              = 0,
        Body              = 1 << 0,  // BodyIndex + BodyName
        SubspaceId        = 1 << 1,
        HeightFromTerrain = 1 << 2,
        SurfaceFlags      = 1 << 3,  // Landed + Splashed + HackingGravity
        LatLonAlt         = 1 << 4,
        VelocityVector    = 1 << 5,
        NormalVector      = 1 << 6,
        SrfRelRotation    = 1 << 7,
        Orbit             = 1 << 8,
        AngularVelocity   = 1 << 9,
        All               = Body | SubspaceId | HeightFromTerrain | SurfaceFlags |
                            LatLonAlt | VelocityVector | NormalVector | SrfRelRotation | Orbit | AngularVelocity
    }

    public class VesselPositionMsgData : VesselBaseMsgData
    {
        /// <inheritdoc />
        internal VesselPositionMsgData() { }
        public override VesselMessageType VesselMessageType => VesselMessageType.Position;

        /// <summary>Which field groups are present in this message.</summary>
        public PositionDeltaFields DeltaFields = PositionDeltaFields.All;
        public ushort SequenceNumber;

        public int BodyIndex;
        public string BodyName;
        public int SubspaceId;
        public float PingSec;
        public float HeightFromTerrain;
        public bool Landed;
        public bool Splashed;
        public bool HackingGravity;
        public double[] LatLonAlt = new double[3];
        public double[] VelocityVector = new double[3];
        public double[] NormalVector = new double[3];
        public float[] SrfRelRotation = new float[4];
        public float[] AngVelocityVector = new float[3];
        public double[] Orbit = new double[8];

        public override string ClassName { get; } = nameof(VesselPositionMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);

            // Write the delta bitmask first so the receiver can skip absent fields.
            lidgrenMsg.Write((ushort)DeltaFields);
            lidgrenMsg.Write(SequenceNumber);

            // PingSec is always included — the receiver needs it to compute interpolation offset.
            lidgrenMsg.Write(PingSec);

            if ((DeltaFields & PositionDeltaFields.Body) != 0)
            {
                lidgrenMsg.Write(BodyIndex);
                lidgrenMsg.Write(BodyName ?? string.Empty);
            }

            if ((DeltaFields & PositionDeltaFields.SubspaceId) != 0)
                lidgrenMsg.Write(SubspaceId);

            if ((DeltaFields & PositionDeltaFields.HeightFromTerrain) != 0)
                lidgrenMsg.Write(HeightFromTerrain);

            if ((DeltaFields & PositionDeltaFields.SurfaceFlags) != 0)
            {
                lidgrenMsg.Write(Landed);
                lidgrenMsg.Write(Splashed);
                lidgrenMsg.Write(HackingGravity);
            }

            if ((DeltaFields & PositionDeltaFields.LatLonAlt) != 0)
                for (var i = 0; i < 3; i++) lidgrenMsg.Write(LatLonAlt[i]);

            if ((DeltaFields & PositionDeltaFields.VelocityVector) != 0)
                for (var i = 0; i < 3; i++) lidgrenMsg.Write(VelocityVector[i]);

            if ((DeltaFields & PositionDeltaFields.NormalVector) != 0)
                for (var i = 0; i < 3; i++) lidgrenMsg.Write(NormalVector[i]);

            if ((DeltaFields & PositionDeltaFields.SrfRelRotation) != 0)
                for (var i = 0; i < 4; i++) lidgrenMsg.Write(SrfRelRotation[i]);

            if ((DeltaFields & PositionDeltaFields.AngularVelocity) != 0)
                for (var i = 0; i < 3; i++) lidgrenMsg.Write(AngVelocityVector[i]);

            if ((DeltaFields & PositionDeltaFields.Orbit) != 0)
                for (var i = 0; i < 8; i++) lidgrenMsg.Write(Orbit[i]);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);

            DeltaFields = (PositionDeltaFields)lidgrenMsg.ReadUInt16();
            SequenceNumber = lidgrenMsg.ReadUInt16();

            PingSec = lidgrenMsg.ReadFloat();

            if ((DeltaFields & PositionDeltaFields.Body) != 0)
            {
                BodyIndex = lidgrenMsg.ReadInt32();
                BodyName = lidgrenMsg.ReadString();
            }

            if ((DeltaFields & PositionDeltaFields.SubspaceId) != 0)
                SubspaceId = lidgrenMsg.ReadInt32();

            if ((DeltaFields & PositionDeltaFields.HeightFromTerrain) != 0)
                HeightFromTerrain = lidgrenMsg.ReadFloat();

            if ((DeltaFields & PositionDeltaFields.SurfaceFlags) != 0)
            {
                Landed = lidgrenMsg.ReadBoolean();
                Splashed = lidgrenMsg.ReadBoolean();
                HackingGravity = lidgrenMsg.ReadBoolean();
            }

            if ((DeltaFields & PositionDeltaFields.LatLonAlt) != 0)
                for (var i = 0; i < 3; i++) LatLonAlt[i] = lidgrenMsg.ReadDouble();

            if ((DeltaFields & PositionDeltaFields.VelocityVector) != 0)
                for (var i = 0; i < 3; i++) VelocityVector[i] = lidgrenMsg.ReadDouble();

            if ((DeltaFields & PositionDeltaFields.NormalVector) != 0)
                for (var i = 0; i < 3; i++) NormalVector[i] = lidgrenMsg.ReadDouble();

            if ((DeltaFields & PositionDeltaFields.SrfRelRotation) != 0)
                for (var i = 0; i < 4; i++) SrfRelRotation[i] = lidgrenMsg.ReadFloat();

            if ((DeltaFields & PositionDeltaFields.AngularVelocity) != 0)
                for (var i = 0; i < 3; i++) AngVelocityVector[i] = lidgrenMsg.ReadFloat();

            if ((DeltaFields & PositionDeltaFields.Orbit) != 0)
                for (var i = 0; i < 8; i++) Orbit[i] = lidgrenMsg.ReadDouble();
        }

        internal override int InternalGetMessageSize()
        {
            // Fixed overhead: base + 2-byte flags + 2-byte sequence + 4-byte PingSec
            var size = base.InternalGetMessageSize() + sizeof(ushort) + sizeof(ushort) + sizeof(float);

            if ((DeltaFields & PositionDeltaFields.Body) != 0)
                size += sizeof(int) + (BodyName ?? string.Empty).GetByteCount();

            if ((DeltaFields & PositionDeltaFields.SubspaceId) != 0)
                size += sizeof(int);

            if ((DeltaFields & PositionDeltaFields.HeightFromTerrain) != 0)
                size += sizeof(float);

            if ((DeltaFields & PositionDeltaFields.SurfaceFlags) != 0)
                size += sizeof(bool) * 3;

            if ((DeltaFields & PositionDeltaFields.LatLonAlt) != 0)
                size += sizeof(double) * 3;

            if ((DeltaFields & PositionDeltaFields.VelocityVector) != 0)
                size += sizeof(double) * 3;

            if ((DeltaFields & PositionDeltaFields.NormalVector) != 0)
                size += sizeof(double) * 3;

            if ((DeltaFields & PositionDeltaFields.SrfRelRotation) != 0)
                size += sizeof(float) * 4;

            if ((DeltaFields & PositionDeltaFields.AngularVelocity) != 0)
                size += sizeof(float) * 3;

            if ((DeltaFields & PositionDeltaFields.Orbit) != 0)
                size += sizeof(double) * 8;

            return size;
        }
    }
}
