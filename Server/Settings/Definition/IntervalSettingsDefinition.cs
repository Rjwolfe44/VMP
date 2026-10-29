using LmpCommon.Xml;
using System;

namespace Server.Settings.Definition
{
    [Serializable]
    public class IntervalSettingsDefinition
    {
        [XmlComment(Value = "Interval in ms at which the client will send POSITION and FLIGHTSTATE updates of their vessel when other players are NEARBY. " +
                "Decrease it if your clients have good network connection and you plan to do dogfights, although in that case consider using interpolation aswell")]
        public int VesselUpdatesMsInterval { get; set; } = 50;

        [XmlComment(Value = "Interval in ms at which the client will send POSITION and FLIGHTSTATE updates for vessels that are uncontrolled and nearby them. " +
                            "This interval is also applied used to send position updates of HIS OWN vessel when NOBODY is around")]
        public int SecondaryVesselUpdatesMsInterval { get; set; } = 150;

        [XmlComment(Value = "Enable an ultra-high-rate POSITION stream when another player vessel is very close. This is intended for docking and can be disabled for very bandwidth-constrained servers.")]
        public bool ProximityHighRateEnabled { get; set; } = true;

        [XmlComment(Value = "Interval in ms for the proximity high-rate POSITION stream. 16ms is about 60Hz. Increase to 33ms for about 30Hz if bandwidth is constrained.")]
        public int ProximityHighRateMsInterval { get; set; } = 16;

        [XmlComment(Value = "Distance in meters at which proximity high-rate POSITION streaming activates.")]
        public float ProximityHighRateRangeMeters { get; set; } = 150f;

        [XmlComment(Value = "Enable reduced-rate POSITION updates for secondary vessels that are idle or landed/splashed.")]
        public bool IdleVesselDetectionEnabled { get; set; } = true;

        [XmlComment(Value = "Interval in ms for idle secondary vessel POSITION updates.")]
        public int IdleVesselUpdatesMsInterval { get; set; } = 2000;

        [XmlComment(Value = "Surface speed in m/s below which a secondary vessel is considered idle when throttle/RCS are also inactive.")]
        public float IdleVesselSpeedThresholdMs { get; set; } = 0.5f;

        [XmlComment(Value = "Send/Receive tick clock. Keep this value low but at least above 2ms to avoid extreme CPU usage.")]
        public int SendReceiveThreadTickMs { get; set; } = 5;

        [XmlComment(Value = "Main thread polling in ms. Keep this value low but at least above 2ms to avoid extreme CPU usage.")]
        public int MainTimeTick { get; set; } = 5;

        [XmlComment(Value = "Interval in ms at which internal LMP structures (Subspaces, Vessels, Scenario files, ...) will be backed up to a file")]
        public int BackupIntervalMs { get; set; } = 30000;

        [XmlComment(Value = "Interval to force a garbage collection and reduce the memory usage. Specify this value in minutes. 0 = deactivated.")]
        public int GcMinutesInterval { get; set; } = 15;
    }
}
