using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using Colossal.Serialization.Entities;
using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Companies;
using Game.Common;
using Game.Economy;
using Game.Pathfind;
using Game.Prefabs;
using Game.Simulation;
using Game.Vehicles;
using HarmonyLib;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace DLCPortImportingFix
{
    public partial class HarborResourceSellerPatchSystem : GameSystemBase
    {
        private static readonly HashSet<string> TargetHarborPrefabNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "CargoHarborComplex01",
            "CargoHarborComplex02",
            "CargoHarborComplex03"
        };

        private static readonly object PatchLock = new object();
        private static HarborResourceSellerPatchSystem s_Instance;
        private static Harmony s_Harmony;
        private static bool s_Patched;
        private static readonly float CargoStationAmountBasedPenalty = ReadStaticField("kCargoStationAmountBasedPenalty", 0f);
        private static readonly float CargoStationPerRequestPenalty = ReadStaticField("kCargoStationPerRequestPenalty", 0f);
        private static readonly int CargoStationMaxTripNeededQueue = ReadStaticField("kCargoStationMaxTripNeededQueue", int.MaxValue);
        private const int CargoStationTargetFlag = 128;

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

        private void SupplementResourceSellerTargets(PathfindSetupSystem.SetupData setupData)
        {
            var harbors = m_HarborQuery.ToEntityArray(Allocator.Temp);

            try
            {
                for (var setupIndex = 0; setupIndex < setupData.Length; setupIndex++)
                {
                    setupData.GetItem(setupIndex, out var buyer, out PathfindTargetSeeker<PathfindSetupBuffer> targetSeeker);
                    if (!IsSupportedSupplementalBuyer(buyer))
                        continue;

                    var request = targetSeeker.m_SetupQueueTarget;
                    var requestedResource = request.m_Resource;
                    if (requestedResource == Resource.NoResource)
                        continue;

                    foreach (var harbor in harbors)
                    {
                        if (!TargetHarborPrefabNames.Contains(GetEntityPrefabName(harbor)))
                            continue;

                        if (!TryGetCombinedStoredResources(harbor, out var combinedResources))
                            continue;

                        if ((combinedResources & requestedResource) == Resource.NoResource)
                            continue;

                        if (!IsValidSupplementalSeller(harbor, requestedResource, request.m_Value))
                            continue;

                        var penalty = CalculateSupplementalPenaltyLikeVanilla(harbor, requestedResource, request);
                        targetSeeker.FindTargets(harbor, penalty);
                    }
                }
            }
            catch (Exception ex)
            {
                Mod.log.Error($"[HarborResourceSellerPatchSystem] Failed to supplement seller targets: {ex}");
            }
            finally
            {
                harbors.Dispose();
            }
        }

        private bool TryGetCombinedStoredResources(Entity harbor, out Resource combinedResources)
        {
            combinedResources = Resource.NoResource;

            if (!EntityManager.HasComponent<PrefabRef>(harbor))
                return false;

            var harborPrefab = EntityManager.GetComponentData<PrefabRef>(harbor).m_Prefab;
            if (!EntityManager.HasComponent<StorageCompanyData>(harborPrefab))
                return false;

            var combinedData = EntityManager.GetComponentData<StorageCompanyData>(harborPrefab);
            var combinedAny = false;

            if (EntityManager.HasBuffer<InstalledUpgrade>(harbor))
            {
                var upgrades = EntityManager.GetBuffer<InstalledUpgrade>(harbor);
                combinedAny = UpgradeUtils.CombineStats(EntityManager, ref combinedData, upgrades);
            }

            combinedResources = combinedData.m_StoredResources;
            return combinedAny || combinedResources != Resource.NoResource;
        }

        private bool IsValidSupplementalSeller(Entity seller, Resource requestedResource, int requestedAmount)
        {
            if (!EntityManager.Exists(seller) || !EntityManager.HasBuffer<Resources>(seller))
                return false;

            var available = EconomyUtils.GetResources(requestedResource, EntityManager.GetBuffer<Resources>(seller));
            if (available <= 0)
                return false;

            if (requestedAmount > 0 && available < requestedAmount / 2)
                return false;

            return true;
        }

        private bool IsSupportedSupplementalBuyer(Entity buyer)
        {
            if (!EntityManager.Exists(buyer))
                return false;

            if (EntityManager.HasComponent<Household>(buyer) ||
                EntityManager.HasComponent<TouristHousehold>(buyer) ||
                EntityManager.HasComponent<CommuterHousehold>(buyer) ||
                EntityManager.HasComponent<HomelessHousehold>(buyer))
                return false;

            return EntityManager.HasComponent<CompanyData>(buyer) || EntityManager.HasComponent<BuyingCompany>(buyer);
        }

        private float CalculateSupplementalPenaltyLikeVanilla(Entity seller, Resource requestedResource, SetupQueueTarget request)
        {
            var requestedAmount = request.m_Value;
            var penalty = 0f;
            var available = EconomyUtils.GetResources(requestedResource, EntityManager.GetBuffer<Resources>(seller));

            if (EntityManager.HasComponent<ServiceAvailable>(seller))
            {
                var service = EntityManager.GetComponentData<ServiceAvailable>(seller);
                penalty -= math.min(available, service.m_ServiceAvailable) * 100f;
            }
            else if (requestedAmount > 0)
            {
                var ratio = math.min(1f, available / (float)requestedAmount);
                penalty += 100f * (1f - ratio);
            }

            if ((request.m_Flags & (SetupTargetFlags)CargoStationTargetFlag) != 0 &&
                EntityManager.HasComponent<PrefabRef>(seller))
            {
                var prefab = EntityManager.GetComponentData<PrefabRef>(seller).m_Prefab;
                if (EntityManager.HasComponent<TransportCompanyData>(prefab))
                {
                    var transportCompanyData = EntityManager.GetComponentData<TransportCompanyData>(prefab);
                    if (EntityManager.HasBuffer<OwnedVehicle>(seller) &&
                        EntityManager.GetBuffer<OwnedVehicle>(seller).Length < transportCompanyData.m_MaxTransports)
                    {
                        if (!EntityManager.HasBuffer<TripNeeded>(seller) ||
                            EntityManager.GetBuffer<TripNeeded>(seller).Length < CargoStationMaxTripNeededQueue)
                        {
                            penalty += CargoStationAmountBasedPenalty * requestedAmount;

                            if (EntityManager.HasBuffer<StorageTransferRequest>(seller))
                            {
                                penalty += CargoStationPerRequestPenalty *
                                           EntityManager.GetBuffer<StorageTransferRequest>(seller).Length;
                            }
                        }
                    }
                }
            }

            if (EntityManager.HasBuffer<TradeCost>(seller))
            {
                var tradeCost = EconomyUtils.GetTradeCost(requestedResource, EntityManager.GetBuffer<TradeCost>(seller));
                penalty += tradeCost.m_BuyCost * requestedAmount * 0.01f;
            }

            return penalty * 100f;
        }

        private string GetEntityPrefabName(Entity entity)
        {
            if (!EntityManager.HasComponent<PrefabRef>(entity))
                return string.Empty;

            return GetPrefabName(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
        }

        private string GetPrefabName(Entity prefab)
        {
            try
            {
                return m_PrefabSystem.GetPrefabName(prefab) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void EnsurePatched()
        {
            lock (PatchLock)
            {
                if (s_Patched)
                    return;

                s_Harmony = new Harmony("glydd.bp-harbor-resource-importing-fix");
                s_Harmony.CreateClassProcessor(typeof(PathfindSetupSystemPatch)).Patch();
                s_Patched = true;
            }
        }

        private static JobHandle SetupResourceSellerPatched(
            ref ResourcePathfindSetup resourcePathfindSetup,
            PathfindSetupSystem system,
            PathfindSetupSystem.SetupData setupData,
            JobHandle inputDeps)
        {
            try
            {
                s_Instance?.SupplementResourceSellerTargets(setupData);
            }
            catch (Exception ex)
            {
                Mod.log.Error($"[HarborResourceSellerPatchSystem] Failed before vanilla ResourceSeller: {ex}");
            }

            return resourcePathfindSetup.SetupResourceSeller(system, setupData, inputDeps);
        }

        private static T ReadStaticField<T>(string fieldName, T fallback)
        {
            try
            {
                var field = AccessTools.Field(typeof(ResourcePathfindSetup), fieldName);
                if (field?.GetValue(null) is T value)
                    return value;
            }
            catch
            {
            }

            return fallback;
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
                    typeof(ResourcePathfindSetup),
                    nameof(ResourcePathfindSetup.SetupResourceSeller),
                    new[]
                    {
                        typeof(PathfindSetupSystem),
                        typeof(PathfindSetupSystem.SetupData),
                        typeof(JobHandle)
                    });
                var helperMethod = AccessTools.Method(
                    typeof(HarborResourceSellerPatchSystem),
                    nameof(SetupResourceSellerPatched));

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

                Mod.log.Error("[HarborResourceSellerPatchSystem] Failed to locate SetupResourceSeller call in transpiler.");
                return codes;
            }
        }
    }
}
