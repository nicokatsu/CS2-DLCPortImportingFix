using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;

namespace DLCPortImportingFix
{
    public class Mod : IMod
    {
        public static ILog log = LogManager.GetLogger($"{nameof(DLCPortImportingFix)}.{nameof(Mod)}")
            .SetShowsErrorsInUI(false);

        public void OnLoad(UpdateSystem updateSystem)
        {
            updateSystem.UpdateAt<HarborResourceSellerPatchSystem>(SystemUpdatePhase.GameSimulation);
        }

        public void OnDispose()
        {
        }
    }
}
