using System;
using System.Collections.Generic;

namespace AmongUs3D
{
    /// <summary>
    /// Règles (pures, testables hors du jeu) qui complètent le classement des collisions physiques d'une carte.
    /// Une collision est une chaîne de petits morceaux (≤ 0,5) ; certains restent "bas" (muret de 30 cm) alors que :
    ///  - ce sont des murs : un trou dans un mur droit, ou tout un côté de salle dont le mur "vision" est en retrait ;
    ///  - ce sont des objets adossés à un mur (réacteur, pupitre...) : leur contour, refermé, devient un meuble.
    /// Le côté praticable d'un morceau se déduit de l'ensemble des collisions (règle pair-impair).
    /// </summary>
    internal static class WallRules
    {
        // Nature d'un morceau (mêmes valeurs que dans WallSystem).
        public const int Hidden = 0, Low = 1, Face = 2, Tall = 3, Bump = 4;

        public struct Piece
        {
            public float Ax, Ay, Bx, By;
            public int Kind;
        }

        private const float Probe = 0.12f;       // distance des points testés de part et d'autre d'un morceau
        private const float WallReach = 3f;      // un mur "vision" plus loin que cela, derrière, n'est plus "ce mur"
        private static readonly float Cos15 = MathF.Cos(15f * M.Deg2Rad), Cos25 = MathF.Cos(25f * M.Deg2Rad), Cos40 = MathF.Cos(40f * M.Deg2Rad);
        // Trois directions quelconques (unitaires) : on vote, pour qu'un rayon passant par un sommet ne trompe pas.
        private static readonly float[] RayX = { 0.99991f, -0.20552f, -0.82432f }, RayY = { 0.01370f, 0.97865f, -0.56610f };

        /// <summary>
        /// Vrai si le point est dans la zone où l'on marche : les collisions physiques en sont la frontière,
        /// un point praticable en est séparé de l'extérieur par un nombre impair de traversées.
        /// </summary>
        public static bool Walkable(float x, float y, List<WallSeg> physical)
        {
            int votes = 0;
            for (int r = 0; r < 3; r++)
            {
                int count = 0;
                for (int i = 0; i < physical.Count; i++)
                {
                    WallSeg s = physical[i];
                    if (Geo.RaySeg(x, y, RayX[r], RayY[r], s.ax, s.ay, s.bx, s.by) > 1e-5f) count++;
                }
                votes += count & 1;
            }
            return votes >= 2;
        }

        /// <summary>Vrai si le premier obstacle rencontré depuis O dans la direction D (unitaire), à moins de max, est un mur "vision".</summary>
        public static bool VisionFirst(float ox, float oy, float dx, float dy, float max, List<WallSeg> vision, List<WallSeg> physical)
        {
            float tv = float.MaxValue;
            for (int i = 0; i < vision.Count; i++)
            {
                WallSeg s = vision[i];
                float t = Geo.RaySeg(ox, oy, dx, dy, s.ax, s.ay, s.bx, s.by);
                if (t > 0.05f && t < tv) tv = t;
            }
            if (tv > max) return false;
            for (int i = 0; i < physical.Count; i++)
            {
                WallSeg s = physical[i];
                float t = Geo.RaySeg(ox, oy, dx, dy, s.ax, s.ay, s.bx, s.by);
                if (t > 0.05f && t < tv) return false;
            }
            return true;
        }

        private static void Dir(in Piece p, out float dx, out float dy)
        {
            dx = p.Bx - p.Ax; dy = p.By - p.Ay;
            float l = MathF.Sqrt(dx * dx + dy * dy);
            if (l < 1e-6f) { dx = 1f; dy = 0f; return; }
            dx /= l; dy /= l;
        }

        // Voisin d'une section droite, sans savoir de quel côté on marche.
        private const int NearNone = 0, NearWallAligned = 1, NearWallAngled = 2, NearLow = 3;

        private static int Neighbour(Piece[] c, bool closed, int idx, float dx, float dy)
        {
            int n = c.Length;
            if (idx < 0 || idx >= n)
            {
                if (!closed) return NearNone;
                idx = ((idx % n) + n) % n;
            }
            if (c[idx].Kind == Low || c[idx].Kind == Bump) return NearLow;
            Dir(c[idx], out float ex, out float ey);
            return ex * dx + ey * dy >= Cos40 ? NearWallAligned : NearWallAngled; // un mur qui continue, même en biais
        }

        // Ce qu'il y a au bout d'une section droite.
        private const int EndNone = 0, EndWall = 1, EndAligned = 2, EndHollow = 3, EndCorner = 4;

        private static int End(Piece[] c, bool closed, int idx, float dx, float dy, float wx, float wy, bool arriving)
        {
            int n = c.Length;
            if (idx < 0 || idx >= n)
            {
                if (!closed) return EndNone;
                idx = ((idx % n) + n) % n;
            }
            Dir(c[idx], out float ex, out float ey);
            if (ex * dx + ey * dy >= Cos25) return c[idx].Kind == Low || c[idx].Kind == Bump ? EndAligned : EndWall;
            // Le voisin part-il du côté praticable (coin rentrant d'une salle) ou du côté plein (coin saillant) ?
            float side = arriving ? -(ex * wx + ey * wy) : ex * wx + ey * wy;
            return side > 0f ? EndHollow : EndCorner;
        }

        /// <summary>
        /// Transforme en murs pleins les sections droites de morceaux bas qui sont en réalité des murs :
        /// celles qui prolongent un mur (trou dans un mur droit), et les côtés entiers de salle (entre deux coins
        /// rentrants) derrière lesquels le premier obstacle est un mur "vision". Renvoie le nombre de morceaux changés.
        /// </summary>
        public static int CompleteWalls(Piece[] c, bool closed, List<WallSeg> vision, List<WallSeg> physical)
        {
            int n = c.Length, total = 0;
            for (int pass = 0; pass < 4 && n > 0; pass++)
            {
                int changed = 0, i = 0;
                while (i < n)
                {
                    if (c[i].Kind != Low) { i++; continue; }
                    Dir(c[i], out float d0x, out float d0y);
                    int first = i, last = i;
                    while (last + 1 < n && c[last + 1].Kind == Low)
                    {
                        Dir(c[last + 1], out float ex, out float ey);
                        if (ex * d0x + ey * d0y < Cos15) break;
                        last++;
                    }
                    i = last + 1;

                    float lx = c[last].Bx - c[first].Ax, ly = c[last].By - c[first].Ay, len = MathF.Sqrt(lx * lx + ly * ly);
                    if (len < 1e-4f) continue;
                    float dx = lx / len, dy = ly / len, nx = -dy, ny = dx;
                    int m = last - first + 1;

                    // 1) Prolonge un mur : un voisin aligné est un mur. Aucun besoin de savoir de quel côté on marche.
                    int na = Neighbour(c, closed, first - 1, dx, dy), nb = Neighbour(c, closed, last + 1, dx, dy);
                    bool tall = na == NearWallAligned || nb == NearWallAligned;

                    // 2) Côté de salle entre deux coins de mur : un mur "vision" est le premier obstacle, d'un côté ou de l'autre.
                    if (!tall && na == NearWallAngled && nb == NearWallAngled)
                    {
                        int hits = 0;
                        for (int q = first; q <= last; q++)
                        {
                            float mx = (c[q].Ax + c[q].Bx) * 0.5f, my = (c[q].Ay + c[q].By) * 0.5f;
                            if (VisionFirst(mx, my, nx, ny, 1.5f, vision, physical) || VisionFirst(mx, my, -nx, -ny, 1.5f, vision, physical)) hits++;
                        }
                        tall = hits >= 0.7f * m;
                    }

                    // 3) Sinon il faut savoir de quel côté on marche : côté de salle entre deux coins rentrants (ou un bout
                    //    de chaîne), avec un mur "vision" pour premier obstacle derrière.
                    if (!tall)
                    {
                        int wa = 0, wb = 0;
                        for (int q = first; q <= last; q++)
                        {
                            float mx = (c[q].Ax + c[q].Bx) * 0.5f, my = (c[q].Ay + c[q].By) * 0.5f;
                            if (Walkable(mx + nx * Probe, my + ny * Probe, physical)) wa++;
                            if (Walkable(mx - nx * Probe, my - ny * Probe, physical)) wb++;
                        }
                        float sign;
                        if (wa * 2 > m && wb * 2 < m) sign = 1f;
                        else if (wb * 2 > m && wa * 2 < m) sign = -1f;
                        else continue; // praticable des deux côtés (barrière isolée) ou d'aucun
                        float wx = nx * sign, wy = ny * sign; // normale vers le côté praticable

                        int a = End(c, closed, first - 1, dx, dy, wx, wy, true), b = End(c, closed, last + 1, dx, dy, wx, wy, false);
                        if ((a == EndHollow || a == EndNone) && (b == EndHollow || b == EndNone) && !(a == EndNone && b == EndNone))
                        {
                            int hits = 0;
                            for (int q = first; q <= last; q++)
                            {
                                float mx = (c[q].Ax + c[q].Bx) * 0.5f, my = (c[q].Ay + c[q].By) * 0.5f;
                                if (VisionFirst(mx, my, -wx, -wy, WallReach, vision, physical)) hits++;
                            }
                            tall = hits >= 0.7f * m;
                        }
                    }
                    if (!tall) continue;
                    for (int q = first; q <= last; q++) c[q].Kind = Tall;
                    changed += m;
                }
                total += changed;
                if (changed == 0) break;
            }
            return total;
        }

        /// <summary>
        /// Repère les objets bas adossés à un mur : une suite de morceaux bas qui s'avance dans la zone praticable
        /// et dont le contour, refermé par sa corde, est massif (ni éclat, ni triangle) et plein à l'intérieur.
        /// Ses morceaux passent à <see cref="Bump"/> ; le polygone (sens direct) est ajouté aux listes.
        /// </summary>
        public static void FindBumps(Piece[] c, bool closed, List<WallSeg> physical, List<List<float>> outX, List<List<float>> outY)
        {
            int n = c.Length;
            if (n < 3) return;
            int start = 0;
            if (closed)
            {
                start = -1;
                for (int i = 0; i < n; i++)
                    if (c[i].Kind != Low) { start = i; break; }
                if (start < 0) return; // boucle entièrement basse : c'est un meuble ordinaire
            }
            int k = 0;
            while (k < n)
            {
                if (c[(start + k) % n].Kind != Low) { k++; continue; }
                int k1 = k;
                while (k1 + 1 < n && c[(start + k1 + 1) % n].Kind == Low) k1++;
                int from = k, count = k1 - k + 1;
                k = k1 + 1;
                if (count < 3) continue;

                var xs = new List<float>(count + 1);
                var ys = new List<float>(count + 1);
                Piece p0 = c[(start + from) % n];
                xs.Add(p0.Ax); ys.Add(p0.Ay);
                for (int q = 0; q < count; q++)
                {
                    Piece p = c[(start + from + q) % n];
                    xs.Add(p.Bx); ys.Add(p.By);
                }
                float ax = xs[0], ay = ys[0], bx = xs[count], by = ys[count];
                float cl = MathF.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                if (cl < 0.05f) continue;
                float ux = (bx - ax) / cl, uy = (by - ay) / cl;

                // morceau le plus éloigné de la corde : un point juste derrière lui est dans l'objet, un point devant est praticable
                int far = 0;
                float farD = 0f;
                for (int q = 0; q < count; q++)
                {
                    Piece p = c[(start + from + q) % n];
                    float mx = (p.Ax + p.Bx) * 0.5f, my = (p.Ay + p.By) * 0.5f;
                    float d = -(mx - ax) * uy + (my - ay) * ux;
                    if (MathF.Abs(d) > MathF.Abs(farD)) { farD = d; far = q; }
                }
                if (MathF.Abs(farD) < 0.2f) continue;
                Piece pf = c[(start + from + far) % n];
                float fx = (pf.Ax + pf.Bx) * 0.5f, fy = (pf.Ay + pf.By) * 0.5f, sgn = farD > 0f ? 1f : -1f;
                // vers la corde : -sgn·(-uy, ux)
                float tx = uy * sgn, ty = -ux * sgn;
                if (Walkable(fx + tx * Probe, fy + ty * Probe, physical)) continue;   // creux praticable (alcôve)
                if (!Walkable(fx - tx * Probe, fy - ty * Probe, physical)) continue;  // rien de praticable devant

                PropShape.Clean(xs, ys);
                if (xs.Count < 4) continue;
                Footprint f = PropShape.Analyze(xs, ys);
                if (f.Area < 0.15f || f.Fill < 0.6f || f.HalfWid < 0.2f || f.HalfLen > 3.5f) continue;

                for (int q = 0; q < count; q++) c[(start + from + q) % n].Kind = Bump;
                outX.Add(xs); outY.Add(ys);
            }
        }
    }
}
