using System;
using System.Collections.Generic;
using UnityEngine;

namespace AmongUs3D
{
    /// <summary>Meubles modelés en 3D (réglage Decor3D) : voir <see cref="PropShape"/>.</summary>
    internal sealed partial class WallSystem
    {
        private readonly List<(float X, float Y)> _emergencyAt = new List<(float, float)>(), _consolesAt = new List<(float, float)>();
        private readonly Dictionary<PropKind, int> _propCounts = new Dictionary<PropKind, int>();
        private bool _emergencySolid;

        private static string NameOf(Solid s)
        {
            try { return s.Col.gameObject.name ?? ""; } catch { return ""; }
        }

        /// <summary>Repère le bouton d'urgence et les consoles de tâches, pour reconnaître les meubles qui les portent.</summary>
        private void FindPropHints(MonoBehaviour mapRoot)
        {
            _emergencyAt.Clear();
            _consolesAt.Clear();
            try
            {
                foreach (SystemConsole sc in mapRoot.GetComponentsInChildren<SystemConsole>(true))
                {
                    if (sc == null) continue;
                    bool emergency = false;
                    try { emergency = sc.MinigamePrefab != null && sc.MinigamePrefab.TryCast<EmergencyMinigame>() != null; }
                    catch { /* ignoré */ }
                    if (!emergency) emergency = sc.name.IndexOf("emergency", StringComparison.OrdinalIgnoreCase) >= 0;
                    Vector3 p = sc.transform.position;
                    (emergency ? _emergencyAt : _consolesAt).Add((p.x, p.y));
                }
            }
            catch (Exception e) { AmongUs3DPlugin.Logger.LogDebug("Consoles système : " + e.Message); }
            try
            {
                foreach (global::Console c in mapRoot.GetComponentsInChildren<global::Console>(true))
                {
                    if (c == null) continue;
                    Vector3 p = c.transform.position;
                    _consolesAt.Add((p.x, p.y));
                }
            }
            catch (Exception e) { AmongUs3DPlugin.Logger.LogDebug("Consoles de tâches : " + e.Message); }
        }

        /// <summary>Direction (unitaire) du mur "vision" le plus proche ; par défaut le nord (le jeu dessine les faces sud).</summary>
        private void NearestWallDir(float x, float y, out float dx, out float dy)
        {
            dx = 0f; dy = 1f;
            float best = 6f;
            foreach (WallSeg s in _segments)
            {
                if (!_meshes[s.owner].Scan) continue;
                float sx = s.bx - s.ax, sy = s.by - s.ay, l2 = sx * sx + sy * sy;
                float t = l2 < 1e-8f ? 0f : M.Clamp01(((x - s.ax) * sx + (y - s.ay) * sy) / l2);
                float qx = s.ax + sx * t - x, qy = s.ay + sy * t - y, d = MathF.Sqrt(qx * qx + qy * qy);
                if (d >= best || d < 1e-4f) continue;
                best = d; dx = qx / d; dy = qy / d;
            }
        }

        private static uint ToRgb(Color c) => ((uint)U.ToByte(c.r) << 16) | ((uint)U.ToByte(c.g) << 8) | U.ToByte(c.b);

        /// <summary>Meuble modelé : partie opaque colorée + peau peinte avec le dessin du jeu.</summary>
        private bool BuildProp(Solid s, MapKind map, float obstacleH, Color obstacleColor, int layer)
        {
            Poly poly = s.Polys[0];
            var xs = new List<float>(poly.X);
            var ys = new List<float>(poly.Y);
            PropShape.Clean(xs, ys);
            if (xs.Count < 3) return false;
            Footprint f = PropShape.Analyze(xs, ys);
            if (f.HalfLen < 0.03f) return false;

            var h = new PropHints
            {
                Indoor = map != MapKind.Polus && map != MapKind.Fungle,
                Blue = map == MapKind.Skeld,
                Height = obstacleH,
                BaseColor = ToRgb(obstacleColor),
            };
            // Si le bouton d'urgence a sa propre collision, c'est elle qui le dessine (voir PropKind.Button).
            if (!_emergencySolid)
                foreach ((float x, float y) in _emergencyAt)
                    if (PropShape.Near(f, x, y, 0.25f)) h.Emergency = true;
            NearestWallDir(f.Cx, f.Cy, out h.WallX, out h.WallY);
            h.LookX = h.WallX; h.LookY = h.WallY;
            float bestConsole = 2.5f;
            foreach ((float x, float y) in _consolesAt)
            {
                if (PropShape.Near(f, x, y, 0.35f)) h.TaskConsole = true;
                float dx = x - f.Cx, dy = y - f.Cy, d = MathF.Sqrt(dx * dx + dy * dy);
                if (d < bestConsole && d > 0.05f) { bestConsole = d; h.LookX = dx / d; h.LookY = dy / d; }
            }

            string name = NameOf(s);
            PropKind kind = PropShape.Classify(name, f, h);
            var solid = new ShapeMesh();
            var art = new ShapeMesh();
            float top = PropShape.Build(kind, f, xs, ys, h, solid, art);
            // Le dessin du jeu montre un point à la hauteur z en (x, y + z) ; il est calé sur la hauteur des anciens blocs.
            float artK = top > 0.01f ? M.Clamp(obstacleH / top, 0.3f, 1.5f) : 1f;

            Load(_b, solid, true, artK);
            if (Commit(_b, s.Col, U.Col(1f, 1f, 1f), layer, false, "AU3D_Meuble3D", false) == null) return false;
            Load(_bt, art, false, artK);
            Commit(_bt, s.Col, U.Col(1f, 1f, 1f), layer, false, "AU3D_Meuble3DDessin", true);

            _propCounts.TryGetValue(kind, out int count);
            _propCounts[kind] = count + 1;
            if (ModConfig.DumpGeometry.Value)
                AmongUs3DPlugin.Logger.LogInfo(FormattableString.Invariant(
                    $"PROP|{kind}|{name}|{f.Cx:0.00},{f.Cy:0.00}|{2f * f.HalfLen:0.00}x{2f * f.HalfWid:0.00}|rond={f.Roundness:0.00} plein={f.Fill:0.00}|urgence={h.Emergency} console={h.TaskConsole}"));
            return true;
        }

        private static void Load(Builder b, ShapeMesh m, bool colors, float artK)
        {
            b.Clear();
            for (int i = 0; i < m.VertexCount; i++)
            {
                float z = m.Z[i];
                b.Vertex(m.X[i], m.Y[i], -z, PropShape.Shade(m.Nx[i], m.Ny[i], m.Nz[i], z), m.X[i], m.Y[i] + z * artK);
                if (colors) b.Rgb.Add(m.Rgba[i] >> 8);
            }
            b.Tris.AddRange(m.Tris);
        }

        private string PropSummary()
        {
            if (_propCounts.Count == 0) return "";
            var sb = new System.Text.StringBuilder(" (3D :");
            foreach (KeyValuePair<PropKind, int> kv in _propCounts) sb.Append(' ').Append(kv.Value).Append(' ').Append(kv.Key);
            return sb.Append(')').ToString();
        }
    }
}
