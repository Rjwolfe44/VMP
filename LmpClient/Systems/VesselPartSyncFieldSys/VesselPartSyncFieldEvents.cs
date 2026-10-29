using LmpClient.Base;
using LmpClient.Extensions;
using LmpClient.ModuleStore;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace LmpClient.Systems.VesselPartSyncFieldSys
{
    public class VesselPartSyncFieldEvents : SubSystem<VesselPartSyncFieldSystem>
    {
        private static readonly Dictionary<Guid, Dictionary<uint, Dictionary<string, Dictionary<string, TimeToSend>>>> LastSendTimeDictionary =
            new Dictionary<Guid, Dictionary<uint, Dictionary<string, Dictionary<string, TimeToSend>>>>();

        private static bool CallIsValid(PartModule module, string fieldName)
        {
            var vessel = module.vessel;
            if (vessel == null || !vessel.loaded || vessel.protoVessel == null)
                return false;

            var part = module.part;
            if (part == null)
                return false;

            //The vessel is immortal so we are sure that it's not ours
            if (part.vessel.IsImmortal())
                return false;

            if (FieldModuleStore.CustomizedModuleBehaviours.TryGetValue(module.moduleName, out var customization))
            {
                if (customization.CustomizedFields.TryGetValue(fieldName, out var fieldCust))
                {
                    var timeToSend = LastSendTimeDictionary.GetOrAdd(module.vessel.id, () => new Dictionary<uint, Dictionary<string, Dictionary<string, TimeToSend>>>())
                        .GetOrAdd(module.part.flightID, () => new Dictionary<string, Dictionary<string, TimeToSend>>())
                        .GetOrAdd(module.moduleName, () => new Dictionary<string, TimeToSend>())
                        .GetOrAdd(fieldName, () => new TimeToSend(fieldCust.MaxIntervalInMs));

                    return timeToSend.ReadyToSend();
                }
            }

            return true;
        }

        #region PartField change events

        // NOTE: per-field-change Log() calls were removed.  PartModuleFloatFieldChanged in particular
        // fires every physics tick for fields like FXModuleThrottleEffects.state on every engine of every
        // vessel, generating thousands of log lines per second.  The CallIsValid() throttle (tied to
        // CustomizedFields.MaxIntervalInMs) only gates network sends — non-customized fields are reported
        // every change.  Logging here was an order-of-magnitude bigger cost than the network send itself
        // and made KSP's Player.log unreadable in MP sessions.

        public void PartModuleBoolFieldChanged(PartModule module, string fieldName, bool newValue)
        {
            if (!CallIsValid(module, fieldName))
                return;

            System.MessageSender.SendVesselPartSyncFieldBoolMsg(module.vessel, module.part, module.moduleName, fieldName, newValue);
        }


        public void PartModuleShortFieldChanged(PartModule module, string fieldName, short newValue)
        {
            if (!CallIsValid(module, fieldName))
                return;

            System.MessageSender.SendVesselPartSyncFieldShortMsg(module.vessel, module.part, module.moduleName, fieldName, newValue);
        }

        public void PartModuleUshortFieldChanged(PartModule module, string fieldName, ushort newValue)
        {
            if (!CallIsValid(module, fieldName))
                return;

            System.MessageSender.SendVesselPartSyncFieldUshortMsg(module.vessel, module.part, module.moduleName, fieldName, newValue);
        }

        public void PartModuleIntFieldChanged(PartModule module, string fieldName, int newValue)
        {
            if (!CallIsValid(module, fieldName))
                return;

            System.MessageSender.SendVesselPartSyncFieldIntMsg(module.vessel, module.part, module.moduleName, fieldName, newValue);
        }

        public void PartModuleUintFieldChanged(PartModule module, string fieldName, uint newValue)
        {
            if (!CallIsValid(module, fieldName))
                return;

            System.MessageSender.SendVesselPartSyncFieldUIntMsg(module.vessel, module.part, module.moduleName, fieldName, newValue);
        }

        public void PartModuleFloatFieldChanged(PartModule module, string fieldName, float newValue)
        {
            if (!CallIsValid(module, fieldName))
                return;

            System.MessageSender.SendVesselPartSyncFieldFloatMsg(module.vessel, module.part, module.moduleName, fieldName, newValue);
        }


        public void PartModuleLongFieldChanged(PartModule module, string fieldName, long newValue)
        {
            if (!CallIsValid(module, fieldName))
                return;

            System.MessageSender.SendVesselPartSyncFieldLongMsg(module.vessel, module.part, module.moduleName, fieldName, newValue);
        }

        public void PartModuleUlongFieldChanged(PartModule module, string fieldName, ulong newValue)
        {
            if (!CallIsValid(module, fieldName))
                return;

            System.MessageSender.SendVesselPartSyncFieldULongMsg(module.vessel, module.part, module.moduleName, fieldName, newValue);
        }

        public void PartModuleDoubleFieldChanged(PartModule module, string fieldName, double newValue)
        {
            if (!CallIsValid(module, fieldName))
                return;

            System.MessageSender.SendVesselPartSyncFieldDoubleMsg(module.vessel, module.part, module.moduleName, fieldName, newValue);
        }

        public void PartModuleVector2FieldChanged(PartModule module, string fieldName, Vector2 newValue)
        {
            if (!CallIsValid(module, fieldName))
                return;

            System.MessageSender.SendVesselPartSyncFieldVector2Msg(module.vessel, module.part, module.moduleName, fieldName, newValue);
        }

        public void PartModuleVector3FieldChanged(PartModule module, string fieldName, Vector3 newValue)
        {
            if (!CallIsValid(module, fieldName))
                return;

            System.MessageSender.SendVesselPartSyncFieldVector3Msg(module.vessel, module.part, module.moduleName, fieldName, newValue);
        }

        public void PartModuleQuaternionFieldChanged(PartModule module, string fieldName, Quaternion newValue)
        {
            if (!CallIsValid(module, fieldName))
                return;

            System.MessageSender.SendVesselPartSyncFieldQuaternionMsg(module.vessel, module.part, module.moduleName, fieldName, newValue);
        }

        public void PartModuleStringFieldChanged(PartModule module, string fieldName, string newValue)
        {
            if (!CallIsValid(module, fieldName))
                return;

            System.MessageSender.SendVesselPartSyncFieldStringMsg(module.vessel, module.part, module.moduleName, fieldName, newValue);
        }

        public void PartModuleObjectFieldChanged(PartModule module, string fieldName, object newValue)
        {
            if (!CallIsValid(module, fieldName))
                return;

            // Unsupported field type — keep the warning so authors notice missing serializer coverage.
            LunaLog.LogWarning($"Field {fieldName} in module {module.moduleName} from part {module.part.flightID} has a field type that is not supported!");
            System.MessageSender.SendVesselPartSyncFieldObjectMsg(module.vessel, module.part, module.moduleName, fieldName, newValue);
        }

        public void PartModuleEnumFieldChanged(PartModule module, string fieldName, int newValue, string newValueStr)
        {
            if (!CallIsValid(module, fieldName))
                return;

            System.MessageSender.SendVesselPartSyncFieldEnumMsg(module.vessel, module.part, module.moduleName, fieldName, newValue, newValueStr);
        }

        #endregion
    }
}
