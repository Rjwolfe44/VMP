using LmpCommon.Message.Data.Vessel;
using System;

namespace LmpCommon.Sync
{
    /// <summary>Last fully-reconstructed received position state for one vessel (delta decode).</summary>
    public class VesselPositionRecvState
    {
        public bool HasSequenceNumber;
        public ushort LastSequenceNumber;
        public int BodyIndex;
        public string BodyName;
        public int SubspaceId;
        public float HeightFromTerrain;
        public bool Landed, Splashed, HackingGravity;
        public readonly double[] LatLonAlt = new double[3];
        public readonly double[] VelocityVector = new double[3];
        public readonly double[] NormalVector = new double[3];
        public readonly float[] SrfRelRotation = new float[4];
        public readonly float[] AngVelocityVector = new float[3];
        public readonly double[] Orbit = new double[8];

        public void CopyFrom(VesselPositionMsgData m)
        {
            BodyIndex = m.BodyIndex;
            BodyName = m.BodyName;
            SubspaceId = m.SubspaceId;
            HeightFromTerrain = m.HeightFromTerrain;
            Landed = m.Landed;
            Splashed = m.Splashed;
            HackingGravity = m.HackingGravity;
            Array.Copy(m.LatLonAlt, LatLonAlt, 3);
            Array.Copy(m.VelocityVector, VelocityVector, 3);
            Array.Copy(m.NormalVector, NormalVector, 3);
            Array.Copy(m.SrfRelRotation, SrfRelRotation, 4);
            Array.Copy(m.AngVelocityVector, AngVelocityVector, 3);
            Array.Copy(m.Orbit, Orbit, 8);
        }
    }
}
