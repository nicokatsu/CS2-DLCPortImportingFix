using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
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
        private const string TargetHarborPrefabPrefix = "CargoHarborComplex";

        private static readonly object PatchLock = new object();
        private static Dictionary<Entity, Resource> s_RuntimeResourcesByHarbor = new Dictionary<Entity, Resource>();
        private static HarborResourceSellerPatchSystem s_Instance;
        private static Harmony s_Harmony;
        private static bool s_Patched;
        private static readonly float CargoStationAmountBasedPenalty = ReadStaticField("kCargoStationAmountBasedPenalty", 0f);
        private static readonly float CargoStationPerRequestPenalty = ReadStaticField("kCargoStationPerRequestPenalty", 0f);
        private static readonly int CargoStationMaxTripNeededQueue = ReadStaticField("kCargoStationMaxTripNeededQueue", int.MaxValue);

        private const int SyncIntervalFrames = 2048;
        private const int CargoStationTargetFlag = 128;
        private EntityQuery m_HarborQuery;
        private EntityQuery m_HarborChildQuery;
        private PrefabSystem m_PrefabSystem;
        private int m_NextSyncFrame;
        private int m_RefreshDiagnosticsCount;
        private HashSet<string> m_LastChildSnapshot = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<Entity> m_TargetChildPrefabs = new HashSet<Entity>();
        private readonly HashSet<Entity> m_NonTargetChildPrefabs = new HashSet<Entity>();

        protected override void OnCreate()
        {
            base.OnCreate();

            s_Instance = this;
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_HarborQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Buildings.CargoTransportStation>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Deleted>());
            m_HarborChildQuery = GetEntityQuery(
                ComponentType.ReadOnly<Owner>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.ReadOnly<Game.Buildings.Building>(),
                ComponentType.ReadOnly<Game.Buildings.CargoTransportStation>(),
                ComponentType.ReadOnly<Game.Objects.SubObject>(),
                ComponentType.Exclude<Deleted>());

            RequireForUpdate(m_HarborQuery);
            RequireForUpdate(m_HarborChildQuery);

            EnsurePatched();
        }

        protected override void OnUpdate()
        {
            if (m_NextSyncFrame > 0)
            {
                m_NextSyncFrame--;
                return;
            }

            m_NextSyncFrame = SyncIntervalFrames;
            RefreshRuntimeResources("OnUpdate");
        }

        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            m_LastChildSnapshot.Clear();
            s_RuntimeResourcesByHarbor = new Dictionary<Entity, Resource>();
            RefreshRuntimeResources("OnGameLoaded");
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            if (ReferenceEquals(s_Instance, this))
                s_Instance = null;
            s_RuntimeResourcesByHarbor = new Dictionary<Entity, Resource>();
            m_LastChildSnapshot = new HashSet<string>(StringComparer.Ordinal);
        }

        private void RefreshRuntimeResources(string phase)
        {
            var harbors = m_HarborQuery.ToEntityArray(Allocator.Temp);
            var childBuildings = m_HarborChildQuery.ToEntityArray(Allocator.Temp);

            try
            {
                var targetHarbors = new HashSet<Entity>();
                var currentSnapshot = new HashSet<string>(StringComparer.Ordinal);
                var validChildren = new List<Entity>();
                var validChildHarbors = new Dictionary<Entity, Entity>();
                var targetChildCandidates = 0;
                var targetChildResources = 0;

                foreach (var harbor in harbors)
                {
                    if (TargetHarborPrefabNames.Contains(GetEntityPrefabName(harbor)))
                        targetHarbors.Add(harbor);
                }

                if (targetHarbors.Count == 0)
                {
                    s_RuntimeResourcesByHarbor = new Dictionary<Entity, Resource>();
                    return;
                }

                foreach (var child in childBuildings)
                {
                    if (!IsTargetHarborChild(child))
                        continue;

                    targetChildCandidates++;

                    var harbor = FindOwningHarbor(child, targetHarbors);
                    if (harbor == Entity.Null)
                        continue;

                    currentSnapshot.Add($"{child}|{harbor}");
                    validChildren.Add(child);
                    validChildHarbors[child] = harbor;
                }

                if (phase != "OnGameLoaded" && SnapshotsEqual(m_LastChildSnapshot, currentSnapshot))
                    return;

                m_LastChildSnapshot = currentSnapshot;
                var aggregated = new Dictionary<Entity, Resource>();

                foreach (var child in validChildren)
                {
                    var harbor = validChildHarbors[child];
                    var childResources = GetManagedResources(child);
                    if (childResources == Resource.NoResource)
                        continue;

                    targetChildResources++;

                    if (aggregated.TryGetValue(harbor, out var existing))
                        aggregated[harbor] = existing | childResources;
                    else
                        aggregated[harbor] = childResources;

                }

                s_RuntimeResourcesByHarbor = aggregated;

                m_RefreshDiagnosticsCount++;
                Mod.log.Info(
                    $"[HarborResourceSellerPatchSystem] {phase} refresh#{m_RefreshDiagnosticsCount} " +
                    $"harborQuery={harbors.Length} childQuery={childBuildings.Length} targetHarbors={targetHarbors.Count} " +
                    $"targetChildCandidates={targetChildCandidates} targetChildResources={targetChildResources} trackedHarbors={aggregated.Count}");
            }
            catch (Exception ex)
            {
                Mod.log.Error($"[HarborResourceSellerPatchSystem] Failed to refresh runtime resources: {ex}");
            }
            finally
            {
                harbors.Dispose();
                childBuildings.Dispose();
            }
        }

        private bool IsTargetHarborChild(Entity entity)
        {
            if (!EntityManager.HasComponent<PrefabRef>(entity))
                return false;

            var prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            if (m_TargetChildPrefabs.Contains(prefab))
                return true;

            if (m_NonTargetChildPrefabs.Contains(prefab))
                return false;

            var prefabName = GetPrefabName(prefab);
            var isTarget =
                prefabName.StartsWith(TargetHarborPrefabPrefix, StringComparison.Ordinal) &&
                (EntityManager.HasComponent<StorageCompanyData>(prefab) || EntityManager.HasComponent<StorageAreaData>(prefab));

            if (isTarget)
                m_TargetChildPrefabs.Add(prefab);
            else
                m_NonTargetChildPrefabs.Add(prefab);

            return isTarget;
        }

        private Entity FindOwningHarbor(Entity entity, HashSet<Entity> targetHarbors)
        {
            var current = entity;
            var guard = 0;

            while (EntityManager.HasComponent<Owner>(current) && guard < 32)
            {
                current = EntityManager.GetComponentData<Owner>(current).m_Owner;
                if (targetHarbors.Contains(current))
                    return current;
                guard++;
            }

            return Entity.Null;
        }

        private Resource GetManagedResources(Entity entity)
        {
            if (!EntityManager.HasComponent<PrefabRef>(entity))
                return Resource.NoResource;

            var prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            var resources = Resource.NoResource;

            if (EntityManager.HasComponent<StorageCompanyData>(prefab))
                resources |= EntityManager.GetComponentData<StorageCompanyData>(prefab).m_StoredResources;

            if (EntityManager.HasComponent<StorageAreaData>(prefab))
                resources |= EntityManager.GetComponentData<StorageAreaData>(prefab).m_Resources;

            return resources;
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
                s_Harmony.PatchAll(typeof(HarborResourceSellerPatchSystem).Assembly);
                s_Patched = true;
            }
        }

        private static JobHandle SetupResourceSellerPatched(
            ref Game.Simulation.ResourcePathfindSetup resourcePathfindSetup,
            Game.Simulation.PathfindSetupSystem system,
            Game.Simulation.PathfindSetupSystem.SetupData setupData,
            JobHandle inputDeps)
        {
            try
            {
                SupplementResourceSellerTargets(setupData);
            }
            catch (Exception ex)
            {
                Mod.log.Error($"[HarborResourceSellerPatchSystem] Failed to add supplemental seller targets: {ex}");
            }

            return resourcePathfindSetup.SetupResourceSeller(system, setupData, inputDeps);
        }

        private static void SupplementResourceSellerTargets(Game.Simulation.PathfindSetupSystem.SetupData setupData)
        {
            var instance = s_Instance;
            var snapshot = s_RuntimeResourcesByHarbor;
            if (instance == null || snapshot == null || snapshot.Count == 0)
                return;

            for (var setupIndex = 0; setupIndex < setupData.Length; setupIndex++)
            {
                setupData.GetItem(setupIndex, out var buyer, out PathfindTargetSeeker<PathfindSetupBuffer> targetSeeker);

                if (!instance.IsSupportedSupplementalBuyer(buyer))
                    continue;

                var request = targetSeeker.m_SetupQueueTarget;
                var requestedResource = request.m_Resource;
                if (requestedResource == Resource.NoResource)
                    continue;

                foreach (var harbor in snapshot)
                {
                    if ((harbor.Value & requestedResource) == Resource.NoResource)
                        continue;

                    if (!instance.IsValidSupplementalSeller(harbor.Key, requestedResource, request.m_Value))
                        continue;

                    var penalty = instance.CalculateSupplementalPenaltyLikeVanilla(harbor.Key, requestedResource, request);
                    targetSeeker.FindTargets(harbor.Key, penalty);
                }
            }
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

        private float CalculateSupplementalPenalty(Entity seller, Resource requestedResource, int requestedAmount)
        {
            var penalty = 0f;

            if (EntityManager.HasComponent<ServiceAvailable>(seller))
            {
                var service = EntityManager.GetComponentData<ServiceAvailable>(seller);
                penalty -= math.min(requestedAmount, service.m_ServiceAvailable) * 100f;
            }
            else if (requestedAmount > 0)
            {
                var available = EconomyUtils.GetResources(requestedResource, EntityManager.GetBuffer<Resources>(seller));
                var ratio = math.min(1f, available / (float)requestedAmount);
                penalty += 100f * (1f - ratio);
            }

            if (EntityManager.HasBuffer<TradeCost>(seller))
            {
                var tradeCost = EconomyUtils.GetTradeCost(requestedResource, EntityManager.GetBuffer<TradeCost>(seller));
                penalty += tradeCost.m_BuyCost * requestedAmount * 0.01f;
            }

            return penalty * 100f;
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

            if ((request.m_Flags & (SetupTargetFlags)CargoStationTargetFlag) != 0)
            {
                if (EntityManager.HasComponent<PrefabRef>(seller))
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
                                    penalty += CargoStationPerRequestPenalty * EntityManager.GetBuffer<StorageTransferRequest>(seller).Length;
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

        private static T ReadStaticField<T>(string fieldName, T fallback)
        {
            try
            {
                var field = AccessTools.Field(typeof(Game.Simulation.ResourcePathfindSetup), fieldName);
                if (field?.GetValue(null) is T value)
                    return value;
            }
            catch
            {
            }

            return fallback;
        }

        private static bool SnapshotsEqual(
            HashSet<string> previous,
            HashSet<string> current)
        {
            if (ReferenceEquals(previous, current))
                return true;

            if (previous == null || current == null || previous.Count != current.Count)
                return false;

            return previous.SetEquals(current);
        }

        [HarmonyPatch]
        private static class PathfindSetupSystemPatch
        {
            private static System.Reflection.MethodBase TargetMethod()
            {
                return AccessTools.Method(
                    typeof(Game.Simulation.PathfindSetupSystem),
                    "FindTargets",
                    new[]
                    {
                        typeof(SetupTargetType),
                        typeof(Game.Simulation.PathfindSetupSystem.SetupData).MakeByRefType()
                    });
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);
                var originalMethod = AccessTools.Method(
                    typeof(Game.Simulation.ResourcePathfindSetup),
                    nameof(Game.Simulation.ResourcePathfindSetup.SetupResourceSeller),
                    new[]
                    {
                        typeof(Game.Simulation.PathfindSetupSystem),
                        typeof(Game.Simulation.PathfindSetupSystem.SetupData),
                        typeof(JobHandle)
                    });
                var helperMethod = AccessTools.Method(typeof(HarborResourceSellerPatchSystem), nameof(SetupResourceSellerPatched));

                for (var i = 0; i < codes.Count; i++)
                {
                    if (codes[i].opcode == OpCodes.Call &&
                        codes[i].operand is System.Reflection.MethodInfo method &&
                        method == originalMethod)
                    {
                        codes[i] = new CodeInstruction(OpCodes.Call, helperMethod);
                        Mod.log.Info("[HarborResourceSellerPatchSystem] Patched PathfindSetupSystem.FindTargets(ResourceSeller)");
                        return codes;
                    }
                }

                Mod.log.Error("[HarborResourceSellerPatchSystem] Failed to locate SetupResourceSeller call in transpiler.");
                return codes;
            }
        }
    }
}
