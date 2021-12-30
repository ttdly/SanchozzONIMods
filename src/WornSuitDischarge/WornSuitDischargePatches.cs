﻿using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using KMod;
using UnityEngine;
using HarmonyLib;
using SanchozzONIMods.Lib;
using SanchozzONIMods.Shared;
using PeterHan.PLib.Core;
using PeterHan.PLib.PatchManager;

namespace WornSuitDischarge
{
    internal sealed class WornSuitDischargePatches : UserMod2
    {
        public override void OnLoad(Harmony harmony)
        {
            base.OnLoad(harmony);
            PUtil.InitLibrary();
            new PPatchManager(harmony).RegisterPatchClass(typeof(WornSuitDischargePatches));
        }

        public override void OnAllModsLoaded(Harmony harmony, IReadOnlyList<Mod> mods)
        {
            base.OnAllModsLoaded(harmony, mods);
            ManualDeliveryKGPatch.Patch(harmony);
        }

        // подкручиваем приоритет, чтобы задача доставки костюмов в доки считалась доставкой жизнеобеспечения.
        [PLibMethod(RunAt.AfterDbInit)]
        private static void AfterDbInit()
        {
            var LifeSupport = Db.Get().ChoreGroups.LifeSupport;
            var FetchCritical = Db.Get().ChoreTypes.FetchCritical;
            var EquipmentFetch = Db.Get().ChoreTypes.EquipmentFetch;
            var traverse = Traverse.Create(EquipmentFetch);
            if (!LifeSupport.choreTypes.Contains(EquipmentFetch))
                LifeSupport.choreTypes.Add(EquipmentFetch);
            if (!EquipmentFetch.groups.Contains(LifeSupport))
            {
                //EquipmentFetch.groups = EquipmentFetch.groups.AddItem(LifeSupport).ToArray();
                traverse.Property<ChoreGroup[]>(nameof(ChoreType.groups)).Value =
                    EquipmentFetch.groups.AddItem(LifeSupport).ToArray();
            }
            //EquipmentFetch.priority = FetchCritical.priority;
            traverse.Property<int>(nameof(ChoreType.priority)).Value = FetchCritical.priority;
        }

        private static bool ShouldTransfer(Assignable assignable, Equipment equipment)
        {
            var resume = equipment?.GetTargetGameObject()?.GetComponent<MinionResume>();
            var durability = assignable?.GetComponent<Durability>();
            return durability != null && durability.IsTrueWornOut(resume);
        }

        private static void Transfer(Assignable assignable, Storage lockerStorage)
        {
            if (assignable != null && lockerStorage != null)
            {
                var suitStorage = assignable.GetComponent<Storage>();
                var suitTank = assignable.GetComponent<SuitTank>();
                if (suitStorage != null && suitTank != null)
                {
                    suitStorage.Transfer(lockerStorage, suitTank.elementTag, suitTank.capacity, false, true);
                }
                var jetSuitTank = assignable.GetComponent<JetSuitTank>();
                if (jetSuitTank != null && lockerStorage.HasTag(JetSuitLockerConfig.ID))
                {
                    lockerStorage.AddLiquid(SimHashes.Petroleum, jetSuitTank.amount, assignable.GetComponent<PrimaryElement>().Temperature, byte.MaxValue, 0, false, true);
                    jetSuitTank.amount = 0f;
                }
            }
        }

        // штатная разэкипировка костюма 
        [HarmonyPatch(typeof(SuitLocker), nameof(SuitLocker.UnequipFrom))]
        private static class SuitLocker_UnequipFrom
        {
            private static void Prefix(SuitLocker __instance, Equipment equipment)
            {
                var assignable = equipment?.GetAssignable(Db.Get().AssignableSlots.Suit);
                var storage = __instance?.GetComponent<Storage>();
                if (ShouldTransfer(assignable, equipment))
                    Transfer(assignable, storage);
            }
        }

        // снятие костюма задачей "вернуть костюм" если док занят
        // не удалось протестировать, возможно мёртвая ветка кода игры.
        [HarmonyPatch(typeof(SuitLocker.ReturnSuitWorkable), "OnCompleteWork")]
        private static class SuitLocker_ReturnSuitWorkable_OnCompleteWork
        {
            private static void TryTransfer(Assignable assignable, SuitLocker.ReturnSuitWorkable workable, Equipment equipment)
            {
                var storage = workable?.GetComponent<Storage>();
                if (ShouldTransfer(assignable, equipment))
                    Transfer(assignable, storage);
            }
            /*
                else
		        {
			---     equipment.GetAssignable(Db.Get().AssignableSlots.Suit).Unassign();
            +++     var assignable = equipment.GetAssignable(Db.Get().AssignableSlots.Suit);
            +++     TryTransfer(assignable, this, equipment);
            +++     assignable.Unassign();
		        }
            */
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase method)
            {
                var instructionsList = instructions.ToList();
                string methodName = method.DeclaringType.FullName + "." + method.Name;

                var unassign = typeof(Assignable).GetMethodSafe(nameof(Assignable.Unassign), false, PPatchTools.AnyArguments);
                var trytransfer = typeof(SuitLocker_ReturnSuitWorkable_OnCompleteWork).GetMethodSafe(nameof(TryTransfer), true, PPatchTools.AnyArguments);

                bool result = false;
                if (unassign != null && trytransfer != null)
                {
                    for (int i = 0; i < instructionsList.Count(); i++)
                    {
                        var instruction = instructionsList[i];
                        if (((instruction.opcode == OpCodes.Call) || (instruction.opcode == OpCodes.Callvirt)) && (instruction.operand is MethodInfo info) && info == unassign)
                        {
                            instructionsList.Insert(i++, new CodeInstruction(OpCodes.Dup));     // assignable
                            instructionsList.Insert(i++, new CodeInstruction(OpCodes.Ldarg_0)); // workable
                            instructionsList.Insert(i++, new CodeInstruction(OpCodes.Ldloc_0)); // equipment
                            instructionsList.Insert(i++, new CodeInstruction(OpCodes.Call, trytransfer));
                            result = true;
#if DEBUG
                            PUtil.LogDebug($"'{methodName}' Transpiler injected");
#endif
                            break;
                        }
                    }
                }
                if (!result)
                {
                    PUtil.LogWarning($"Could not apply Transpiler to the '{methodName}'");
                }
                return instructionsList;
            }
        }

        // снятие костюма при прохождении мимо маркера, если нет подходящих доков
        // ищем док с наименьшей массой
        [HarmonyPatch]
        private static class SuitMarker_SuitMarkerReactable_Run
        {
            private static void TryTransfer(Assignable assignable, SuitMarker marker, Equipment equipment)
            {
                if (ShouldTransfer(assignable, equipment))
                {
                    Storage storage = null;
                    float mass = float.PositiveInfinity;
                    var pooledList = ListPool<SuitLocker, SuitMarker>.Allocate();
                    marker?.GetAttachedLockers(pooledList);
                    foreach (var locker in pooledList)
                    {
                        var s = locker.GetComponent<Storage>();
                        var m = s.MassStored();
                        if (m < mass)
                        {
                            mass = m;
                            storage = s;
                        }
                    }
                    pooledList.Recycle();
                    Transfer(assignable, storage);
                }
            }

            private static MethodBase TargetMethod()
            {
                return typeof(SuitMarker).GetNestedType("SuitMarkerReactable", PPatchTools.BASE_FLAGS)
                    .GetMethodSafe("Run", false, PPatchTools.AnyArguments);
            }
            /*
                    assignable = equipment.GetAssignable(Db.Get().AssignableSlots.Suit);
            +++     TryTransfer(assignable, this.suitMarker, equipment);
		            assignable.Unassign();
            */
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase method)
            {
                var instructionsList = instructions.ToList();
                string methodName = method.DeclaringType.FullName + "." + method.Name;

                var unassign = typeof(Assignable).GetMethodSafe(nameof(Assignable.Unassign), false, PPatchTools.AnyArguments);
                var trytransfer = typeof(SuitMarker_SuitMarkerReactable_Run).GetMethodSafe(nameof(TryTransfer), true, PPatchTools.AnyArguments);
                var suitMarker = typeof(SuitMarker).GetNestedType("SuitMarkerReactable", PPatchTools.BASE_FLAGS).GetFieldSafe("suitMarker", false);

                bool result = false;
                if (unassign != null && trytransfer != null && suitMarker != null)
                {
                    for (int i = 0; i < instructionsList.Count(); i++)
                    {
                        var instruction = instructionsList[i];
                        if (((instruction.opcode == OpCodes.Call) || (instruction.opcode == OpCodes.Callvirt)) && (instruction.operand is MethodInfo info) && info == unassign)
                        {
                            instructionsList.Insert(i++, new CodeInstruction(OpCodes.Dup));     // assignable
                            instructionsList.Insert(i++, new CodeInstruction(OpCodes.Ldarg_0));
                            instructionsList.Insert(i++, new CodeInstruction(OpCodes.Ldfld, suitMarker));
                            instructionsList.Insert(i++, new CodeInstruction(OpCodes.Ldloc_0)); // equipment
                            instructionsList.Insert(i++, new CodeInstruction(OpCodes.Call, trytransfer));
                            result = true;
#if DEBUG
                            PUtil.LogDebug($"'{methodName}' Transpiler injected");
#endif
                            break;
                        }
                    }
                }
                if (!result)
                {
                    PUtil.LogWarning($"Could not apply Transpiler to the '{methodName}'");
                }
                return instructionsList;
            }
        }

        // при деконструкции не выпускать кислород в атмосферу
        // доставка кислорода и керосина баллонами
        [HarmonyPatch]
        private static class XXXSuitLockerConfig_ConfigureBuildingTemplate
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                const string name = nameof(IBuildingConfig.ConfigureBuildingTemplate);
                var methods = new List<MethodBase>();
                methods.Add(typeof(SuitLockerConfig).GetMethod(name));
                methods.Add(typeof(JetSuitLockerConfig).GetMethod(name));
                methods.Add(typeof(OxygenMaskLockerConfig).GetMethod(name));
                if (DlcManager.IsExpansion1Active())
                    methods.Add(typeof(LeadSuitLockerConfig).GetMethod(name));
                return methods;
            }

            private static void Postfix(GameObject go, Tag prefab_tag)
            {
                float capacity = go.GetComponent<ConduitConsumer>()?.capacityKG ?? JetSuitLockerConfig.O2_CAPACITY;
                var storage = go.GetComponent<Storage>();
                AddManualDeliveryKG(go, GameTags.Oxygen, capacity).SetStorage(storage);
                if (prefab_tag == JetSuitLockerConfig.ID)
                    AddManualDeliveryKG(go, SimHashes.Petroleum.CreateTag(), JetSuitLocker.FUEL_CAPACITY).SetStorage(storage);
                go.GetComponent<KPrefabID>().prefabInitFn += delegate (GameObject inst)
                {
                    var mdkgs = inst.GetComponents<ManualDeliveryKG>();
                    foreach (ManualDeliveryKG mg in mdkgs)
                        ManualDeliveryKGPatch.userPaused.Set(mg, true);
                };
                go.AddOrGet<CopyBuildingSettings>();
                go.AddOrGet<StorageDropper>();
            }

            private static ManualDeliveryKG AddManualDeliveryKG(GameObject go, Tag requestedTag, float capacity)
            {
                const float refill = 0.75f;
                var md = go.AddComponent<ManualDeliveryKG>();
                md.capacity = capacity;
                md.refillMass = refill * capacity;
                md.requestedItemTag = requestedTag;
                md.choreTypeIDHash = Db.Get().ChoreTypes.MachineFetch.IdHash;
                md.operationalRequirement = FetchOrder2.OperationalRequirement.Functional;
                md.allowPause = true;
                return md;
            }
        }

        // копирование настроек самого дока
        // копируется только с настроеного дока на ненастроеный
        // ожидает доставки или имеет костюм -> запросить доставку
        // просто пустой -> оставить пустым
        [HarmonyPatch(typeof(SuitLocker.States), nameof(SuitLocker.States.InitializeStates))]
        private static class SuitLocker_States_InitializeStates
        {
            private static void Postfix(SuitLocker.States __instance)
            {
                __instance.empty.notconfigured
                    .EventHandler(GameHashes.CopySettings, (smi, data) =>
                    {
                        var locker = ((GameObject)data)?.GetComponent<SuitLocker>();
                        if (locker != null && locker.smi.sm.isConfigured.Get(locker.smi))
                        {
                            if (locker.smi.sm.isWaitingForSuit.Get(locker.smi) || locker.GetStoredOutfit() != null)
                                smi.master.ConfigRequestSuit();
                            else
                                smi.master.ConfigNoSuit();
                        }
                    });
            }
        }
    }
}
