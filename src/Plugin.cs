using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace AmongUs3D
{
    /// <summary>
    /// Point d'entrée BepInEx (Windows) / Starlight (Android).
    /// Le mod est 100 % côté client : les autres joueurs n'ont pas besoin de l'installer.
    /// </summary>
    [BepInPlugin(Id, Name, Version)]
    public class AmongUs3DPlugin : BasePlugin
    {
        public const string Id = "fr.mods.amongus3d";
        public const string Name = "AmongUs3D";
        public const string Version = "1.0.1";

        internal static ManualLogSource Logger;
        private Harmony _harmony;

        public override void Load()
        {
            Logger = Log;
            ModConfig.Bind(Config);
            InputCompat.Init();
            View3D.Create();
            AddComponent<View3DBehaviour>();

            _harmony = new Harmony(Id);
            try
            {
                _harmony.PatchAll(typeof(MovementPatch));
            }
            catch (Exception e)
            {
                Log.LogError("Impossible d'installer le patch de déplacement (la vue 3D restera utilisable, " +
                             "mais les déplacements resteront ceux de la 2D) : " + e);
            }

            Log.LogInfo($"{Name} {Version} chargé ({(Platform.IsMobile ? "mobile" : "PC")}). " +
                        "Souris = regarder, clic gauche = utiliser, Alt maintenu = curseur. " +
                        "F5 = 3D/2D, F6 = 1re/3e personne, F7 = mini-carte, F8 = souris libre/caméra, F9 = diagnostic, " +
                        "F10/F11 = sensibilité, F12 = capture d'écran.");
        }
    }

    internal static class Platform
    {
        private static int _mobile = -1;

        public static bool IsMobile
        {
            get
            {
                if (_mobile < 0)
                {
                    try
                    {
                        var p = Application.platform;
                        _mobile = (p == RuntimePlatform.Android || p == RuntimePlatform.IPhonePlayer) ? 1 : 0;
                    }
                    catch { _mobile = 0; }
                }
                return _mobile == 1;
            }
        }
    }

    /// <summary>
    /// Composant Unity injecté (persistant) : relaie Update / LateUpdate vers la logique C# classique.
    /// </summary>
    public class View3DBehaviour : MonoBehaviour
    {
        public View3DBehaviour(IntPtr ptr) : base(ptr) { }

        public void Update() => View3D.Instance?.SafeUpdate();

        public void LateUpdate() => View3D.Instance?.SafeLateUpdate();
    }
}
