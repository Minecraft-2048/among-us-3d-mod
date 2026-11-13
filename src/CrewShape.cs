using System;
using System.Collections.Generic;

namespace AmongUs3D
{
    /// <summary>Zones de couleur d'un personnage 3D.</summary>
    internal static class CrewZone
    {
        public const byte Body = 0, Visor = 1, Backpack = 2, Leg = 3, Bone = 4, Flesh = 5;
    }

    /// <summary>
    /// Maillage décrit par de simples listes (aucun type Unity : testable hors du jeu).
    /// Repère du modèle : X = droite du personnage, Y = devant lui, Z = vers le haut ; unité = taille du personnage.
    /// </summary>
    internal sealed class ShapeMesh
    {
        public readonly List<float> X = new List<float>(), Y = new List<float>(), Z = new List<float>();
        public readonly List<float> Nx = new List<float>(), Ny = new List<float>(), Nz = new List<float>();
        public readonly List<byte> Zone = new List<byte>();
        public readonly List<int> Tris = new List<int>();
        /// <summary>Couleur propre de chaque sommet (0xRRGGBBAA), seulement pour les formes tirées d'un dessin (chapeaux).</summary>
        public readonly List<uint> Rgba = new List<uint>();

        public int VertexCount => X.Count;
        public bool HasColors => Rgba.Count == X.Count && X.Count > 0;

        public int AddColored(float x, float y, float z, float nx, float ny, float nz, byte r, byte g, byte b)
        {
            Rgba.Add(((uint)r << 24) | ((uint)g << 16) | ((uint)b << 8) | 0xFFu);
            return Add(x, y, z, nx, ny, nz, CrewZone.Body);
        }

        public int Add(float x, float y, float z, float nx, float ny, float nz, byte zone)
        {
            float l = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (l < 1e-6f) { nx = 0f; ny = 0f; nz = 1f; l = 1f; }
            X.Add(x); Y.Add(y); Z.Add(z);
            Nx.Add(nx / l); Ny.Add(ny / l); Nz.Add(nz / l);
            Zone.Add(zone);
            return X.Count - 1;
        }
    }

    /// <summary>
    /// Formes 3D des personnages (le "haricot" d'Among Us), fabriquées par le calcul : aucun fichier de modèle.
    /// Tout est fait de "super-ellipsoïdes" : des ellipsoïdes dont on règle l'arrondi (1 = œuf, 0,3 = boîte arrondie).
    /// </summary>
    internal static class CrewShape
    {
        // Proportions (taille du personnage = 1).
        public const float HipHeight = 0.24f;     // hauteur de l'articulation des jambes
        public const float LegSpacing = 0.145f;   // demi-écart entre les jambes
        public const float BodyRadiusX = 0.30f, BodyRadiusY = 0.245f;
        public const float OutlineWidth = 0.028f; // épaisseur du contour noir

        private static float Pow(float w, float e) => MathF.Sign(w) * MathF.Pow(MathF.Abs(w), e);

        /// <summary>Ajoute une super-ellipsoïde centrée en (cx, cy, cz), de demi-axes (rx, ry, rz).</summary>
        /// <param name="e1">arrondi vertical (profil vu de côté)</param>
        /// <param name="e2">arrondi horizontal (forme vue de dessus)</param>
        public static void AddSuperEllipsoid(ShapeMesh m, float cx, float cy, float cz, float rx, float ry, float rz,
                                             float e1, float e2, int rings, int segs, byte zone)
        {
            int first = m.VertexCount;
            for (int i = 0; i <= rings; i++)
            {
                float theta = -MathF.PI * 0.5f + MathF.PI * i / rings;
                float ct = MathF.Cos(theta), st = MathF.Sin(theta);
                if (i == 0) { ct = 0f; st = -1f; }
                if (i == rings) { ct = 0f; st = 1f; }
                for (int j = 0; j <= segs; j++)
                {
                    float phi = -MathF.PI + 2f * MathF.PI * j / segs;
                    float cp = MathF.Cos(phi), sp = MathF.Sin(phi);
                    if (j == segs) { cp = -1f; sp = 0f; } // referme exactement la couture
                    if (j == 0) { cp = -1f; sp = 0f; }
                    float x = rx * Pow(ct, e1) * Pow(cp, e2);
                    float y = ry * Pow(ct, e1) * Pow(sp, e2);
                    float z = rz * Pow(st, e1);
                    float nx = Pow(ct, 2f - e1) * Pow(cp, 2f - e2) / rx;
                    float ny = Pow(ct, 2f - e1) * Pow(sp, 2f - e2) / ry;
                    float nz = Pow(st, 2f - e1) / rz;
                    m.Add(cx + x, cy + y, cz + z, nx, ny, nz, zone);
                }
            }
            int w = segs + 1;
            for (int i = 0; i < rings; i++)
            for (int j = 0; j < segs; j++)
            {
                int a = first + i * w + j, b = a + 1, c = a + w, d = c + 1;
                AddTriangle(m, a, c, b);
                AddTriangle(m, b, c, d);
            }
        }

        /// <summary>Ajoute un triangle orienté vers l'extérieur (ignoré s'il est aplati, comme aux pôles).</summary>
        public static void AddTriangle(ShapeMesh m, int a, int b, int c)
        {
            float ux = m.X[b] - m.X[a], uy = m.Y[b] - m.Y[a], uz = m.Z[b] - m.Z[a];
            float vx = m.X[c] - m.X[a], vy = m.Y[c] - m.Y[a], vz = m.Z[c] - m.Z[a];
            float cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
            if (cx * cx + cy * cy + cz * cz < 1e-14f) return;
            float nx = m.Nx[a] + m.Nx[b] + m.Nx[c], ny = m.Ny[a] + m.Ny[b] + m.Ny[c], nz = m.Nz[a] + m.Nz[b] + m.Nz[c];
            // Dans le repère du modèle (main droite), un triangle vu de l'extérieur tourne dans le sens direct.
            if (cx * nx + cy * ny + cz * nz < 0f) { int t = b; b = c; c = t; }
            m.Tris.Add(a); m.Tris.Add(b); m.Tris.Add(c);
        }

        /// <summary>Corps debout : tronc, sac à dos et visière (sans les jambes, animées à part).</summary>
        public static ShapeMesh Body()
        {
            var m = new ShapeMesh();
            // tronc : du haut des jambes (0,20) au sommet de la tête (1,00)
            AddSuperEllipsoid(m, 0f, 0f, 0.60f, BodyRadiusX, BodyRadiusY, 0.40f, 0.62f, 0.86f, 14, 22, CrewZone.Body);
            // sac à dos, dans le dos
            AddSuperEllipsoid(m, 0f, -0.285f, 0.50f, 0.185f, 0.105f, 0.20f, 0.5f, 0.5f, 8, 14, CrewZone.Backpack);
            // visière, bombée vers l'avant
            AddSuperEllipsoid(m, 0f, 0.185f, 0.715f, 0.205f, 0.115f, 0.118f, 0.75f, 0.8f, 9, 16, CrewZone.Visor);
            return m;
        }

        /// <summary>Une jambe, décrite autour de son articulation (la hanche est à l'origine, le pied en Z négatif).</summary>
        public static ShapeMesh Leg()
        {
            var m = new ShapeMesh();
            AddSuperEllipsoid(m, 0f, 0f, -0.105f, 0.118f, 0.155f, 0.135f, 0.5f, 0.85f, 8, 14, CrewZone.Leg);
            return m;
        }

        /// <summary>Cadavre : la moitié basse du personnage, avec l'os qui dépasse (comme dans le jeu).</summary>
        public static ShapeMesh Corpse()
        {
            var m = new ShapeMesh();
            // demi-tronc
            AddSuperEllipsoid(m, 0f, 0f, 0.33f, BodyRadiusX, BodyRadiusY, 0.20f, 0.45f, 0.86f, 9, 22, CrewZone.Body);
            // chair à la coupe
            AddSuperEllipsoid(m, 0f, 0f, 0.515f, BodyRadiusX * 0.86f, BodyRadiusY * 0.86f, 0.035f, 0.9f, 0.86f, 5, 18, CrewZone.Flesh);
            // os : une tige et deux boules
            AddSuperEllipsoid(m, 0f, 0f, 0.60f, 0.045f, 0.045f, 0.13f, 0.5f, 1f, 6, 10, CrewZone.Bone);
            AddSuperEllipsoid(m, -0.05f, 0f, 0.74f, 0.06f, 0.055f, 0.055f, 1f, 1f, 6, 10, CrewZone.Bone);
            AddSuperEllipsoid(m, 0.05f, 0f, 0.74f, 0.06f, 0.055f, 0.055f, 1f, 1f, 6, 10, CrewZone.Bone);
            // sac à dos (moitié basse)
            AddSuperEllipsoid(m, 0f, -0.285f, 0.36f, 0.185f, 0.105f, 0.13f, 0.5f, 0.5f, 6, 14, CrewZone.Backpack);
            // jambes
            AddSuperEllipsoid(m, -LegSpacing, 0f, HipHeight - 0.105f, 0.118f, 0.155f, 0.135f, 0.5f, 0.85f, 8, 14, CrewZone.Leg);
            AddSuperEllipsoid(m, LegSpacing, 0f, HipHeight - 0.105f, 0.118f, 0.155f, 0.135f, 0.5f, 0.85f, 8, 14, CrewZone.Leg);
            return m;
        }

        /// <summary>Surface décrite par deux paramètres : u de 0 (haut) à 1 (bas), phi autour de l'axe vertical.</summary>
        public delegate void Surface(float u, float phi, out float x, out float y, out float z);

        /// <summary>
        /// Ajoute une surface fermée décrite par <paramref name="f"/> (pôles en u = 0 et u = 1).
        /// Les normales sont calculées par différences finies et tournées vers l'extérieur.
        /// </summary>
        public static void AddParametric(ShapeMesh m, int rings, int segs, Surface f, byte zone,
                                         float tipNx, float tipNy, float tipNz)
        {
            int first = m.VertexCount;
            const float du = 1e-3f, dp = 1e-3f;
            for (int i = 0; i <= rings; i++)
            {
                float u = (float)i / rings;
                f(u, 0f, out float ax, out float ay, out float az);
                f(u, MathF.PI, out float bx, out float by, out float bz);
                float cx = (ax + bx) * 0.5f, cy = (ay + by) * 0.5f, cz = (az + bz) * 0.5f;
                for (int j = 0; j <= segs; j++)
                {
                    float phi = j == segs ? -MathF.PI : -MathF.PI + 2f * MathF.PI * j / segs;
                    f(u, phi, out float x, out float y, out float z);
                    float nx, ny, nz;
                    if (i == 0) { nx = 0f; ny = 0f; nz = 1f; }
                    else if (i == rings) { nx = tipNx; ny = tipNy; nz = tipNz; }
                    else
                    {
                        f(u - du, phi, out float x0, out float y0, out float z0);
                        f(u + du, phi, out float x1, out float y1, out float z1);
                        f(u, phi - dp, out float xa, out float ya, out float za);
                        f(u, phi + dp, out float xb, out float yb, out float zb);
                        float tux = x1 - x0, tuy = y1 - y0, tuz = z1 - z0;
                        float tpx = xb - xa, tpy = yb - ya, tpz = zb - za;
                        nx = tuy * tpz - tuz * tpy;
                        ny = tuz * tpx - tux * tpz;
                        nz = tux * tpy - tuy * tpx;
                        if (nx * nx + ny * ny + nz * nz < 1e-18f) { nx = x - cx; ny = y - cy; nz = z - cz; }
                        if (nx * (x - cx) + ny * (y - cy) + nz * (z - cz) < 0f) { nx = -nx; ny = -ny; nz = -nz; }
                    }
                    m.Add(x, y, z, nx, ny, nz, zone);
                }
            }
            int w = segs + 1;
            for (int i = 0; i < rings; i++)
            for (int j = 0; j < segs; j++)
            {
                int a = first + i * w + j, b = a + 1, c = a + w, d = c + 1;
                AddTriangle(m, a, c, b);
                AddTriangle(m, b, c, d);
            }
        }

        /// <summary>
        /// Fantôme : le haut du personnage (tête, visière, sac à dos) qui se termine en une queue
        /// effilée, rejetée vers l'arrière. Pas de jambes : il flotte.
        /// </summary>
        public static ShapeMesh Ghost()
        {
            var m = new ShapeMesh();
            AddParametric(m, 26, 22, GhostSurface, CrewZone.Body, 0f, -0.5f, -0.866f);
            AddSuperEllipsoid(m, 0f, -0.27f, 0.58f, 0.175f, 0.10f, 0.165f, 0.5f, 0.5f, 8, 14, CrewZone.Backpack);
            AddSuperEllipsoid(m, 0f, 0.185f, 0.715f, 0.205f, 0.115f, 0.118f, 0.75f, 0.8f, 9, 16, CrewZone.Visor);
            return m;
        }

        /// <summary>Position du tip de la queue du fantôme (pour les tests).</summary>
        public const float GhostTipY = -0.22f, GhostTipZ = 0.03f;

        private static void GhostSurface(float u, float phi, out float x, out float y, out float z)
        {
            const float z0 = 0.60f, rz = 0.40f, e1 = 0.62f, e2 = 0.86f;
            u = M.Clamp01(u);
            float cp = MathF.Cos(phi), sp = MathF.Sin(phi);
            if (u <= 0.5f)
            {
                // haut : la même calotte que le personnage vivant
                float lat = (0.5f - u) / 0.5f * MathF.PI * 0.5f;
                float cl = MathF.Cos(lat), sl = MathF.Sin(lat);
                x = BodyRadiusX * Pow(cl, e1) * Pow(cp, e2);
                y = BodyRadiusY * Pow(cl, e1) * Pow(sp, e2);
                z = z0 + rz * Pow(sl, e1);
                return;
            }
            // bas : la queue s'affine et part vers l'arrière
            float s = (u - 0.5f) / 0.5f;
            float wdt = MathF.Pow(MathF.Max(0f, MathF.Cos(s * MathF.PI * 0.5f)), 0.75f);
            float yc = GhostTipY * MathF.Pow(s, 1.5f);
            z = z0 - (z0 - GhostTipZ) * MathF.Pow(s, 0.85f);
            x = BodyRadiusX * wdt * (1f - 0.25f * s) * Pow(cp, e2);
            y = yc + BodyRadiusY * wdt * Pow(sp, e2);
        }

        /// <summary>
        /// Éclairage "dessin animé" d'un sommet : 0 = dans l'ombre, 1 = en pleine lumière.
        /// La lumière vient d'en haut, un peu de devant et de la gauche, comme sur les dessins du jeu.
        /// </summary>
        public static float Light(float nx, float ny, float nz, float z, byte zone)
        {
            const float lx = -0.30f, ly = 0.35f, lz = 0.887f;
            float t = nx * lx + ny * ly + nz * lz;
            float s = M.SmoothStep01((t + 0.12f) / 0.30f);
            if (zone == CrewZone.Leg) return s * 0.45f;
            // le bas du corps reste dans l'ombre (comme la bande sombre des dessins)
            float low = M.SmoothStep01((z - 0.24f) / 0.20f);
            return s * (0.35f + 0.65f * low);
        }

        /// <summary>Reflet clair de la visière (0..1).</summary>
        public static float VisorShine(float nx, float ny, float nz)
        {
            const float lx = -0.50f, ly = 0.55f, lz = 0.669f;
            float t = nx * lx + ny * ly + nz * lz;
            return M.SmoothStep01((t - 0.80f) / 0.10f);
        }
    }
}
