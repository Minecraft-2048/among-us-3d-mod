using System;
using System.Collections.Generic;

namespace AmongUs3D
{
    /// <summary>
    /// Relief d'une carte à plusieurs niveaux (The Fungle : la jungle en bas, les hauteurs au-dessus de la falaise).
    /// Le jeu est plat : rien n'y dit à quelle hauteur se trouve un endroit. On le déduit :
    ///  - les collisions découpent la carte en zones où l'on marche sans obstacle ;
    ///  - une échelle relie deux zones, et sa longueur à l'écran est leur dénivelé (le jeu dessine un point
    ///    situé à la hauteur h au-dessus du sol décalé de h vers le haut) ;
    ///  - entre deux zones (la falaise, les rochers), la hauteur varie en douceur.
    /// Calcul pur (aucun type Unity), fait une fois par carte.
    /// </summary>
    internal sealed class Terrain
    {
        public static readonly Terrain Flat = new Terrain();

        /// <summary>Faux = carte plate : <see cref="Height"/> vaut 0 partout.</summary>
        public bool Active { get; private set; }
        public float MaxHeight { get; private set; }
        public int ZoneCount { get; private set; }
        public string Summary { get; private set; } = "plat";

        private float _x0, _y0, _cell;
        private int _w, _h;
        private float[] _height;

        private Terrain() { }

        /// <param name="barriers">collisions physiques (ce que le personnage ne traverse pas)</param>
        /// <param name="ladders">échelles : pied (ax, ay) et sommet (bx, by)</param>
        public static Terrain Build(List<WallSeg> barriers, List<WallSeg> ladders, float cell = 0.25f)
        {
            if (barriers == null || ladders == null || barriers.Count == 0 || ladders.Count == 0) return Flat;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (WallSeg s in barriers)
            {
                minX = MathF.Min(minX, MathF.Min(s.ax, s.bx)); maxX = MathF.Max(maxX, MathF.Max(s.ax, s.bx));
                minY = MathF.Min(minY, MathF.Min(s.ay, s.by)); maxY = MathF.Max(maxY, MathF.Max(s.ay, s.by));
            }
            var t = new Terrain { _cell = cell, _x0 = minX - 2f, _y0 = minY - 2f };
            t._w = (int)((maxX + 2f - t._x0) / cell) + 1;
            t._h = (int)((maxY + 2f - t._y0) / cell) + 1;
            if (t._w < 4 || t._h < 4 || (long)t._w * t._h > 1_500_000) return Flat;
            int w = t._w, h = t._h, n = w * h;

            // ---- collisions -> cases infranchissables
            var blocked = new bool[n];
            foreach (WallSeg s in barriers)
            {
                float dx = s.bx - s.ax, dy = s.by - s.ay, len = MathF.Sqrt(dx * dx + dy * dy);
                int steps = Math.Max(1, (int)(len / (cell * 0.4f)));
                for (int k = 0; k <= steps; k++)
                {
                    int i = (int)((s.ax + dx * k / steps - t._x0) / cell), j = (int)((s.ay + dy * k / steps - t._y0) / cell);
                    if (i >= 0 && i < w && j >= 0 && j < h) blocked[j * w + i] = true;
                }
            }

            // ---- zones : remplissage de proche en proche (4 voisins : une ligne de cases bloquées ne se traverse pas)
            var zone = new int[n];
            var size = new List<int> { 0 };
            var queue = new Queue<int>();
            int Fill(int start)
            {
                if (start < 0) return 0;
                if (zone[start] != 0) return zone[start];
                int id = size.Count, count = 0;
                zone[start] = id;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int c = queue.Dequeue();
                    count++;
                    int ci = c % w, cj = c / w;
                    if (ci > 0) Visit(c - 1);
                    if (ci < w - 1) Visit(c + 1);
                    if (cj > 0) Visit(c - w);
                    if (cj < h - 1) Visit(c + w);
                }
                size.Add(count);
                return id;

                void Visit(int v)
                {
                    if (blocked[v] || zone[v] != 0) return;
                    zone[v] = id;
                    queue.Enqueue(v);
                }
            }
            int Near(float x, float y)
            {
                int i0 = (int)((x - t._x0) / cell), j0 = (int)((y - t._y0) / cell);
                for (int r = 0; r <= 6; r++)
                    for (int dj = -r; dj <= r; dj++)
                        for (int di = -r; di <= r; di++)
                        {
                            if (Math.Max(Math.Abs(di), Math.Abs(dj)) != r) continue;
                            int i = i0 + di, j = j0 + dj;
                            if (i >= 0 && i < w && j >= 0 && j < h && !blocked[j * w + i]) return j * w + i;
                        }
                return -1;
            }
            int outside = Fill(blocked[0] ? Near(t._x0, t._y0) : 0);

            // ---- échelles -> dénivelés entre zones
            var links = new List<(int Low, int High, float Rise)>();
            foreach (WallSeg l in ladders)
            {
                int a = Fill(Near(l.ax, l.ay)), b = Fill(Near(l.bx, l.by));
                float rise = l.by - l.ay;
                if (a == 0 || b == 0 || a == b || a == outside || b == outside || rise < 0.5f) continue;
                links.Add((a, b, rise));
            }
            if (links.Count == 0) return Flat;

            var level = new Dictionary<int, float> { [links[0].Low] = 0f };
            for (bool changed = true; changed;)
            {
                changed = false;
                foreach ((int low, int high, float rise) in links)
                {
                    if (level.TryGetValue(low, out float lv) && !level.ContainsKey(high)) { level[high] = lv + rise; changed = true; }
                    else if (level.TryGetValue(high, out float hv) && !level.ContainsKey(low)) { level[low] = hv - rise; changed = true; }
                }
            }
            // Le niveau 0 est celui de la plus grande zone (le gros de la carte), les autres sont comptés à partir de lui.
            int biggest = -1;
            foreach (int id in level.Keys)
                if (biggest < 0 || size[id] > size[biggest]) biggest = id;
            float baseLevel = level[biggest];
            float lowest = float.MaxValue, highest = float.MinValue;
            var ids = new List<int>(level.Keys);
            foreach (int id in ids)
            {
                level[id] -= baseLevel;
                lowest = MathF.Min(lowest, level[id]); highest = MathF.Max(highest, level[id]);
            }

            // ---- hauteur de chaque case : fixée dans les zones connues, lissée ailleurs (falaises, rochers, vide)
            var height = new float[n];
            var known = new bool[n];
            for (int c = 0; c < n; c++)
                if (zone[c] != 0 && level.TryGetValue(zone[c], out float v)) { height[c] = v; known[c] = true; }
            for (int sweep = 0; sweep < 160; sweep++)
            {
                bool forward = (sweep & 1) == 0;
                for (int q = 0; q < n; q++)
                {
                    int c = forward ? q : n - 1 - q;
                    if (known[c]) continue;
                    int ci = c % w, cj = c / w;
                    float sum = 0f;
                    int cnt = 0;
                    if (ci > 0) { sum += height[c - 1]; cnt++; }
                    if (ci < w - 1) { sum += height[c + 1]; cnt++; }
                    if (cj > 0) { sum += height[c - w]; cnt++; }
                    if (cj < h - 1) { sum += height[c + w]; cnt++; }
                    height[c] = sum / cnt;
                }
            }

            t._height = height;
            t.Active = true;
            t.MaxHeight = highest - lowest;
            t.ZoneCount = level.Count;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var parts = new List<string>();
            foreach (int id in ids) parts.Add(level[id].ToString("+0.0;-0.0;0", inv));
            t.Summary = $"{level.Count} niveaux ({string.Join(", ", parts)}), {links.Count} échelles";
            return t;
        }

        /// <summary>Hauteur du sol en (x, y), en unités de la carte (0 = niveau principal).</summary>
        public float Height(float x, float y)
        {
            if (!Active) return 0f;
            float fx = (x - _x0) / _cell - 0.5f, fy = (y - _y0) / _cell - 0.5f;
            int i = (int)MathF.Floor(fx), j = (int)MathF.Floor(fy);
            float u = fx - i, v = fy - j;
            if (i < 0) { i = 0; u = 0f; } else if (i >= _w - 1) { i = _w - 2; u = 1f; }
            if (j < 0) { j = 0; v = 0f; } else if (j >= _h - 1) { j = _h - 2; v = 1f; }
            int c = j * _w + i;
            float a = _height[c] + (_height[c + 1] - _height[c]) * u;
            float b = _height[c + _w] + (_height[c + _w + 1] - _height[c + _w]) * u;
            return a + (b - a) * v;
        }
    }
}
