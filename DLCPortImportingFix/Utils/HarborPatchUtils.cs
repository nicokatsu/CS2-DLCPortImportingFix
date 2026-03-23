using System;
using System.Collections.Generic;
using Game.Buildings;
using Game.Companies;
using Game.Economy;
using Game.Prefabs;
using Unity.Entities;

namespace DLCPortImportingFix
{
    internal static class HarborPatchUtils
    {
        private static readonly HashSet<string> TargetHarborPrefabNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "CargoHarborComplex01",
            "CargoHarborComplex02",
            "CargoHarborComplex03"
        };

        public static bool IsTargetHarbor(EntityManager entityManager, PrefabSystem prefabSystem, Entity entity)
        {
            return TargetHarborPrefabNames.Contains(GetEntityPrefabName(entityManager, prefabSystem, entity));
        }

        public static bool TryGetCombinedStorageData(
            EntityManager entityManager,
            Entity harbor,
            out StorageCompanyData storageCompanyData,
            out StorageLimitData storageLimitData,
            out bool hasLimit)
        {
            storageCompanyData = default;
            storageLimitData = default;
            hasLimit = false;

            if (!entityManager.HasComponent<PrefabRef>(harbor))
                return false;

            var prefab = entityManager.GetComponentData<PrefabRef>(harbor).m_Prefab;
            if (!entityManager.HasComponent<StorageCompanyData>(prefab))
                return false;

            storageCompanyData = entityManager.GetComponentData<StorageCompanyData>(prefab);
            hasLimit = entityManager.HasComponent<StorageLimitData>(prefab);
            if (hasLimit)
                storageLimitData = entityManager.GetComponentData<StorageLimitData>(prefab);

            if (!entityManager.HasBuffer<InstalledUpgrade>(harbor))
                return true;

            var upgrades = entityManager.GetBuffer<InstalledUpgrade>(harbor);
            UpgradeUtils.CombineStats(entityManager, ref storageCompanyData, upgrades);
            if (hasLimit)
                UpgradeUtils.CombineStats(entityManager, ref storageLimitData, upgrades);

            return true;
        }

        public static bool TryGetCombinedStoredResources(
            EntityManager entityManager,
            Entity harbor,
            out Resource combinedResources)
        {
            combinedResources = Resource.NoResource;
            if (!TryGetCombinedStorageData(entityManager, harbor, out var storageCompanyData, out _, out _))
                return false;

            combinedResources = storageCompanyData.m_StoredResources;
            return combinedResources != Resource.NoResource;
        }

        private static string GetEntityPrefabName(EntityManager entityManager, PrefabSystem prefabSystem, Entity entity)
        {
            if (!entityManager.HasComponent<PrefabRef>(entity))
                return string.Empty;

            return GetPrefabName(prefabSystem, entityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
        }

        private static string GetPrefabName(PrefabSystem prefabSystem, Entity prefab)
        {
            try
            {
                return prefabSystem.GetPrefabName(prefab) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
