using Server.Settings.Base;
using Server.Settings.Definition;

namespace Server.Settings.Structures
{
    public class CompressionSettings : SettingsBase<CompressionSettingsDefinition>
    {
        protected override string Filename => "CompressionSettings.xml";
    }
}
