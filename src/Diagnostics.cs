using System;
using System.Text;
using UnityEngine;

namespace AmongUs3D
{
    /// <summary>
    /// Écrit dans le journal BepInEx (BepInEx/LogOutput.log) tout ce qui aide à régler le mod
    /// sur une nouvelle version du jeu : calques, caméras, matériaux des personnages, murs trouvés...
    /// </summary>
    internal static class Diagnostics
    {
        public static void Dump(Rig rig, WallSystem walls, ProxySystem proxies, Controls controls, KeyHints hints, int layer3D, int layerMini,
                                bool active, bool want3D, PlayerControl local, Camera main)
        {
            var sb = new StringBuilder();
            sb.AppendLine("===== AmongUs3D : diagnostic =====");
            Try(sb, () => sb.AppendLine($"Mod {AmongUs3DPlugin.Version} | Unity {Application.unityVersion} | {Application.platform} | jeu {Application.version} | écran {Screen.width}x{Screen.height}"));
            sb.AppendLine($"3D voulue={want3D} active={active} | calque 3D={layer3D} mini-carte={layerMini} | entrées tactiles={InputCompat.TouchAvailable} axe souris={InputCompat.MouseAxisAvailable}");
            if (controls != null)
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                sb.AppendLine($"Caméra : souris={controls.MouseLook} curseur verrouillé={controls.Captured} tank={controls.TankActive} " +
                              $"lacet={controls.Yaw.ToString("0.0", inv)} tangage={controls.Pitch.ToString("0.0", inv)} | " +
                              $"axe souris cumulé depuis le dernier diagnostic=({controls.DebugAxisX.ToString("0.###", inv)}, {controls.DebugAxisY.ToString("0.###", inv)}) " +
                              $"échelle={ModConfig.MouseAxisScale.Value.ToString("0.##", inv)} sensibilité={ModConfig.MouseSensitivity.Value.ToString("0.###", inv)}");
                controls.DebugAxisX = 0f;
                controls.DebugAxisY = 0f;
            }

            Try(sb, () =>
            {
                sb.Append("Calques : ");
                for (int i = 0; i < 32; i++)
                {
                    string n = U.LayerName(i);
                    if (!string.IsNullOrEmpty(n)) sb.Append(i).Append('=').Append(n).Append(' ');
                }
                sb.AppendLine();
            });

            Try(sb, () =>
            {
                sb.AppendLine("Caméras actives :");
                foreach (Camera c in Camera.allCameras)
                {
                    if (c == null) continue;
                    sb.AppendLine($"  - {c.name} depth={c.depth} ortho={c.orthographic} size={c.orthographicSize:0.##} " +
                                  $"clear={c.clearFlags} cible={(c.targetTexture != null ? c.targetTexture.name : "écran")} " +
                                  $"masque=[{U.MaskToNames(c.cullingMask)}]");
                }
            });

            if (main != null)
                Try(sb, () => sb.AppendLine($"Caméra principale : {main.name} pos={main.transform.position} near={main.nearClipPlane} far={main.farClipPlane} tri={main.transparencySortMode}"));

            Try(sb, () =>
            {
                MeshRenderer sq = Game.ShadowQuad;
                if (sq == null) { sb.AppendLine("ShadowQuad : absent"); return; }
                Material m = sq.sharedMaterial;
                sb.AppendLine($"ShadowQuad : calque={sq.gameObject.layer} ({U.LayerName(sq.gameObject.layer)}) actif={sq.gameObject.activeInHierarchy} " +
                              $"shader={(m != null && m.shader != null ? m.shader.name : "?")} ordre={sq.sortingOrder}");
            });

            if (local != null)
            {
                Try(sb, () =>
                {
                    sb.AppendLine($"Joueur local : pos={local.transform.position} vivant={!Game.IsDead(local)} bouche={Game.InVent(local)} peutBouger={Game.CanMove(local)}");
                    foreach (Renderer r in local.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r == null) continue;
                        Material m = r.sharedMaterial;
                        string shader = m != null && m.shader != null ? m.shader.name : "?";
                        string stencil = "";
                        if (m != null && m.HasProperty("_StencilComp")) stencil += $" _StencilComp={m.GetFloat("_StencilComp")}";
                        if (m != null && m.HasProperty("_MaskComp")) stencil += $" _MaskComp={m.GetFloat("_MaskComp")}";
                        var sr = r.TryCast<SpriteRenderer>();
                        string mask = sr != null ? " masque=" + sr.maskInteraction : "";
                        sb.AppendLine($"    {r.gameObject.name} [{r.GetIl2CppType().Name}] calque={U.LayerName(r.gameObject.layer)} actif={r.enabled && r.gameObject.activeInHierarchy} " +
                                      $"shader={shader} tri={r.sortingLayerID}/{r.sortingOrder}{mask}{stencil}");
                    }
                });
            }

            sb.AppendLine("Murs : " + (walls != null ? walls.Summary : "-"));
            sb.AppendLine("Personnages (joueurs/animaux/corps) : " + (proxies != null ? $"{proxies.Count}, dont {proxies.ModelCount} en modèle 3D" : "-"));
            if (proxies != null) Try(sb, () => proxies.Describe(sb));
            if (hints != null) Try(sb, () => hints.Describe(sb));
            sb.AppendLine("Rig : " + (rig != null && rig.IsAlive ? $"OK (sol {rig.FloorHalf:0.#})" : "non créé"));
            sb.Append("===== fin du diagnostic =====");
            AmongUs3DPlugin.Logger.LogInfo(sb.ToString());
        }

        private static void Try(StringBuilder sb, Action a)
        {
            try { a(); }
            catch (Exception e) { sb.AppendLine("  (erreur : " + e.GetType().Name + " " + e.Message + ")"); }
        }
    }
}
