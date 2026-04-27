using Server.Settings.Structures;

namespace Server.Web.Structures.Settings
{
    public class ServerIntervalSettings
    {
        public int VesselUpdatesMsInterval => IntervalSettings.SettingsStore.VesselUpdatesMsInterval;
        public int SecondaryVesselUpdatesMsInterval => IntervalSettings.SettingsStore.SecondaryVesselUpdatesMsInterval;
        public bool ProximityHighRateEnabled => IntervalSettings.SettingsStore.ProximityHighRateEnabled;
        public int ProximityHighRateMsInterval => IntervalSettings.SettingsStore.ProximityHighRateMsInterval;
        public float ProximityHighRateRangeMeters => IntervalSettings.SettingsStore.ProximityHighRateRangeMeters;
        public bool IdleVesselDetectionEnabled => IntervalSettings.SettingsStore.IdleVesselDetectionEnabled;
        public int IdleVesselUpdatesMsInterval => IntervalSettings.SettingsStore.IdleVesselUpdatesMsInterval;
        public float IdleVesselSpeedThresholdMs => IntervalSettings.SettingsStore.IdleVesselSpeedThresholdMs;
        public int SendReceiveThreadTickMs => IntervalSettings.SettingsStore.SendReceiveThreadTickMs;
        public int MainTimeTick => IntervalSettings.SettingsStore.MainTimeTick;
        public int BackupIntervalMs => IntervalSettings.SettingsStore.BackupIntervalMs;
    }
}
