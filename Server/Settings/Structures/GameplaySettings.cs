using LmpCommon.Enums;
using Server.Settings.Base;
using Server.Settings.Definition;

namespace Server.Settings.Structures
{
    public class GameplaySettings : SettingsBase<GameplaySettingsDefinition>
    {
        protected override string Filename => "GameplaySettings.xml";

        /// <summary>The defaults of this section depend on the difficulty, so General must be loaded before this one.</summary>
        public override object CreateDefaults()
        {
            var definition = new GameplaySettingsDefinition();
            switch (GeneralSettings.SettingsStore.GameDifficulty)
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
                case GameDifficulty.Custom:
                    definition.SetNormal();
                    break;
            }

            return definition;
        }
    }
}
