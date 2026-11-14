using System;
using System.Collections.Generic;

namespace AmongUs3D
{
    /// <summary>
    /// Mathématiques pures (aucun type Unity) : testables hors du jeu et rapides sous IL2CPP
    /// (les opérateurs Vector3 de l'interop passent par le runtime natif, on les évite).
    ///
    /// Repère 3D du mod : le plan XY du jeu 2D est le SOL, la verticale "haut" est -Z
    /// (vers l'ancienne caméra 2D). Lacet (yaw) en degrés : 0 = nord (+Y, "haut" en 2D),
    /// 90 = est (+X, "droite" en 2D), sens horaire vu du dessus.
    /// </summary>
    internal static class M
    {
        public const float Deg2Rad = (float)(Math.PI / 180.0);

        public static float Clamp(float v, float a, float b) => v < a ? a : (v > b ? b : v);
        public static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        public static float Lerp(float a, float b, float t) => a + (b - a) * t;
        public static float SmoothStep01(float t) { t = Clamp01(t); return t * t * (3f - 2f * t); }

        public static float WrapDegrees(float a)
        {
            a %= 360f;
            if (a < 0f) a += 360f;
            return a;
        }

        /// <summary>Plus petit écart signé de <paramref name="from"/> vers <paramref name="to"/>, dans [-180, 180].</summary>
        public static float DeltaAngle(float from, float to)
        {
            float d = WrapDegrees(to - from);
            return d > 180f ? d - 360f : d;
        }

        /// <summary>Avant (fx,fy) et droite (rx,ry) sur le sol pour un lacet donné.</summary>
        public static void YawBasis(float yawDeg, out float fx, out float fy, out float rx, out float ry)
        {
            float r = yawDeg * Deg2Rad;
            fx = MathF.Sin(r);
            fy = MathF.Cos(r);
            rx = fy;   // droite = avant tourné de 90° dans le sens horaire
            ry = -fx;
        }

        /// <summary>
        /// Transforme un vecteur rond (joystick/clavier normalisé) en vecteur "carré" :
        /// une diagonale clavier (0.707, 0.707) devient (1, 1), une demi-inclinaison analogique reste à 0.5.
        /// </summary>
        public static void SquareAxis(float x, float y, out float sx, out float sy)
        {
            float len = MathF.Sqrt(x * x + y * y);
            float m = MathF.Max(MathF.Abs(x), MathF.Abs(y));
            if (m < 1e-6f) { sx = 0f; sy = 0f; return; }
            float k = len / m;
            sx = x * k;
            sy = y * k;
        }

        /// <summary>
        /// Convertit la vitesse calculée par le jeu (en coordonnées 2D écran : x = droite, y = haut)
        /// en vitesse relative à la caméra 3D.
        ///  - Mode "strafe" : haut = avancer, bas = reculer, gauche/droite = pas de côté.
        ///  - Mode "tank"   : haut/bas = avancer/reculer à pleine vitesse, gauche/droite servent à tourner
        ///                    (aucun déplacement latéral).
        /// La norme n'est jamais augmentée au-delà de celle fournie par le jeu.
        /// </summary>
        public static void TransformVelocity(float vx, float vy, float yawDeg, bool tank, out float ox, out float oy)
        {
            YawBasis(yawDeg, out float fx, out float fy, out float rx, out float ry);
            if (!tank)
            {
                ox = vx * rx + vy * fx;
                oy = vx * ry + vy * fy;
                return;
            }
            SquareAxis(vx, vy, out _, out float forward);
            float len = MathF.Sqrt(vx * vx + vy * vy);
            forward = Clamp(forward, -len, len);
            ox = fx * forward;
            oy = fy * forward;
        }

        /// <summary>
        /// Luminosité (0..1) d'un point du décor à la distance d du joueur, imitant la vision 2D :
        /// pleine lumière dans le rayon de vision, pénombre (ambient) au-delà, noir au bord de la zone affichée.
        /// </summary>
        public static float VisionBrightness(float d, float radius, bool fullVision, float ambient, float fadeStart, float fadeEnd)
        {
            float far = 1f - SmoothStep01((d - fadeStart) / MathF.Max(0.001f, fadeEnd - fadeStart));
            if (fullVision) return far;
            float inner = radius * 0.8f;
            float outer = radius * 1.05f + 0.15f;
            float lit = 1f - SmoothStep01((d - inner) / MathF.Max(0.001f, outer - inner));
            return far * MathF.Max(lit, ambient);
        }

        /// <summary>Opacité (0..1) d'un personnage à la distance d : invisible hors du rayon de vision, comme en 2D.</summary>
        public static float VisionAlpha(float d, float radius, bool fullVision)
        {
            if (fullVision) return 1f;
            float inner = radius * 0.85f;
            float outer = radius * 1.0f + 0.1f;
            return 1f - SmoothStep01((d - inner) / MathF.Max(0.001f, outer - inner));
        }

        /// <summary>Intersection de segments [P0,P1] et [Q0,Q1]. t = position le long de P (0..1).</summary>
        public static bool SegmentIntersect(float p0x, float p0y, float p1x, float p1y,
                                            float q0x, float q0y, float q1x, float q1y, out float t)
        {
            t = 0f;
            float rx = p1x - p0x, ry = p1y - p0y;
            float sx = q1x - q0x, sy = q1y - q0y;
            float denom = rx * sy - ry * sx;
            if (MathF.Abs(denom) < 1e-9f) return false; // parallèles
            float qpx = q0x - p0x, qpy = q0y - p0y;
            float tt = (qpx * sy - qpy * sx) / denom;
            float u = (qpx * ry - qpy * rx) / denom;
            if (tt < 0f || tt > 1f || u < 0f || u > 1f) return false;
            t = tt;
            return true;
        }

        /// <summary>
        /// Équivalent de Quaternion.LookRotation(forward, up) de Unity (sans appel natif).
        /// L'axe local Z pointe vers forward, l'axe local Y vers up (orthogonalisé), X = cross(up, forward).
        /// </summary>
        public static void LookRotation(float fx, float fy, float fz, float ux, float uy, float uz,
                                        out float qx, out float qy, out float qz, out float qw)
        {
            float fl = MathF.Sqrt(fx * fx + fy * fy + fz * fz);
            if (fl < 1e-8f) { qx = qy = qz = 0f; qw = 1f; return; }
            fx /= fl; fy /= fl; fz /= fl;

            // right = cross(up, forward)
            float rx = uy * fz - uz * fy;
            float ry = uz * fx - ux * fz;
            float rz = ux * fy - uy * fx;
            float rl = MathF.Sqrt(rx * rx + ry * ry + rz * rz);
            if (rl < 1e-8f)
            {
                // up colinéaire à forward : on prend un up de secours
                float ax = MathF.Abs(fx) < 0.9f ? 1f : 0f, ay = ax > 0f ? 0f : 1f, az = 0f;
                rx = ay * fz - az * fy; ry = az * fx - ax * fz; rz = ax * fy - ay * fx;
                rl = MathF.Sqrt(rx * rx + ry * ry + rz * rz);
            }
            rx /= rl; ry /= rl; rz /= rl;

            // up' = cross(forward, right)
            float vx = fy * rz - fz * ry;
            float vy = fz * rx - fx * rz;
            float vz = fx * ry - fy * rx;

            // Matrice de rotation colonnes = (right, up', forward)
            float m00 = rx, m01 = vx, m02 = fx;
            float m10 = ry, m11 = vy, m12 = fy;
            float m20 = rz, m21 = vz, m22 = fz;
            float trace = m00 + m11 + m22;
            if (trace > 0f)
            {
                float s = MathF.Sqrt(trace + 1f) * 2f;
                qw = 0.25f * s; qx = (m21 - m12) / s; qy = (m02 - m20) / s; qz = (m10 - m01) / s;
            }
            else if (m00 > m11 && m00 > m22)
            {
                float s = MathF.Sqrt(1f + m00 - m11 - m22) * 2f;
                qw = (m21 - m12) / s; qx = 0.25f * s; qy = (m01 + m10) / s; qz = (m02 + m20) / s;
            }
            else if (m11 > m22)
            {
                float s = MathF.Sqrt(1f + m11 - m00 - m22) * 2f;
                qw = (m02 - m20) / s; qx = (m01 + m10) / s; qy = 0.25f * s; qz = (m12 + m21) / s;
            }
            else
            {
                float s = MathF.Sqrt(1f + m22 - m00 - m11) * 2f;
                qw = (m10 - m01) / s; qx = (m02 + m20) / s; qy = (m12 + m21) / s; qz = 0.25f * s;
            }
        }

        /// <summary>Rotation d'un vecteur par un quaternion (utilisé par les tests).</summary>
        public static void Rotate(float qx, float qy, float qz, float qw, float vx, float vy, float vz,
                                  out float ox, out float oy, out float oz)
        {
            // v' = v + 2w(q×v) + 2 q×(q×v)
            float cx = qy * vz - qz * vy, cy = qz * vx - qx * vz, cz = qx * vy - qy * vx;
            float ccx = qy * cz - qz * cy, ccy = qz * cx - qx * cz, ccz = qx * cy - qy * cx;
            ox = vx + 2f * (qw * cx + ccx);
            oy = vy + 2f * (qw * cy + ccy);
            oz = vz + 2f * (qw * cz + ccz);
        }

        /// <summary>Quaternion d'une rotation autour de Z (degrés), comme Quaternion.Euler(0, 0, a).</summary>
        public static void RotationZ(float degrees, out float qx, out float qy, out float qz, out float qw)
        {
            float h = degrees * Deg2Rad * 0.5f;
            qx = 0f; qy = 0f; qz = MathF.Sin(h); qw = MathF.Cos(h);
        }
    }

    /// <summary>Géométrie 2D pure utilisée pour transformer les collisions de la carte en décor 3D.</summary>
    internal static class Geo
    {
        /// <summary>Distance d'un point P au segment [A,B].</summary>
        public static float DistPointSeg(float px, float py, float ax, float ay, float bx, float by)
        {
            float dx = bx - ax, dy = by - ay;
            float len2 = dx * dx + dy * dy;
            float t = len2 > 1e-9f ? M.Clamp01(((px - ax) * dx + (py - ay) * dy) / len2) : 0f;
            float qx = ax + dx * t - px, qy = ay + dy * t - py;
            return MathF.Sqrt(qx * qx + qy * qy);
        }

        /// <summary>Ordonnée où la verticale x = px coupe le segment [A,B] (NaN si elle ne le coupe pas).</summary>
        public static float CrossY(float ax, float ay, float bx, float by, float px)
        {
            float dx = bx - ax;
            if ((ax - px) * (bx - px) > 0f || MathF.Abs(dx) < 1e-5f) return float.NaN;
            return ay + (px - ax) / dx * (by - ay);
        }

        /// <summary>Distance le long de la demi-droite O + t·D (D unitaire) jusqu'au segment [A,B] (NaN si rien).</summary>
        public static float RaySeg(float ox, float oy, float dx, float dy, float ax, float ay, float bx, float by)
        {
            float sx = bx - ax, sy = by - ay;
            float denom = dx * sy - dy * sx;
            if (MathF.Abs(denom) < 1e-9f) return float.NaN;
            float qx = ax - ox, qy = ay - oy;
            float t = (qx * sy - qy * sx) / denom;
            float u = (qx * dy - qy * dx) / denom;
            if (t < 0f || u < 0f || u > 1f) return float.NaN;
            return t;
        }

        /// <summary>Aire signée d'un polygone (positive = sens anti-horaire).</summary>
        public static float SignedArea(IList<float> x, IList<float> y)
        {
            float a = 0f;
            int n = x.Count;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                a += x[i] * y[j] - x[j] * y[i];
            }
            return a * 0.5f;
        }

        private static bool PointInTriangle(float px, float py, float ax, float ay, float bx, float by, float cx, float cy)
        {
            float d1 = (px - bx) * (ay - by) - (ax - bx) * (py - by);
            float d2 = (px - cx) * (by - cy) - (bx - cx) * (py - cy);
            float d3 = (px - ax) * (cy - ay) - (cx - ax) * (py - ay);
            bool neg = d1 < 0f || d2 < 0f || d3 < 0f;
            bool pos = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(neg && pos);
        }

        /// <summary>
        /// Découpe un polygone simple (convexe ou non) en triangles ("ear clipping").
        /// Ajoute des triplets d'indices dans <paramref name="tris"/> ; renvoie faux si rien n'a pu être produit.
        /// </summary>
        public static bool Triangulate(IList<float> xs, IList<float> ys, List<int> tris)
        {
            int n = xs.Count;
            if (n < 3) return false;
            int before = tris.Count;
            bool ccw = SignedArea(xs, ys) > 0f;
            var idx = new List<int>(n);
            for (int i = 0; i < n; i++) idx.Add(ccw ? i : n - 1 - i);

            int guard = n * n + 16;
            while (idx.Count > 3 && guard-- > 0)
            {
                int m = idx.Count;
                bool clipped = false;
                for (int i = 0; i < m && !clipped; i++)
                {
                    int a = idx[(i + m - 1) % m], b = idx[i], c = idx[(i + 1) % m];
                    float cross = (xs[b] - xs[a]) * (ys[c] - ys[a]) - (ys[b] - ys[a]) * (xs[c] - xs[a]);
                    if (cross <= 1e-9f) continue; // sommet rentrant ou aplati
                    bool ear = true;
                    for (int j = 0; j < m && ear; j++)
                    {
                        int p = idx[j];
                        if (p == a || p == b || p == c) continue;
                        if (PointInTriangle(xs[p], ys[p], xs[a], ys[a], xs[b], ys[b], xs[c], ys[c])) ear = false;
                    }
                    if (!ear) continue;
                    tris.Add(a); tris.Add(b); tris.Add(c);
                    idx.RemoveAt(i);
                    clipped = true;
                }
                if (clipped) continue;
                // Aucun "ear" : on retire le sommet le plus aplati pour continuer (polygone dégénéré).
                int flat = 0;
                float best = float.MaxValue;
                for (int i = 0; i < m; i++)
                {
                    int a = idx[(i + m - 1) % m], b = idx[i], c = idx[(i + 1) % m];
                    float cross = MathF.Abs((xs[b] - xs[a]) * (ys[c] - ys[a]) - (ys[b] - ys[a]) * (xs[c] - xs[a]));
                    if (cross < best) { best = cross; flat = i; }
                }
                idx.RemoveAt(flat);
            }
            if (idx.Count == 3)
            {
                tris.Add(idx[0]); tris.Add(idx[1]); tris.Add(idx[2]);
            }
            return tris.Count > before;
        }

        /// <summary>Médiane d'une liste (0 si vide). La liste est triée au passage.</summary>
        public static float Median(List<float> values, float fallback)
        {
            if (values.Count == 0) return fallback;
            values.Sort();
            return values[values.Count / 2];
        }
    }

    /// <summary>
    /// Le jeu lit les touches d'après leur place sur le clavier et les nomme comme sur un clavier américain :
    /// "Q" désigne la touche en haut à gauche, qui porte la lettre A sur un clavier français.
    /// </summary>
    internal static class KeyLayout
    {
        private const string Row1 = "QWERTYUIOP", Row2 = "ASDFGHJKL", Row3 = "ZXCVBNM";

        /// <summary>Code matériel (PC) de la touche qui porte ce nom sur un clavier américain ; 0 si ce n'est pas une lettre ou un signe.</summary>
        public static int UsScanCode(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            string n = name.Trim().ToUpperInvariant();
            if (n.Length == 1)
            {
                int i = Row1.IndexOf(n[0]);
                if (i >= 0) return 0x10 + i;
                i = Row2.IndexOf(n[0]);
                if (i >= 0) return 0x1E + i;
                i = Row3.IndexOf(n[0]);
                if (i >= 0) return 0x2C + i;
            }
            switch (n)
            {
                case "[": case "LEFT BRACKET": case "LEFTBRACKET": return 0x1A;
                case "]": case "RIGHT BRACKET": case "RIGHTBRACKET": return 0x1B;
                case ";": case "SEMICOLON": return 0x27;
                case "'": case "QUOTE": return 0x28;
                case "`": case "BACK QUOTE": case "BACKQUOTE": return 0x29;
                case "\\": case "BACKSLASH": return 0x2B;
                case ",": case "COMMA": return 0x33;
                case ".": case "PERIOD": return 0x34;
                case "/": case "SLASH": return 0x35;
                case "-": case "MINUS": return 0x0C;
                case "=": case "EQUALS": return 0x0D;
            }
            return 0;
        }
    }

    /// <summary>Un segment de mur sur le sol (pour la ligne de vue et la collision de la caméra).</summary>
    internal struct WallSeg
    {
        public float ax, ay, bx, by;
        public int owner; // index du maillage propriétaire (visible ou non selon les portes)
    }

    /// <summary>Géométrie pure des murs : extrusion verticale de polylignes 2D.</summary>
    internal static class WallGeometry
    {
        /// <summary>
        /// Ajoute une bande verticale (mur) pour chaque segment de la polyligne.
        /// Sommets : bas (z = 0) puis haut (z = -height), segments subdivisés tous les maxSegLen
        /// pour un éclairage par sommet régulier. shade = ombrage statique par sommet.
        /// </summary>
        public static void AddStrips(IList<float> px, IList<float> py, bool closed, float height, float maxSegLen,
                                     float bottomShade, int owner,
                                     List<float> vx, List<float> vy, List<float> vz, List<float> shade,
                                     List<int> tris, List<WallSeg> segs)
        {
            int n = px.Count;
            if (n < 2) return;
            int segCount = closed ? n : n - 1;
            // Lumière d'ambiance venant du nord-ouest (purement esthétique).
            const float lx = -0.4472136f, ly = 0.8944272f;
            for (int s = 0; s < segCount; s++)
            {
                float ax = px[s], ay = py[s];
                float bx = px[(s + 1) % n], by = py[(s + 1) % n];
                float dx = bx - ax, dy = by - ay;
                float len = MathF.Sqrt(dx * dx + dy * dy);
                if (len < 0.01f) continue;

                segs?.Add(new WallSeg { ax = ax, ay = ay, bx = bx, by = by, owner = owner });

                float nx = -dy / len, ny = dx / len;
                float face = 0.72f + 0.28f * MathF.Abs(nx * lx + ny * ly);
                int pieces = Math.Max(1, (int)MathF.Ceiling(len / MathF.Max(0.05f, maxSegLen)));
                int baseIndex = vx.Count;
                for (int k = 0; k <= pieces; k++)
                {
                    float t = (float)k / pieces;
                    float x = ax + dx * t, y = ay + dy * t;
                    vx.Add(x); vy.Add(y); vz.Add(0f); shade.Add(face * bottomShade);
                    vx.Add(x); vy.Add(y); vz.Add(-height); shade.Add(face);
                }
                for (int k = 0; k < pieces; k++)
                {
                    int b0 = baseIndex + k * 2, t0 = b0 + 1, b1 = b0 + 2, t1 = b0 + 3;
                    tris.Add(b0); tris.Add(t0); tris.Add(b1);
                    tris.Add(t0); tris.Add(t1); tris.Add(b1);
                }
            }
        }

        /// <summary>
        /// Comme <see cref="AddStrips"/>, mais le mur est découpé en trois bandes horizontales à arêtes nettes :
        /// une plinthe sombre, le panneau (dégradé du bas vers le haut) et un bandeau en haut.
        /// Purement esthétique : donne aux murs unis l'allure d'une cloison plutôt que d'un aplat.
        /// </summary>
        public static void AddPanelStrips(IList<float> px, IList<float> py, bool closed, float height, float maxSegLen,
                                          int owner,
                                          List<float> vx, List<float> vy, List<float> vz, List<float> shade,
                                          List<int> tris, List<WallSeg> segs)
        {
            int n = px.Count;
            if (n < 2) return;
            float plinth = MathF.Min(0.16f, height * 0.12f);
            float band = MathF.Min(0.12f, height * 0.08f);
            // (bas, haut, ombrage en bas, ombrage en haut) de chaque bande
            Span<float> z0 = stackalloc float[] { 0f, plinth, height - band };
            Span<float> z1 = stackalloc float[] { plinth, height - band, height };
            Span<float> s0 = stackalloc float[] { 0.40f, 0.66f, 0.56f };
            Span<float> s1 = stackalloc float[] { 0.44f, 1.00f, 0.56f };

            int segCount = closed ? n : n - 1;
            const float lx = -0.4472136f, ly = 0.8944272f;
            for (int s = 0; s < segCount; s++)
            {
                float ax = px[s], ay = py[s];
                float bx = px[(s + 1) % n], by = py[(s + 1) % n];
                float dx = bx - ax, dy = by - ay;
                float len = MathF.Sqrt(dx * dx + dy * dy);
                if (len < 0.01f) continue;

                segs?.Add(new WallSeg { ax = ax, ay = ay, bx = bx, by = by, owner = owner });

                float nx = -dy / len, ny = dx / len;
                float face = 0.72f + 0.28f * MathF.Abs(nx * lx + ny * ly);
                int pieces = Math.Max(1, (int)MathF.Ceiling(len / MathF.Max(0.05f, maxSegLen)));
                for (int b = 0; b < 3; b++)
                {
                    if (z1[b] - z0[b] < 1e-4f) continue;
                    int baseIndex = vx.Count;
                    for (int k = 0; k <= pieces; k++)
                    {
                        float t = (float)k / pieces;
                        float x = ax + dx * t, y = ay + dy * t;
                        vx.Add(x); vy.Add(y); vz.Add(-z0[b]); shade.Add(face * s0[b]);
                        vx.Add(x); vy.Add(y); vz.Add(-z1[b]); shade.Add(face * s1[b]);
                    }
                    for (int k = 0; k < pieces; k++)
                    {
                        int b0 = baseIndex + k * 2, t0 = b0 + 1, b1 = b0 + 2, t1 = b0 + 3;
                        tris.Add(b0); tris.Add(t0); tris.Add(b1);
                        tris.Add(t0); tris.Add(t1); tris.Add(b1);
                    }
                }
            }
        }

        /// <summary>Distance d'un point à une boîte englobante 2D (0 si à l'intérieur).</summary>
        public static float DistanceToBox(float x, float y, float minX, float minY, float maxX, float maxY)
        {
            float dx = x < minX ? minX - x : (x > maxX ? x - maxX : 0f);
            float dy = y < minY ? minY - y : (y > maxY ? y - maxY : 0f);
            return MathF.Sqrt(dx * dx + dy * dy);
        }
    }
}
