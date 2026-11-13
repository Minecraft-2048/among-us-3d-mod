using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace AmongUs3D
{
    /// <summary>
    /// Après le calcul normal de la vitesse par le jeu (PlayerPhysics.FixedUpdate), on la fait tourner
    /// selon l'orientation de la caméra 3D : "haut" devient "avancer", etc.
    /// La norme n'est jamais augmentée : vitesse, collisions et synchronisation réseau restent celles du jeu.
    /// Même principe que le modificateur "Invert" de The Other Roles (velocity *= -1).
    /// </summary>
    [HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.FixedUpdate))]
    internal static class MovementPatch
    {
        private static bool _broken;

        public static void Postfix(PlayerPhysics __instance)
        {
            if (_broken || !View3D.MovementOverride) return;
            try
            {
                Apply(__instance);
            }
            catch (Exception e)
            {
                _broken = true;
                AmongUs3DPlugin.Logger.LogError("Patch de déplacement désactivé suite à une erreur : " + e);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Apply(PlayerPhysics physics)
        {
            PlayerControl owner = physics.myPlayer;
            if (owner == null) return;
            PlayerControl local = PlayerControl.LocalPlayer;
            if (local == null || owner.Pointer != local.Pointer) return;

            Rigidbody2D body = physics.body;
            if (body == null) return;

            View3D.RecordFixedStep(body.position);

            if (!owner.CanMove) return;
            Vector2 v = body.velocity;
            if (v.x * v.x + v.y * v.y < 1e-8f) return;

            M.TransformVelocity(v.x, v.y, View3D.CurrentYaw, View3D.TankMode, out float ox, out float oy);
            body.velocity = U.V2(ox, oy);
        }
    }
}
