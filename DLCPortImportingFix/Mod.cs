using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using System;
using System.Text.RegularExpressions;
using UnityEngine;

namespace DLCPortImportingFix
{
    public class Mod : IMod
    {
        public static ILog log = LogManager.GetLogger($"{nameof(DLCPortImportingFix)}.{nameof(Mod)}")
            .SetShowsErrorsInUI(false);

        public void OnLoad(UpdateSystem updateSystem)
        {
            if (IsOfficiallyFixedVersion(Application.version))
            {
                log.Info($"Game version {Application.version} includes the official Bridges & Ports harbor import/mail fix. Mod systems are disabled.");
                return;
            }

            log.Info($"Game version {Application.version} is older than the official fix. Registering harbor import/mail fix systems.");
            updateSystem.UpdateAt<HarborMailTransferPatchSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<HarborResourceSellerPatchSystem>(SystemUpdatePhase.GameSimulation);
        }

        public void OnDispose()
        {
        }

        private static bool IsOfficiallyFixedVersion(string version)
        {
            if (!TryParseGameVersion(version, out var parsedVersion))
            {
                log.Warn($"Could not parse game version '{version}'. Registering fix systems to keep compatibility with older builds.");
                return false;
            }

            var fixedVersion = new GameVersion(1, 5, 7, 1);
            return parsedVersion.CompareTo(fixedVersion) >= 0;
        }

        private static bool TryParseGameVersion(string version, out GameVersion gameVersion)
        {
            gameVersion = default;
            if (string.IsNullOrWhiteSpace(version))
            {
                return false;
            }

            var match = Regex.Match(version, @"(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)f(?<build>\d+)");
            if (!match.Success)
            {
                return false;
            }

            gameVersion = new GameVersion(
                int.Parse(match.Groups["major"].Value),
                int.Parse(match.Groups["minor"].Value),
                int.Parse(match.Groups["patch"].Value),
                int.Parse(match.Groups["build"].Value));
            return true;
        }

        private readonly struct GameVersion : IComparable<GameVersion>
        {
            private readonly int m_Major;
            private readonly int m_Minor;
            private readonly int m_Patch;
            private readonly int m_Build;

            public GameVersion(int major, int minor, int patch, int build)
            {
                m_Major = major;
                m_Minor = minor;
                m_Patch = patch;
                m_Build = build;
            }

            public int CompareTo(GameVersion other)
            {
                var majorComparison = m_Major.CompareTo(other.m_Major);
                if (majorComparison != 0)
                {
                    return majorComparison;
                }

                var minorComparison = m_Minor.CompareTo(other.m_Minor);
                if (minorComparison != 0)
                {
                    return minorComparison;
                }

                var patchComparison = m_Patch.CompareTo(other.m_Patch);
                if (patchComparison != 0)
                {
                    return patchComparison;
                }

                return m_Build.CompareTo(other.m_Build);
            }
        }
    }
}
