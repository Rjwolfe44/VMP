using System;

namespace Server.Settings.Base
{
    /// <summary>
    /// One section of the server settings. The values live in <see cref="SettingsStore"/>; where they are stored is the job of
    /// <see cref="SettingsFile"/> (Config/vmp-server.json). <see cref="Load"/> reads the whole file once (all sections), <see cref="Save"/> writes it.
    /// </summary>
    public abstract class SettingsBase<T> : ISettings
        where T : class, new()
    {
        /// <summary>Name of the XML file of this section before the unified store (see <see cref="SettingsFile"/> for the import).</summary>
        protected abstract string Filename { get; }
        public static T SettingsStore { get; private set; } = new T();

        public string SectionId => SettingsCatalog.SectionIdOf(typeof(T));
        public string LegacyFilename => Filename;
        public Type DefinitionType => typeof(T);

        object ISettings.Store
        {
            get => SettingsStore;
            set => SettingsStore = (T)value;
        }

        public virtual object CreateDefaults() => new T();

        public virtual void Load() => SettingsFile.Current.Load();

        public void Save() => SettingsFile.Current.Save();
    }
}
