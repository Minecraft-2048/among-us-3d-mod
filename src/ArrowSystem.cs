using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace AmongUs3D
{
    /// <summary>
    /// Flèches du jeu (sabotages, cibles...) en vue 3D. Le jeu les place selon la vue de dessus
    /// ("en haut de l'écran" = nord) ; ici, après son calcul, on les replace selon la caméra 3D :
    ///  - la cible est dans le champ de vision : la flèche se pose juste au-dessus d'elle, pointe vers le bas ;
    ///  - sinon : boussole autour du centre de l'écran (en haut = devant, à droite = à droite, en bas = derrière).
    /// Les flèches restent celles du jeu (mêmes images, mêmes couleurs), seules leur place et leur
    /// orientation changent, et uniquement tant que la vue 3D est affichée.
    /// </summary>
    internal sealed class ArrowSystem
    {
        private readonly List<ArrowBehaviour> _arrows = new List<ArrowBehaviour>();
        private float _nextScan;
        private bool _broken;

        public int Count => _arrows.Count;

        public void Update(Rig rig, float px, float py, float yaw)
        {
            if (_broken || rig == null || !ModConfig.SabotageArrows3D.Value) return;
            try
            {
                UpdateImpl(rig, px, py, yaw);
            }
            catch (Exception e)
            {
                _broken = true;
                AmongUs3DPlugin.Logger.LogWarning("Flèches 3D désactivées (les flèches restent celles de la vue 2D) : " + e.Message);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void UpdateImpl(Rig rig, float px, float py, float yaw)
        {
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 0.3f;
                _arrows.Clear();
                foreach (ArrowBehaviour a in UnityEngine.Object.FindObjectsOfType<ArrowBehaviour>())
                    if (a != null) _arrows.Add(a);
            }
            if (_arrows.Count == 0) return;

            M.YawBasis(yaw, out float fx, out float fy, out float rx, out float ry);
            rig.UiFrame(out float cx, out float cy, out float halfW, out float halfH);
            if (halfW < 0.01f || halfH < 0.01f) return;
            // Boussole : un peu plus large que haute, sans aller jusqu'aux bords d'un écran très large.
            float radX = MathF.Min(halfW * 0.88f, halfH * 1.7f), radY = halfH * 0.74f;

            for (int i = 0; i < _arrows.Count; i++)
            {
                ArrowBehaviour a = _arrows[i];
                if (!U.Alive(a)) continue;
                SpriteRenderer img = a.image;
                if (img == null || !img.enabled || !a.gameObject.activeInHierarchy) continue; // cachée par le jeu (cible toute proche)

                Vector3 target = a.target;
                float dx = target.x - px, dy = target.y - py;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                if (dist < 0.05f) continue;

                Transform tr = a.transform;
                float z = tr.position.z;
                float wx, wy, angle;
                if (rig.Project(target.x, target.y, -0.45f, out float vx, out float vy)
                    && vx > 0.06f && vx < 0.94f && vy > 0.12f && vy < 0.8f)
                {
                    // Devant nous : juste au-dessus de l'endroit visé, pointe vers le bas.
                    wx = cx + (vx * 2f - 1f) * halfW;
                    wy = cy + (vy * 2f - 1f) * halfH + 0.55f;
                    angle = -90f;
                }
                else
                {
                    float f = (dx * fx + dy * fy) / dist, r = (dx * rx + dy * ry) / dist;
                    wx = cx + r * radX;
                    wy = cy + f * radY;
                    angle = MathF.Atan2(f * radY, r * radX) / M.Deg2Rad;
                }
                tr.position = U.V3(wx, wy, z);
                tr.rotation = U.RotationZ(angle);
                tr.localScale = U.V3(1f, 1f, 1f);
            }
        }
    }
}
