using Server.Settings.Base;
using Server.Settings.Definition;

namespace Server.Settings.Structures
{
    public class AdminApiSettings : SettingsBase<AdminApiSettingsDefinition>
    {
        protected override string Filename => "AdminApiSettings.xml";
    }
}
