using LmpCommon.Enums;
using Server.Context;
using Server.Log;
using Server.Settings.Definition;
using Server.Settings.Structures;
using System.Linq;

namespace Server.Settings
{
    public static class SettingsHandler
    {
        /// <summary>Loads every section from Config/vmp-server.json (see <see cref="SettingsFile"/>; the old XML files are imported once). General is loaded before Gameplay.</summary>
        public static void LoadSettings()
        {
            SettingsFile.Current = SettingsFile.Create(ServerContext.ConfigDirectory);
            SettingsFile.Current.Load();
        }

        public static void ValidateDifficultySettings()
        {
            if (GeneralSettings.SettingsStore.GameDifficulty == GameDifficulty.Custom)
                return;

            if (GeneralSettings.SettingsStore.GameDifficulty != GameDifficulty.Custom &&
                HasDifferencesAgainstGivenSetting(GeneralSettings.SettingsStore.GameDifficulty))
            {
                LunaLog.Info("Your gameplay settings are different than your General - GameDifficulty value. " +
                                "So the difficulty setting will be set as \"Custom\". " +
                                $"In case you want to use the default setting values for {GeneralSettings.SettingsStore.GameDifficulty}, " +
                                "remove the \"gameplay\" section from Config/vmp-server.json (with the server stopped) so it's recreated again");

                GeneralSettings.SettingsStore.GameDifficulty = GameDifficulty.Custom;
            }
        }

        private static bool HasDifferencesAgainstGivenSetting(GameDifficulty difficulty)
        {
            var definition = new GameplaySettingsDefinition();
            switch (difficulty)
            {
                case GameDifficulty.Easy:
                    definition.SetEasy();
                    break;
                case GameDifficulty.Normal:
                    definition.SetNormal();
                    break;
                case GameDifficulty.Moderate:
                    definition.SetModerate();
                    break;
                case GameDifficulty.Hard:
                    definition.SetHard();
                    break;
            }

            return typeof(GameplaySettingsDefinition).GetProperties()
                .Select(p => new
                {
                    value = p.GetValue(GameplaySettings.SettingsStore).ToString(),
                    defaultVal = p.GetValue(definition).ToString()
                })
                .Any(v => v.value != v.defaultVal);
        }
    }
}
