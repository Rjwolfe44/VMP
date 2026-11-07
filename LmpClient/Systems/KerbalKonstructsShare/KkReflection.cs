using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace LmpClient.Systems.KerbalKonstructsShare
{
    /// <summary>
    /// Every Kerbal Konstructs member the static sharing uses, resolved once by name. KK is optional and not referenced
    /// at compile time. If any member is missing (KK absent, or a version that renamed something) <see cref="TryCreate"/>
    /// fails with the list of missing names and sharing stays off; nothing else changes.
    /// Checked against KerbalKonstructs.dll file version 1.12.3.0 (assembly 2.0.0.0).
    /// </summary>
    internal sealed class KkReflection
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        public Type StaticInstanceType, GroupCenterType, SettingsType, KkType;

        //StaticInstance
        public FieldInfo SiUuid, SiBody, SiGroup, SiRelativePosition, SiRadialPosition, SiRefLatitude, SiRefLongitude, SiModel,
            SiConfigPath, SiConfigUrl, SiIsInSavegame, SiGroupCenter, SiLaunchSite, SiHasLaunchSites, SiMyFacilities, SiGrassConfigs;
        public MethodInfo SiOrientate, SiDestroy, SiDeactivate, SiActivate, SiSaveConfig;
        public ConstructorInfo SiCtor;

        //StaticDatabase
        public FieldInfo SdInstancedByUuid, SdAllStaticInstances, SdAllStaticModels;
        public MethodInfo SdHasInstance, SdHasGroupCenter, SdGetGroupCenter;

        //Model, launch site, facility
        public FieldInfo ModelName, LaunchSiteName, FacilityStaticInstance;
        //Open/closed state that KK's cfg writer changes as a side effect (restored after a snapshot)
        public FieldInfo LsOpenState, LsOpenString, FacOpenState, FacOpenString;

        //GroupCenter
        public FieldInfo GcIsBuiltIn, GcIsInSavegame, GcIsActive, GcLaunchsites;

        //Static helpers
        public MethodInfo ParseInstanceConfig, WriteInstanceConfig, AttachLaunchSite, AttachFacilities, SaveObjects, SelectInstance, CareerStateLoad;
        public FieldInfo KkGameTime, KkConvertLegacy, KkSelectedInstance;

        //Scenario module
        public MethodInfo SettingsOnLoad, SettingsOnSave;

        public static KkReflection TryCreate(out string problem)
        {
            problem = null;
            var r = new KkReflection();
            var missing = new List<string>();

            Type T(string name)
            {
                var t = AccessTools.TypeByName(name);
                if (t == null) missing.Add(name);
                return t;
            }

            FieldInfo F(Type t, string name)
            {
                var f = t == null ? null : t.GetField(name, All);
                if (f == null) missing.Add((t?.Name ?? "?") + "." + name);
                return f;
            }


            MethodInfo M(Type t, string name, params Type[] args)
            {
                MethodInfo m = null;
                if (t != null && args.Length > 0 && Array.TrueForAll(args, a => a != null))
                    m = t.GetMethod(name, All, null, args, null);
                else if (t != null && args.Length == 0)
                    m = t.GetMethod(name, All, null, Type.EmptyTypes, null);
                if (m == null) missing.Add((t?.Name ?? "?") + "." + name);
                return m;
            }

            r.StaticInstanceType = T("KerbalKonstructs.Core.StaticInstance");
            var staticDb = T("KerbalKonstructs.Core.StaticDatabase");
            var configParser = T("KerbalKonstructs.Core.ConfigParser");
            var lsManager = T("KerbalKonstructs.Core.LaunchSiteManager");
            r.GroupCenterType = T("KerbalKonstructs.Core.GroupCenter");
            var model = T("KerbalKonstructs.Core.StaticModel");
            var launchSite = T("KerbalKonstructs.Core.KKLaunchSite");
            var facility = T("KerbalKonstructs.Modules.KKFacility");
            var careerState = T("KerbalKonstructs.Modules.CareerState");
            r.KkType = T("KerbalKonstructs.KerbalKonstructs");
            r.SettingsType = T("KerbalKonstructs.Career.KerbalKonstructsSettings");
            if (missing.Count > 0)
            {
                problem = "missing types: " + string.Join(", ", missing.ToArray());
                return null;
            }

            var si = r.StaticInstanceType;
            r.SiUuid = F(si, "UUID");
            r.SiBody = F(si, "CelestialBody");
            r.SiGroup = F(si, "Group");
            r.SiRelativePosition = F(si, "RelativePosition");
            r.SiRadialPosition = F(si, "RadialPosition");
            r.SiRefLatitude = F(si, "RefLatitude");
            r.SiRefLongitude = F(si, "RefLongitude");
            r.SiModel = F(si, "model");
            r.SiConfigPath = F(si, "configPath");
            r.SiConfigUrl = F(si, "configUrl");
            r.SiIsInSavegame = F(si, "isInSavegame");
            r.SiGroupCenter = F(si, "groupCenter");
            r.SiLaunchSite = F(si, "launchSite");
            r.SiHasLaunchSites = F(si, "hasLauchSites");
            r.SiMyFacilities = F(si, "myFacilities");
            r.SiGrassConfigs = F(si, "grassColor2Configs");
            r.SiOrientate = M(si, "Orientate");
            r.SiDestroy = M(si, "Destroy");
            r.SiDeactivate = M(si, "Deactivate");
            r.SiActivate = M(si, "Activate");
            r.SiSaveConfig = M(si, "SaveConfig");
            r.SiCtor = si.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (r.SiCtor == null) missing.Add("StaticInstance()");

            r.SdInstancedByUuid = F(staticDb, "instancedByUUID");
            r.SdAllStaticInstances = F(staticDb, "allStaticInstances");
            r.SdAllStaticModels = F(staticDb, "allStaticModels");
            r.SdHasInstance = M(staticDb, "HasInstance", si);
            r.SdHasGroupCenter = M(staticDb, "HasGroupCenter", typeof(string));
            r.SdGetGroupCenter = M(staticDb, "GetGroupCenter", typeof(string));

            r.ModelName = F(model, "name");
            r.LaunchSiteName = F(launchSite, "LaunchSiteName");
            r.FacilityStaticInstance = F(facility, "staticInstance");
            r.LsOpenState = F(launchSite, "openState");
            r.LsOpenString = F(launchSite, "openString");
            r.FacOpenState = F(facility, "openState");
            r.FacOpenString = F(facility, "openString");

            r.GcIsBuiltIn = F(r.GroupCenterType, "isBuiltIn");
            r.GcIsInSavegame = F(r.GroupCenterType, "isInSavegame");
            r.GcIsActive = F(r.GroupCenterType, "isActive");
            r.GcLaunchsites = F(r.GroupCenterType, "launchsites");

            r.ParseInstanceConfig = M(configParser, "ParseInstanceConfig", si, typeof(ConfigNode));
            r.WriteInstanceConfig = M(configParser, "WriteInstanceConfig", si, typeof(ConfigNode));
            r.AttachLaunchSite = M(lsManager, "AttachLaunchSite", si, typeof(ConfigNode));
            r.AttachFacilities = M(r.KkType, "AttachFacilities", si, typeof(ConfigNode));
            r.SaveObjects = M(r.KkType, "SaveObjects");
            r.CareerStateLoad = M(careerState, "Load", typeof(ConfigNode));
            r.SelectInstance = M(r.KkType, "SelectInstance", si, typeof(bool));
            r.KkGameTime = F(r.KkType, "gameTime");
            r.KkConvertLegacy = F(r.KkType, "convertLegacyConfigs");
            r.KkSelectedInstance = F(r.KkType, "selectedInstance");

            r.SettingsOnLoad = M(r.SettingsType, "OnLoad", typeof(ConfigNode));
            r.SettingsOnSave = M(r.SettingsType, "OnSave", typeof(ConfigNode));

            if (missing.Count > 0)
            {
                problem = "missing members: " + string.Join(", ", missing.ToArray());
                return null;
            }
            return r;
        }

        // ---- typed helpers -------------------------------------------------------------------------------------

        public string Uuid(object si) => SiUuid.GetValue(si) as string;
        public string Group(object si) => SiGroup.GetValue(si) as string;
        public CelestialBody Body(object si) => SiBody.GetValue(si) as CelestialBody;
        public object Model(object si) => SiModel.GetValue(si);
        public string ModelNameOf(object si) { var m = Model(si); return m == null ? null : ModelName.GetValue(m) as string; }
        public string ConfigPath(object si) => SiConfigPath.GetValue(si) as string;
        public UrlDir.UrlConfig ConfigUrl(object si) => SiConfigUrl.GetValue(si) as UrlDir.UrlConfig;
        public bool IsInSavegame(object si) => SiIsInSavegame.GetValue(si) is bool b && b;
        public object GroupCenterOf(object si) => SiGroupCenter.GetValue(si);
        public UnityEngine.Vector3 RelativePosition(object si) => (UnityEngine.Vector3)SiRelativePosition.GetValue(si);

        public string LaunchSiteNameOf(object si)
        {
            if (!(SiHasLaunchSites.GetValue(si) is bool has) || !has) return null;
            var ls = SiLaunchSite.GetValue(si);
            return ls == null ? null : LaunchSiteName.GetValue(ls) as string;
        }

        public IDictionary InstancesByUuid() => SdInstancedByUuid.GetValue(null) as IDictionary;
        public Array AllInstances() => SdAllStaticInstances.GetValue(null) as Array;
        public IList AllModels() => SdAllStaticModels.GetValue(null) as IList;

        public object FindModel(string name)
        {
            var models = AllModels();
            if (models == null || string.IsNullOrEmpty(name)) return null;
            foreach (var m in models)
            {
                if (m != null && ModelName.GetValue(m) as string == name)
                    return m;
            }
            return null;
        }

        public bool HasGroupCenter(string dbKey) => dbKey != null && SdHasGroupCenter.Invoke(null, new object[] { dbKey }) is bool b && b;
        public object GetGroupCenter(string dbKey) => HasGroupCenter(dbKey) ? SdGetGroupCenter.Invoke(null, new object[] { dbKey }) : null;
        public bool GroupIsBuiltIn(object gc) => GcIsBuiltIn.GetValue(gc) is bool b && b;
        public bool GroupIsInSavegame(object gc) => GcIsInSavegame.GetValue(gc) is bool b && b;
        public bool GroupIsActive(object gc) => GcIsActive.GetValue(gc) is bool b && b;
        public bool HasInstance(object si) => SdHasInstance.Invoke(null, new[] { si }) is bool b && b;

        public object SelectedInstance() => KkSelectedInstance.GetValue(null);
        public bool OnLoadGateOpen() => KkGameTime.GetValue(null) is double d && d == -1.0;
    }
}
