using System;

namespace Server.Settings.Base
{
    public interface ISettings
    {
        void Load();
        void Save();

        /// <summary>Name of the section in Config/vmp-server.json: the definition name without "SettingsDefinition", camelCase.</summary>
        string SectionId { get; }

        /// <summary>The file this section used before the unified store existed (only read by the one-time import).</summary>
        string LegacyFilename { get; }

        Type DefinitionType { get; }

        /// <summary>The live settings object (the static SettingsStore of the section).</summary>
        object Store { get; set; }

        /// <summary>A new object holding the default values of this section.</summary>
        object CreateDefaults();
    }
}
