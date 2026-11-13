using System;

namespace AmongUs3D
{
    /// <summary>
    /// Transforme le dessin 2D d'un chapeau (vu de profil, comme dans le jeu) en objet 3D "gonflé" :
    /// vu de côté, on retrouve exactement le dessin ; de face ou de trois quarts, le chapeau a une vraie
    /// épaisseur, arrondie vers ses bords (un casque devient une coque bombée, une casquette une calotte).
    /// Le bord du bas, posé sur la tête, garde toute son épaisseur pour bien coiffer la tête 3D.
    /// Aucun type Unity : testable hors du jeu.
    ///
    /// Repère du modèle (comme <see cref="CrewShape"/>) : X = droite du personnage (épaisseur),
    /// Y = devant lui (horizontale du dessin), Z = vers le haut ; unité = taille du personnage.
    /// </summary>
    internal static class HatShape
    {
        /// <summary>Opacité minimale (0-255) d'un pixel du dessin pour qu'il fasse partie du chapeau.</summary>
        public const byte AlphaCut = 100;

        /// <summary>Demi-épaisseur maximale : un peu plus que la demi-largeur de la tête (0,30).</summary>
        public const float HalfThickness = 0.315f;

        /// <param name="rgba">pixels RGBA (ligne du bas en premier), couleur éventuellement prémultipliée par l'alpha</param>
        /// <param name="y0">Y du bord gauche de l'image ; <paramref name="cellY"/> = largeur signée d'un pixel en Y</param>
        /// <param name="z0">Z du bord bas de l'image ; <paramref name="cellZ"/> = hauteur d'un pixel en Z</param>
        /// <returns>null si le dessin est vide</returns>
        public static ShapeMesh Build(byte[] rgba, int w, int h, float y0, float cellY, float z0, float cellZ, float halfThickness)
        {
            if (rgba == null || w < 2 || h < 2 || rgba.Length < w * h * 4 || MathF.Abs(cellY) < 1e-7f || cellZ < 1e-7f) return null;
            int n = w * h;
            var opaque = new bool[n];
            int count = 0, minI = w, maxI = -1, minJ = h, maxJ = -1;
            for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                int k = j * w + i;
                if (rgba[k * 4 + 3] <= AlphaCut) continue;
                opaque[k] = true;
                count++;
                if (i < minI) minI = i;
                if (i > maxI) maxI = i;
                if (j < minJ) minJ = j;
                if (j > maxJ) maxJ = j;
            }
            if (count < 3) return null;

            float span = MathF.Min(maxI - minI + 1, maxJ - minJ + 1);
            float radius = M.Clamp(span * 0.45f, 2.5f, 16f);
            int pad = (int)MathF.Ceiling(radius) + 2;

            // 1) Masque "virtuel" : chaque colonne est prolongée vers le bas sous son pixel le plus haut,
            //    et au-delà du bas de l'image (pad lignes) : le bord inférieur n'amincit pas le chapeau.
            int hv = h + pad;
            var top = new int[w];
            for (int i = 0; i < w; i++)
            {
                top[i] = -1;
                for (int j = h - 1; j >= 0; j--)
                    if (opaque[j * w + i]) { top[i] = j; break; }
            }
            var dist = new float[w * hv];
            const float Inf = 1e9f;
            for (int jv = 0; jv < hv; jv++)
            for (int i = 0; i < w; i++)
            {
                int j = jv - pad;
                dist[jv * w + i] = top[i] >= 0 && j <= top[i] ? Inf : 0f;
            }

            // 2) Distance (approchée, chanfrein 1 / racine de 2) au bord du masque. Gauche, droite et haut de
            //    l'image comptent comme du vide ; sous la marge du bas, la matière continue.
            const float D1 = 1f, D2 = 1.41421356f;
            for (int jv = 0; jv < hv; jv++)
            for (int i = 0; i < w; i++)
            {
                int k = jv * w + i;
                if (dist[k] == 0f) continue;
                float d = dist[k];
                d = MathF.Min(d, Neighbor(dist, w, hv, i - 1, jv) + D1);
                d = MathF.Min(d, Neighbor(dist, w, hv, i, jv - 1) + D1);
                d = MathF.Min(d, Neighbor(dist, w, hv, i - 1, jv - 1) + D2);
                d = MathF.Min(d, Neighbor(dist, w, hv, i + 1, jv - 1) + D2);
                dist[k] = d;
            }
            for (int jv = hv - 1; jv >= 0; jv--)
            for (int i = w - 1; i >= 0; i--)
            {
                int k = jv * w + i;
                if (dist[k] == 0f) continue;
                float d = dist[k];
                d = MathF.Min(d, Neighbor(dist, w, hv, i + 1, jv) + D1);
                d = MathF.Min(d, Neighbor(dist, w, hv, i, jv + 1) + D1);
                d = MathF.Min(d, Neighbor(dist, w, hv, i + 1, jv + 1) + D2);
                d = MathF.Min(d, Neighbor(dist, w, hv, i - 1, jv + 1) + D2);
                dist[k] = d;
            }

            // 3) Épaisseur de chaque pixel du masque : profil en arc de cercle (bord fin, cœur épais).
            var thick = new float[w * hv];
            float tMin = halfThickness * 0.06f;
            for (int k = 0; k < thick.Length; k++)
            {
                if (dist[k] <= 0f) continue;
                float x = M.Clamp01((dist[k] - 0.5f) / radius);
                float t = halfThickness * MathF.Sqrt(MathF.Max(0f, 1f - (1f - x) * (1f - x)));
                thick[k] = MathF.Max(t, tMin);
            }

            // 3 bis) Couleurs pour le volume : le contour noir du dessin, étiré sur les bords arrondis, rendrait le
            //    chapeau tout sombre de face. Les pixels sombres proches du bord prennent la couleur de l'intérieur
            //    (le contour est redessiné par une coque noire, comme pour les personnages).
            var col = new float[n * 3];
            for (int k = 0; k < n; k++)
            {
                if (!opaque[k]) continue;
                Unpremultiply(rgba, k, out col[k * 3], out col[k * 3 + 1], out col[k * 3 + 2]);
            }
            var edge = new int[n]; // 1 = pixel du bord, 2 = juste derrière, ... (0 = loin du bord ou vide)
            for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                int k = j * w + i;
                if (!opaque[k]) continue;
                bool border = false;
                for (int dj = -1; dj <= 1 && !border; dj++)
                for (int di = -1; di <= 1 && !border; di++)
                {
                    int ni = i + di, nj = j + dj;
                    if (ni < 0 || ni >= w || nj < 0 || nj >= h || !opaque[nj * w + ni]) border = true;
                }
                if (border) edge[k] = 1;
            }
            for (int ring = 2; ring <= 3; ring++)
            for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                int k = j * w + i;
                if (!opaque[k] || edge[k] != 0) continue;
                for (int dj = -1; dj <= 1; dj++)
                for (int di = -1; di <= 1; di++)
                {
                    int ni = i + di, nj = j + dj;
                    if (ni >= 0 && ni < w && nj >= 0 && nj < h && edge[nj * w + ni] == ring - 1) edge[k] = ring;
                }
            }
            var fill = (float[])col.Clone();
            for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                int k = j * w + i;
                if (!opaque[k] || edge[k] == 0 || Luma(col, k) >= 0.33f) continue;
                float r = 0f, g = 0f, b = 0f, wsum = 0f;
                for (int dj = -3; dj <= 3; dj++)
                for (int di = -3; di <= 3; di++)
                {
                    int ni = i + di, nj = j + dj;
                    if (ni < 0 || ni >= w || nj < 0 || nj >= h) continue;
                    int q = nj * w + ni;
                    if (!opaque[q] || Luma(col, q) < 0.33f) continue;
                    if (edge[q] != 0 && edge[q] <= edge[k]) continue; // seulement plus à l'intérieur
                    float wq = 1f / (1f + di * di + dj * dj);
                    r += col[q * 3] * wq; g += col[q * 3 + 1] * wq; b += col[q * 3 + 2] * wq; wsum += wq;
                }
                if (wsum <= 0f) continue;
                fill[k * 3] = r / wsum; fill[k * 3 + 1] = g / wsum; fill[k * 3 + 2] = b / wsum;
            }

            // 4) Coins de la grille : épaisseur moyenne des 4 pixels voisins (le vide compte pour 0),
            //    couleur moyenne des pixels dessinés voisins.
            int cw = w + 1, ch = h + 1;
            var cornerT = new float[cw * ch];
            var cornerOn = new bool[cw * ch];
            var cr = new float[cw * ch];
            var cg = new float[cw * ch];
            var cb = new float[cw * ch];
            for (int j = 0; j <= h; j++)
            for (int i = 0; i <= w; i++)
            {
                float sumT = 0f, r = 0f, g = 0f, b = 0f;
                int drawn = 0;
                for (int dj = -1; dj <= 0; dj++)
                for (int di = -1; di <= 0; di++)
                {
                    int pi = i + di, pj = j + dj;
                    if (pi < 0 || pi >= w || pj + pad < 0 || pj >= h) continue;
                    sumT += thick[(pj + pad) * w + pi];
                    if (pj < 0) continue;
                    int k = pj * w + pi;
                    if (!opaque[k]) continue;
                    drawn++;
                    r += fill[k * 3]; g += fill[k * 3 + 1]; b += fill[k * 3 + 2];
                }
                int c = j * cw + i;
                cornerT[c] = sumT * 0.25f;
                if (drawn == 0) continue;
                cornerOn[c] = true;
                cr[c] = r / drawn; cg[c] = g / drawn; cb[c] = b / drawn;
            }

            // 5) Deux coques (côté droit +X et côté gauche -X), avec des normales tirées de la pente de l'épaisseur.
            var m = new ShapeMesh();
            var plus = new int[cw * ch];
            var minus = new int[cw * ch];
            for (int j = 0; j <= h; j++)
            for (int i = 0; i <= w; i++)
            {
                int c = j * cw + i;
                plus[c] = minus[c] = -1;
                if (!cornerOn[c]) continue;
                float t = cornerT[c];
                float dtdi = (CornerT(cornerT, cw, ch, i + 1, j, t) - CornerT(cornerT, cw, ch, i - 1, j, t)) * 0.5f;
                float dtdj = (CornerT(cornerT, cw, ch, i, j + 1, t) - CornerT(cornerT, cw, ch, i, j - 1, t)) * 0.5f;
                float gy = dtdi / cellY, gz = dtdj / cellZ;
                float y = y0 + i * cellY, z = z0 + j * cellZ;
                plus[c] = Vertex(m, t, y, z, 1f, -gy, -gz, cr[c], cg[c], cb[c]);
                minus[c] = Vertex(m, -t, y, z, -1f, -gy, -gz, cr[c], cg[c], cb[c]);
            }
            for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                if (!opaque[j * w + i]) continue;
                int a = j * cw + i, b = a + 1, c = a + cw, d = c + 1;
                CrewShape.AddTriangle(m, plus[a], plus[b], plus[d]);
                CrewShape.AddTriangle(m, plus[a], plus[d], plus[c]);
                CrewShape.AddTriangle(m, minus[a], minus[b], minus[d]);
                CrewShape.AddTriangle(m, minus[a], minus[d], minus[c]);
            }

            // 6) Tranche : là où le dessin s'arrête, une bande sombre relie les deux coques (le contour noir du dessin).
            float signY = MathF.Sign(cellY);
            for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                int k = j * w + i;
                if (!opaque[k]) continue;
                float pr = fill[k * 3], pg = fill[k * 3 + 1], pb = fill[k * 3 + 2];
                for (int dir = 0; dir < 4; dir++)
                {
                    int di = dir == 0 ? 1 : (dir == 1 ? -1 : 0);
                    int dj = dir == 2 ? 1 : (dir == 3 ? -1 : 0);
                    int ni = i + di, nj = j + dj;
                    if (ni >= 0 && ni < w && nj >= 0 && nj < h && opaque[nj * w + ni]) continue;
                    // les deux coins de l'arête
                    int pi0, pj0, pi1, pj1;
                    if (di == 1) { pi0 = i + 1; pj0 = j; pi1 = i + 1; pj1 = j + 1; }
                    else if (di == -1) { pi0 = i; pj0 = j; pi1 = i; pj1 = j + 1; }
                    else if (dj == 1) { pi0 = i; pj0 = j + 1; pi1 = i + 1; pj1 = j + 1; }
                    else { pi0 = i; pj0 = j; pi1 = i + 1; pj1 = j; }
                    int c0 = pj0 * cw + pi0, c1 = pj1 * cw + pi1;
                    float t0 = cornerT[c0], t1 = cornerT[c1];
                    if (t0 < 1e-5f && t1 < 1e-5f) continue;
                    // tranche un peu plus sombre que les faces (dessous posé sur la tête : plus sombre encore)
                    float dark = dj == -1 ? 0.45f : 0.7f;
                    float ny = di * signY, nz = dj;
                    float y0e = y0 + pi0 * cellY, z0e = z0 + pj0 * cellZ;
                    float y1e = y0 + pi1 * cellY, z1e = z0 + pj1 * cellZ;
                    float r = pr * dark, g = pg * dark, b = pb * dark;
                    int a = m.AddColored(t0, y0e, z0e, 0f, ny, nz, B(r), B(g), B(b));
                    int bb = m.AddColored(-t0, y0e, z0e, 0f, ny, nz, B(r), B(g), B(b));
                    int c = m.AddColored(t1, y1e, z1e, 0f, ny, nz, B(r), B(g), B(b));
                    int d = m.AddColored(-t1, y1e, z1e, 0f, ny, nz, B(r), B(g), B(b));
                    CrewShape.AddTriangle(m, a, c, d);
                    CrewShape.AddTriangle(m, a, d, bb);
                }
            }
            return m.Tris.Count > 0 ? m : null;
        }

        private static float Neighbor(float[] dist, int w, int hv, int i, int jv)
        {
            if (i < 0 || i >= w || jv >= hv) return 0f; // gauche, droite, haut : vide
            if (jv < 0) return 1e9f;                      // sous la marge : matière
            return dist[jv * w + i];
        }

        private static float CornerT(float[] t, int cw, int ch, int i, int j, float fallback)
        {
            if (i < 0 || i >= cw || j < 0 || j >= ch) return fallback;
            return t[j * cw + i];
        }

        private static void Unpremultiply(byte[] rgba, int k, out float r, out float g, out float b)
        {
            float a = rgba[k * 4 + 3];
            float s = a > 0f && a < 255f ? 255f / a : 1f;
            r = MathF.Min(255f, rgba[k * 4] * s);
            g = MathF.Min(255f, rgba[k * 4 + 1] * s);
            b = MathF.Min(255f, rgba[k * 4 + 2] * s);
        }

        private static float Luma(float[] col, int k) => (0.299f * col[k * 3] + 0.587f * col[k * 3 + 1] + 0.114f * col[k * 3 + 2]) / 255f;

        private static int Vertex(ShapeMesh m, float x, float y, float z, float nx, float ny, float nz, float r, float g, float b)
        {
            // léger éclairage par-dessus le dessin (qui a déjà ses propres ombres) pour faire sentir le volume
            float l = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            float lit = l > 1e-6f ? (nx * -0.30f + ny * 0.35f + nz * 0.887f) / l : 0f;
            float k = 0.72f + 0.28f * M.Clamp01(0.5f + 0.5f * lit);
            return m.AddColored(x, y, z, nx, ny, nz, B(r * k), B(g * k), B(b * k));
        }

        private static byte B(float v) => (byte)(v <= 0f ? 0 : (v >= 255f ? 255 : (int)(v + 0.5f)));
    }
}
