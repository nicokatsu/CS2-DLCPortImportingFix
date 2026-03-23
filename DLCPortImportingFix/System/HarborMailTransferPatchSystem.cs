using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Pathfind;
using Game.Prefabs;
using Game.Simulation;
using HarmonyLib;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace DLCPortImportingFix
{
    public partial class HarborMailTransferPatchSystem : GameSystemBase
    {
        private static readonly object PatchLock = new object();
        private static HarborMailTransferPatchSystem s_Instance;
        private static Harmony s_Harmony;
        private static bool s_Patched;

        private EntityQuery m_HarborQuery;
        private PrefabSystem m_PrefabSystem;

        protected override void OnCreate()
        {
            base.OnCreate();

            s_Instance = this;
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_HarborQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Buildings.CargoTransportStation>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.ReadOnly<Game.Companies.StorageCompany>(),
                ComponentType.ReadOnly<Resources>(),
                ComponentType.ReadOnly<TradeCost>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());

            RequireForUpdate(m_HarborQuery);
            EnsurePatched();
        }

        protected override void OnUpdate()
        {
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();

            if (ReferenceEquals(s_Instance, this))
                s_Instance = null;
        }

        private void SupplementMailTransferTargets(PathfindSetupSystem.SetupData setupData)
        {
            var harbors = m_HarborQuery.ToEntityArray(Allocator.Temp);

            try
            {
                for (var setupIndex = 0; setupIndex < setupData.Length; setupIndex++)
                {
                    setupData.GetItem(setupIndex, out _, out PathfindTargetSeeker<PathfindSetupBuffer> targetSeeker);
                    var request = targetSeeker.m_SetupQueueTarget;
                    var requestedResource = request.m_Resource;
                    if (!IsSupportedMailResource(requestedResource))
                        continue;

                    foreach (var harbor in harbors)
                    {
                        if (!HarborPatchUtils.IsTargetHarbor(EntityManager, m_PrefabSystem, harbor))
                            continue;

                        if (!HarborPatchUtils.TryGetCombinedStorageData(
                                EntityManager,
                                harbor,
                                out var storageCompanyData,
                                out var storageLimitData,
                                out var hasStorageLimit))
                            continue;

                        if ((requestedResource & storageCompanyData.m_StoredResources) == Resource.NoResource)
                            continue;

                        if (!CanServeMailTransferRequest(harbor, requestedResource, request, storageLimitData, hasStorageLimit))
                            continue;

                        var penalty = CalculateMailTransferPenalty(harbor, requestedResource, request, storageLimitData, hasStorageLimit);
                        targetSeeker.FindTargets(harbor, penalty);
                    }
                }
            }
            catch (Exception ex)
            {
                Mod.log.Error($"[HarborMailTransferPatchSystem] Failed to supplement mail transfer targets: {ex}");
            }
            finally
            {
                harbors.Dispose();
            }
        }

        private static bool IsSupportedMailResource(Resource resource)
        {
            return (resource & (Resource.UnsortedMail | Resource.LocalMail | Resource.OutgoingMail)) != Resource.NoResource;
        }

        private bool CanServeMailTransferRequest(
            Entity harbor,
            Resource requestedResource,
            SetupQueueTarget request,
            StorageLimitData storageLimitData,
            bool hasStorageLimit)
        {
            var available = EconomyUtils.GetResources(requestedResource, EntityManager.GetBuffer<Resources>(harbor));
            var requestedAmount = request.m_Value;

            if ((request.m_Flags & SetupTargetFlags.Export) != SetupTargetFlags.None)
            {
                if (available < requestedAmount)
                    return false;

                var tradeCost = EconomyUtils.GetTradeCost(requestedResource, EntityManager.GetBuffer<TradeCost>(harbor));
                var scoreAmount = available - tradeCost.m_BuyCost * requestedAmount;
                return scoreAmount >= requestedAmount;
            }

            if ((request.m_Flags & SetupTargetFlags.Import) != SetupTargetFlags.None)
            {
                if (!hasStorageLimit)
                    return false;

                var remainingCapacity = storageLimitData.m_Limit - available;
                return remainingCapacity >= requestedAmount;
            }

            return false;
        }

        private float CalculateMailTransferPenalty(
            Entity harbor,
            Resource requestedResource,
            SetupQueueTarget request,
            StorageLimitData storageLimitData,
            bool hasStorageLimit)
        {
            var available = EconomyUtils.GetResources(requestedResource, EntityManager.GetBuffer<Resources>(harbor));
            var requestedAmount = request.m_Value;
            var tradeCost = EconomyUtils.GetTradeCost(requestedResource, EntityManager.GetBuffer<TradeCost>(harbor));

            if ((request.m_Flags & SetupTargetFlags.Export) != SetupTargetFlags.None)
            {
                var scoreAmount = available - tradeCost.m_BuyCost * requestedAmount;
                return math.max(0f, 500f - scoreAmount);
            }

            if ((request.m_Flags & SetupTargetFlags.Import) != SetupTargetFlags.None)
            {
                if (!hasStorageLimit)
                    return 0f;

                var remainingCapacity = storageLimitData.m_Limit - available;
                return math.max(0f, -0.1f * remainingCapacity + tradeCost.m_SellCost * requestedAmount);
            }

            return 0f;
        }

        private static void EnsurePatched()
        {
            lock (PatchLock)
            {
                if (s_Patched)
                    return;

                s_Harmony = new Harmony("glydd.bp-harbor-mail-transfer-fix");
                s_Harmony.CreateClassProcessor(typeof(PathfindSetupSystemPatch)).Patch();
                s_Patched = true;
            }
        }

        private static JobHandle SetupMailTransferPatched(
            ref PostServicePathfindSetup postServicePathfindSetup,
            PathfindSetupSystem system,
            PathfindSetupSystem.SetupData setupData,
            JobHandle inputDeps)
        {
            try
            {
                s_Instance?.SupplementMailTransferTargets(setupData);
            }
            catch (Exception ex)
            {
                Mod.log.Error($"[HarborMailTransferPatchSystem] Failed before vanilla SetupMailTransfer: {ex}");
            }

            return postServicePathfindSetup.SetupMailTransfer(system, setupData, inputDeps);
        }

        [HarmonyPatch]
        private static class PathfindSetupSystemPatch
        {
            private static System.Reflection.MethodBase TargetMethod()
            {
                return AccessTools.Method(
                    typeof(PathfindSetupSystem),
                    "FindTargets",
                    new[]
                    {
                        typeof(SetupTargetType),
                        typeof(PathfindSetupSystem.SetupData).MakeByRefType()
                    });
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);
                var originalMethod = AccessTools.Method(
                    typeof(PostServicePathfindSetup),
                    nameof(PostServicePathfindSetup.SetupMailTransfer),
                    new[]
                    {
                        typeof(PathfindSetupSystem),
                        typeof(PathfindSetupSystem.SetupData),
                        typeof(JobHandle)
                    });
                var helperMethod = AccessTools.Method(
                    typeof(HarborMailTransferPatchSystem),
                    nameof(SetupMailTransferPatched));

                for (var i = 0; i < codes.Count; i++)
                {
                    if (codes[i].opcode == OpCodes.Call &&
                        codes[i].operand is System.Reflection.MethodInfo method &&
                        method == originalMethod)
                    {
                        codes[i] = new CodeInstruction(OpCodes.Call, helperMethod);
                        return codes;
                    }
                }

                Mod.log.Error("[HarborMailTransferPatchSystem] Failed to locate SetupMailTransfer call in transpiler.");
                return codes;
            }
        }
    }
}
