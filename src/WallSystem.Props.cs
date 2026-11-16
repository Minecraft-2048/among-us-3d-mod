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
            return BuildProp(s.Col, NameOf(s), new List<float>(poly.X), new List<float>(poly.Y), false, map, obstacleH, obstacleColor, layer);
        }

        /// <param name="generic">vrai pour un contour d'objet adossé à un mur : sa forme est gardée telle quelle</param>
        private bool BuildProp(Collider2D col, string name, List<float> xs, List<float> ys, bool generic, MapKind map,
                               float obstacleH, Color obstacleColor, int layer)
        {
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

            // Vraie hauteur du meuble, lue sur son dessin (voir SpriteRise).
            if (!generic && ModConfig.TrueHeights.Value) h.ArtHeight = SpriteRise(col, xs, ys);
            PropKind kind = generic ? PropKind.Generic : PropShape.Classify(name, f, h);
            // Un pupitre ou une caisse dont le dessin monte haut est en réalité un meuble haut (armoire, machine).
            if ((kind == PropKind.Console || kind == PropKind.Crate) && h.ArtHeight >= 0.8f) kind = PropKind.Cabinet;
            var solid = new ShapeMesh();
            var art = new ShapeMesh();
            float top = PropShape.Build(kind, f, xs, ys, h, solid, art);
            // Le dessin du jeu montre un point à la hauteur z en (x, y + z). À la vraie hauteur il se plaque tel quel ;
            // sinon il est calé sur la hauteur des anciens blocs.
            bool trueHeight = kind == PropKind.Cabinet || (kind == PropKind.Generic && h.ArtHeight >= 0.2f);
            float artK = trueHeight ? 1f : (top > 0.01f ? M.Clamp(obstacleH / top, 0.3f, 1.5f) : 1f);

            Load(_b, solid, true, artK);
            if (Commit(_b, col, U.Col(1f, 1f, 1f), layer, false, "AU3D_Meuble3D", false) == null) return false;
            Load(_bt, art, false, artK);
            Commit(_bt, col, U.Col(1f, 1f, 1f), layer, false, "AU3D_Meuble3DDessin", true);

            _propCounts.TryGetValue(kind, out int count);
            _propCounts[kind] = count + 1;
            if (ModConfig.DumpGeometry.Value)
                AmongUs3DPlugin.Logger.LogInfo(FormattableString.Invariant(
                    $"PROP|{kind}|{name}|{f.Cx:0.00},{f.Cy:0.00}|{2f * f.HalfLen:0.00}x{2f * f.HalfWid:0.00}|rond={f.Roundness:0.00} plein={f.Fill:0.00}|urgence={h.Emergency} console={h.TaskConsole}|haut={top:0.00} dessin={h.ArtHeight:0.00}"));
            return true;
        }

        /// <summary>
        /// Vraie hauteur d'un meuble, lue sur son dessin : le jeu dessine le dessus d'un objet de hauteur H décalé de H
        /// vers le haut, donc son dessin dépasse d'autant le haut de sa collision. 0 si le dessin est introuvable ou douteux.
        /// </summary>
        private static float SpriteRise(Collider2D col, List<float> xs, List<float> ys)
        {
            try
            {
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                for (int i = 0; i < xs.Count; i++)
                {
                    minX = MathF.Min(minX, xs[i]); maxX = MathF.Max(maxX, xs[i]);
                    minY = MathF.Min(minY, ys[i]); maxY = MathF.Max(maxY, ys[i]);
                }
                SpriteRenderer sr = col.GetComponent<SpriteRenderer>();
                if (sr == null)
                {
                    Transform parent = col.transform.parent;
                    if (parent != null) sr = parent.GetComponent<SpriteRenderer>();
                }
                if (sr == null || sr.sprite == null || !sr.enabled) return 0f;
                Bounds b = sr.bounds;
                Vector3 bmin = b.min, bmax = b.max;
                // Ce doit être le dessin de ce meuble-là : à peu près sa largeur, posé au même endroit.
                float w = maxX - minX;
                if (bmax.x - bmin.x > 2.2f * w + 0.6f) return 0f;
                if (bmin.y < minY - 0.8f || bmin.y > maxY) return 0f;
                if (bmax.x < minX || bmin.x > maxX) return 0f;
                float rise = bmax.y - maxY;
                return rise > 2.2f ? 0f : MathF.Max(0f, rise); // plus haut qu'un mur : ce n'est pas le dessin de ce meuble
            }
            catch { return 0f; }
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

        /// <summary>
        /// Portes dont le passage va du nord au sud (toutes sortes : portes de sabotage, de décontamination...) :
        /// le jeu dessine leur battant à plat entre la collision et le mur "vision". On le redresse à l'entrée,
        /// sur un panneau plein, visible seulement quand la porte est fermée.
        /// </summary>
        private int BuildDoorPanels(MonoBehaviour mapRoot, float wallH, Color doorColor, int layer)
        {
            int count = 0;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var polys = new List<Poly>();
            SomeKindaDoor[] doors;
            try { doors = mapRoot.GetComponentsInChildren<SomeKindaDoor>(true); }
            catch (Exception e) { AmongUs3DPlugin.Logger.LogDebug("Portes : " + e.Message); return 0; }
            foreach (SomeKindaDoor door in doors)
            {
                if (door == null) continue;
                Collider2D box = Game.DoorCollider(door, out Collider2D shadow);
                if (box == null) continue;
                polys.Clear();
                Extract(box, polys);
                if (polys.Count == 0) continue;
                Bounds2(polys[0], out float minX, out float minY, out float maxX, out float maxY);
                float top = maxY + 0.1f;
                if (shadow != null)
                {
                    polys.Clear();
                    Extract(shadow, polys);
                    if (polys.Count > 0)
                    {
                        Bounds2(polys[0], out _, out _, out _, out float shadowTop);
                        if (shadowTop > minY + 0.3f) top = shadowTop;
                    }
                }
                bool across = maxX - minX >= 1.2f * (maxY - minY); // barre un passage nord-sud
                if (ModConfig.DumpGeometry.Value)
                    AmongUs3DPlugin.Logger.LogInfo(FormattableString.Invariant(
                        $"DOOR|{door.GetIl2CppType().Name}|{door.name}|{(across ? "travers" : "long")}|{minX:0.###},{minY:0.###},{maxX:0.###},{maxY:0.###}|haut={top:0.###}|trigger={box.isTrigger} actif={box.enabled}"));
                if (!across) continue; // passage est-ouest : le battant est vu par la tranche, le panneau uni suffit

                float d = M.Clamp(top - minY, 0.4f, MathF.Min(wallH, 2.2f));
                _b.Clear();
                WallGeometry.AddStrips(new[] { minX, maxX }, new[] { minY, minY }, false, wallH, 0.5f, 0.62f, _meshes.Count,
                                       _b.X, _b.Y, _b.Z, _b.Shade, _b.Tris, null);
                if (Commit(_b, box, doorColor, layer, true, "AU3D_PorteSupport", false) == null) continue;

                _bt.Clear();
                int pieces = Math.Max(1, (int)MathF.Ceiling((maxX - minX) / 0.5f));
                for (int k = 0; k < pieces; k++)
                {
                    float x0 = minX + (maxX - minX) * k / pieces, x1 = minX + (maxX - minX) * (k + 1) / pieces;
                    float y = minY - SkinOffset;
                    int b0 = _bt.Vertex(x0, y, 0f, 1f, x0, minY);
                    int t0 = _bt.Vertex(x0, y, -d, 1f, x0, minY + d);
                    int b1 = _bt.Vertex(x1, y, 0f, 1f, x1, minY);
                    int t1 = _bt.Vertex(x1, y, -d, 1f, x1, minY + d);
                    _bt.Quad(b0, t0, b1, t1);
                }
                Commit(_bt, box, U.Col(1f, 1f, 1f), layer, true, "AU3D_PorteDessin", true);
                count++;
            }
            return count;
        }

        /// <summary>Relief de la carte, déduit des collisions et des échelles (voir <see cref="Terrain"/>). Plat hors de The Fungle.</summary>
        private static Terrain BuildTerrain(MonoBehaviour mapRoot, List<Collider2D> colliders, int obstacleMask, MapKind kind)
        {
            if (!ModConfig.Relief.Value || kind != MapKind.Fungle || obstacleMask == 0) return Terrain.Flat;
            try
            {
                var barriers = new List<WallSeg>();
                var polys = new List<Poly>();
                foreach (Collider2D c in colliders)
                {
                    if ((obstacleMask & (1 << c.gameObject.layer)) == 0) continue;
                    if (c.isTrigger || !c.enabled || !c.gameObject.activeInHierarchy || Game.IsDoor(c)) continue;
                    polys.Clear();
                    Extract(c, polys);
                    foreach (Poly p in polys)
                    {
                        int n = p.X.Length, count = p.Closed ? n : n - 1;
                        for (int s = 0; s < count; s++)
                            barriers.Add(new WallSeg { ax = p.X[s], ay = p.Y[s], bx = p.X[(s + 1) % n], by = p.Y[(s + 1) % n] });
                    }
                }
                var ladders = new List<WallSeg>();
                foreach (Ladder l in mapRoot.GetComponentsInChildren<Ladder>(true))
                {
                    if (l == null || l.IsTop) continue;
                    Ladder top = l.Destination;
                    if (top == null) continue;
                    Vector3 a = l.transform.position, b = top.transform.position;
                    ladders.Add(new WallSeg { ax = a.x, ay = a.y, bx = b.x, by = b.y });
                }
                Terrain t = Terrain.Build(barriers, ladders);
                AmongUs3DPlugin.Logger.LogInfo("Relief : " + t.Summary);
                return t;
            }
            catch (Exception e)
            {
                AmongUs3DPlugin.Logger.LogWarning("Relief indisponible (carte plate) : " + e.Message);
                return Terrain.Flat;
            }
        }

        /// <summary>Diagnostic : échelles et tyrolienne, qui relient deux niveaux de la carte.</summary>
        private static void DumpLinks(MonoBehaviour mapRoot)
        {
            try
            {
                foreach (Ladder l in mapRoot.GetComponentsInChildren<Ladder>(true))
                {
                    if (l == null) continue;
                    Vector3 p = l.transform.position;
                    Ladder d = l.Destination;
                    Vector3 q = d != null ? d.transform.position : p;
                    AmongUs3DPlugin.Logger.LogInfo(FormattableString.Invariant(
                        $"LADDER|{l.name}|haut={l.IsTop}|{p.x:0.##},{p.y:0.##}|vers {q.x:0.##},{q.y:0.##}"));
                }
                foreach (ZiplineBehaviour z in mapRoot.GetComponentsInChildren<ZiplineBehaviour>(true))
                {
                    if (z == null) continue;
                    Vector3 a = z.landingPositionTop.position, b = z.landingPositionBottom.position;
                    Vector3 c = z.handleTop.position, d = z.handleBottom.position;
                    AmongUs3DPlugin.Logger.LogInfo(FormattableString.Invariant(
                        $"ZIPLINE|haut {a.x:0.##},{a.y:0.##}|bas {b.x:0.##},{b.y:0.##}|poignées {c.x:0.##},{c.y:0.##} et {d.x:0.##},{d.y:0.##}"));
                }
            }
            catch (Exception e) { AmongUs3DPlugin.Logger.LogInfo("LADDER|erreur|" + e.Message); }
        }

        private static void Bounds2(Poly p, out float minX, out float minY, out float maxX, out float maxY)
        {
            minX = minY = float.MaxValue; maxX = maxY = float.MinValue;
            for (int i = 0; i < p.X.Length; i++)
            {
                minX = MathF.Min(minX, p.X[i]); maxX = MathF.Max(maxX, p.X[i]);
                minY = MathF.Min(minY, p.Y[i]); maxY = MathF.Max(maxY, p.Y[i]);
            }
        }

        /// <summary>Diagnostic (réglage ExporterLaGeometrie) : le classement final de chaque morceau de collision physique.</summary>
        private static void DumpPieces(List<Solid> solids)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            foreach (Solid s in solids)
            {
                sb.Clear();
                sb.Append("PIECE|").Append(NameOf(s)).Append('|').Append(s.ShortLayer ? 'S' : '-').Append(s.Furniture ? 'F' : '-').Append('|');
                foreach (Edge e in s.Edges)
                    for (int k = 0; k < e.Kind.Length; k++)
                    {
                        PieceEnds(e, k, out float x0, out float y0, out float x1, out float y1);
                        sb.Append(x0.ToString("0.##", inv)).Append(',').Append(y0.ToString("0.##", inv)).Append(',')
                          .Append(x1.ToString("0.##", inv)).Append(',').Append(y1.ToString("0.##", inv)).Append(',').Append(e.Kind[k]);
                        if (e.Kind[k] == Face) sb.Append(':').Append(e.D[k].ToString("0.##", inv));
                        sb.Append(' ');
                    }
                AmongUs3DPlugin.Logger.LogInfo(sb.ToString());
                if (s.BumpX == null) continue;
                for (int b = 0; b < s.BumpX.Count; b++)
                {
                    sb.Clear();
                    sb.Append("BUMP|").Append(NameOf(s)).Append('|');
                    for (int i = 0; i < s.BumpX[b].Count; i++)
                        sb.Append(s.BumpX[b][i].ToString("0.##", inv)).Append(',').Append(s.BumpY[b][i].ToString("0.##", inv)).Append(' ');
                    AmongUs3DPlugin.Logger.LogInfo(sb.ToString());
                }
            }
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
