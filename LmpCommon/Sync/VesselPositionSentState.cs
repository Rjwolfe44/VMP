using LmpCommon.Message.Data.Vessel;
using System;

namespace LmpCommon.Sync
{
    /// <summary>Last transmitted position values for one vessel (delta encode).</summary>
    public class VesselPositionSentState
    {
        public int SendCount;
        public ushort SequenceNumber;
        public int BodyIndex;
        public string BodyName;
        public int SubspaceId;
        public bool Landed, Splashed, HackingGravity;
        public readonly double[] Orbit = new double[8];
        public readonly float[] AngVelocityVector = new float[3];

        public void CopyFrom(VesselPositionMsgData m)
        {
            BodyIndex = m.BodyIndex;
            BodyName = m.BodyName;
            SubspaceId = m.SubspaceId;
            Landed = m.Landed;
            Splashed = m.Splashed;
            HackingGravity = m.HackingGravity;
            Array.Copy(m.AngVelocityVector, AngVelocityVector, 3);
            Array.Copy(m.Orbit, Orbit, 8);
        }
    }
}
