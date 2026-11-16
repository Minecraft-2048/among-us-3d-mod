using System;
using System.Collections.Generic;

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
            => BuildCore(rgba, w, h, y0, cellY, z0, cellZ, halfThickness, false, null, null);

        /// <summary>Animal : gonflé comme un chapeau posé, les parties fines (oreilles, queue, antennes) restant rondes.</summary>
        public static ShapeMesh BuildPet(byte[] rgba, int w, int h, float y0, float cellY, float z0, float cellZ, float halfThickness)
            => BuildCore(rgba, w, h, y0, cellY, z0, cellZ, halfThickness, true, null, null);

        /// <param name="roundThin">les parties étroites du dessin (pointes, antennes) prennent une section ronde au lieu d'être des plaques</param>
        /// <param name="emit">pixels à fabriquer (null = tous) ; les autres pixels opaques comptent comme de la matière pour l'épaisseur</param>
        /// <param name="extraCap">demi-épaisseur toujours permise à chaque pixel malgré <paramref name="roundThin"/> (null = aucune)</param>
        private static ShapeMesh BuildCore(byte[] rgba, int w, int h, float y0, float cellY, float z0, float cellZ, float halfThickness,
                                           bool roundThin, bool[] emit, float[] extraCap)
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
            bool Emit(int q) => opaque[q] && (emit == null || emit[q]);

            float span = MathF.Min(maxI - minI + 1, maxJ - minJ + 1);
            float px48 = MathF.Max(1f, MathF.Max(w, h) / 48f); // réglages pensés pour une image de 48 pixels
            float radius = M.Clamp(span * 0.45f, 2.5f * px48, 16f * px48);
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
            float cellW = 0.5f * (MathF.Abs(cellY) + cellZ);
            for (int k = 0; k < thick.Length; k++)
            {
                if (dist[k] <= 0f) continue;
                float x = M.Clamp01((dist[k] - 0.5f) / radius);
                float t = halfThickness * MathF.Sqrt(MathF.Max(0f, 1f - (1f - x) * (1f - x)));
                if (roundThin)
                {
                    // section à peu près ronde : pas plus épais que la largeur locale du dessin
                    float cap = (dist[k] + 0.5f) * cellW * 0.8f;
                    if (extraCap != null)
                    {
                        int jj = Math.Min(h - 1, Math.Max(0, k / w - pad));
                        cap = MathF.Max(cap, extraCap[jj * w + k % w]);
                    }
                    t = MathF.Min(t, cap);
                }
                thick[k] = MathF.Max(t, tMin);
            }

            // 3 bis) Couleurs pour le volume : le contour noir du dessin, étiré sur les bords arrondis, rendrait le
            //    chapeau tout sombre de face. Les pixels sombres proches du bord prennent la couleur de l'intérieur
            //    (le contour est redessiné par une coque noire, comme pour les personnages).
            float[] fill = FillColors(rgba, w, h, opaque);

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
                    if (!Emit(k)) continue;
                    drawn++;
                    r += fill[k * 3]; g += fill[k * 3 + 1]; b += fill[k * 3 + 2];
                }
                int c = j * cw + i;
                cornerT[c] = sumT * 0.25f;
                if (drawn == 0) continue;
                cornerOn[c] = true;
                cr[c] = r / drawn; cg[c] = g / drawn; cb[c] = b / drawn;
            }
            return Shells(fill, opaque, Emit, w, h, y0, cellY, z0, cellZ, cw, ch, cornerT, cornerOn, cr, cg, cb);
        }

        /// <summary>
        /// Couleurs pour le volume : le contour noir du dessin, étiré sur les bords arrondis, rendrait le chapeau
        /// tout sombre de face. Les pixels sombres proches du bord prennent la couleur de l'intérieur (le contour est
        /// redessiné par une coque noire, comme pour les personnages). 3 composantes par pixel, de 0 à 255.
        /// </summary>
        private static float[] FillColors(byte[] rgba, int w, int h, bool[] opaque)
        {
            int n = w * h;
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
            return fill;
        }

        private static ShapeMesh Shells(float[] fill, bool[] opaque, Func<int, bool> emitted, int w, int h, float y0, float cellY, float z0, float cellZ,
                                        int cw, int ch, float[] cornerT, bool[] cornerOn, float[] cr, float[] cg, float[] cb)
        {
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
                if (!emitted(j * w + i)) continue;
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
                if (!emitted(k)) continue;
                float pr = fill[k * 3], pg = fill[k * 3 + 1], pb = fill[k * 3 + 2];
                for (int dir = 0; dir < 4; dir++)
                {
                    int di = dir == 0 ? 1 : (dir == 1 ? -1 : 0);
                    int dj = dir == 2 ? 1 : (dir == 3 ? -1 : 0);
                    int ni = i + di, nj = j + dj;
                    if (ni >= 0 && ni < w && nj >= 0 && nj < h && emitted(nj * w + ni)) continue;
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

        // ================================================================== chapeaux : coque autour de la tête

        /// <summary>Rapport largeur / profondeur de la tête du modèle (la coque d'un chapeau a la même forme).</summary>
        private const float HeadKx = CrewShape.BodyRadiusX / CrewShape.BodyRadiusY;
        /// <summary>Écart entre la tête et la coque, et épaisseur de la coque.</summary>
        private const float ShellGap = 0.018f, ShellThickness = 0.026f;
        /// <summary>Demi-profondeur maximale d'une coque (au-delà, le dessin dépasse : il est gonflé à part).</summary>
        private const float ShellMaxRadius = 0.46f;

        /// <summary>Demi-profondeur (Y) de la tête du modèle à la hauteur z (voir <see cref="CrewShape.Body"/>) ; 0 au-dessus du sommet.</summary>
        public static float HeadHalfDepth(float z)
        {
            float u = (z - 0.60f) / 0.40f;
            if (u >= 1f) return 0f;
            if (u <= 0f) return CrewShape.BodyRadiusY;
            return CrewShape.BodyRadiusY * MathF.Pow(1f - MathF.Pow(u, 2f / 0.62f), 0.31f);
        }

        /// <summary>
        /// Chapeau : si le dessin entoure le haut de la tête (casquette, bonnet, couronne, capuche, perruque, casque…),
        /// la partie qui entoure la tête devient une coque fine qui épouse le crâne, et seul ce qui dépasse devant
        /// ou derrière (visière de casquette, queue de cheval…) est gonflé. Sinon (objet posé sur la tête), tout est
        /// gonflé. Dans les deux cas, les parties étroites (pointes, antennes, étoiles) ont une section ronde.
        /// </summary>
        public static ShapeMesh BuildHat(byte[] rgba, int w, int h, float y0, float cellY, float z0, float cellZ, float halfThickness,
                                         string productId = null)
        {
            if (rgba == null || w < 2 || h < 2 || rgba.Length < w * h * 4 || MathF.Abs(cellY) < 1e-7f || cellZ < 1e-7f) return null;
            if (KeepPuffed(productId)) return Build(rgba, w, h, y0, cellY, z0, cellZ, halfThickness);
            int n = w * h;
            var opaque = new bool[n];
            int count = 0;
            for (int k = 0; k < n; k++)
                if (rgba[k * 4 + 3] > AlphaCut) { opaque[k] = true; count++; }
            if (count < 3) return null;
            if (!WrapsHead(opaque, w, h, y0, cellY, z0, cellZ))
                return BuildCore(rgba, w, h, y0, cellY, z0, cellZ, halfThickness, true, null, null);

            ShellPlan plan = PlanShell(opaque, w, h, y0, cellY, z0, cellZ);
            float[] radius = plan.Radius;
            var rest = new bool[n];
            int restCount = 0;
            for (int k = 0; k < n; k++)
                if (opaque[k] && !plan.Shell[k]) { rest[k] = true; restCount++; }

            float[] fill = FillColors(rgba, w, h, opaque);
            var m = new ShapeMesh();
            AddShell(m, plan, fill, w, h, y0, cellY, z0, cellZ);

            if (restCount > 0)
            {
                // Ce qui dépasse à hauteur de la tête (visière de casquette, rabat de capuche) garde la largeur de la tête ;
                // au-dessus, les parties étroites restent rondes.
                var cap = new float[n];
                for (int j = 0; j < h; j++)
                {
                    float z = z0 + (j + 0.5f) * cellZ;
                    if (z > 1.0f) continue;
                    float r = MathF.Max(radius[j], HeadHalfDepth(z) + ShellGap);
                    for (int i = 0; i < w; i++) cap[j * w + i] = HeadKx * r * 0.9f;
                }
                ShapeMesh p = BuildCore(rgba, w, h, y0, cellY, z0, cellZ, halfThickness, true, rest, cap);
                if (p != null) Append(m, p);
            }
            return m.Tris.Count > 0 ? m : null;
        }

        /// <summary>
        /// Objets du magasin traités à part. Cache-oreilles, casques audio et casques-micros sont dessinés de profil avec leurs deux
        /// côtés visibles (l'un devant, l'autre derrière la tête) : enroulés, ils feraient un anneau autour de la tête ;
        /// gonflés comme avant, ils posent un rond de chaque côté.
        /// </summary>
        private static readonly string[] PuffedIds = { "earmuff", "headset", "headphone" };

        public static bool KeepPuffed(string productId)
        {
            if (string.IsNullOrEmpty(productId)) return false;
            string id = productId.ToLowerInvariant();
            foreach (string p in PuffedIds)
                if (id.Contains(p)) return true;
            return false;
        }

        /// <summary>Pour la mise au point (atelier des chapeaux) : la forme choisie pour ce dessin, en une ligne.</summary>
        internal static string Explain(byte[] rgba, int w, int h, float y0, float cellY, float z0, float cellZ, out bool[] shellMask)
        {
            shellMask = null;
            var opaque = new bool[w * h];
            for (int k = 0; k < w * h; k++) opaque[k] = rgba[k * 4 + 3] > AlphaCut;
            if (!WrapsHead(opaque, w, h, y0, cellY, z0, cellZ)) return "objet posé : gonflé";
            ShellPlan plan = PlanShell(opaque, w, h, y0, cellY, z0, cellZ);
            float[] radius = plan.Radius;
            bool[] shell = plan.Shell;
            shellMask = shell;
            int shellCount = 0, restCount = 0;
            for (int k = 0; k < w * h; k++) { if (shell[k]) shellCount++; else if (opaque[k]) restCount++; }
            var sb = new System.Text.StringBuilder("coque : ");
            sb.Append(shellCount).Append(" pixels, ").Append(restCount).Append(" gonflés ; rayons");
            for (int j = 0; j < h; j += 8)
                if (radius[j] > 0f) sb.Append(' ').Append((z0 + (j + 0.5f) * cellZ).ToString("0.00")).Append(':').Append(radius[j].ToString("0.00"))
                                      .Append('@').Append(plan.Center[j].ToString("0.00"));
            int lids = 0;
            foreach (int l in plan.LidRow) if (l >= 0) lids++;
            sb.Append(" ; couvercle sur ").Append(lids).Append(" colonnes");
            return sb.ToString();
        }

        /// <summary>Le dessin entoure-t-il le haut de la tête (sur la plus grande partie de sa profondeur, sous le sommet) ?</summary>
        private static bool WrapsHead(bool[] opaque, int w, int h, float y0, float cellY, float z0, float cellZ)
        {
            for (int j = 0; j < h; j++)
            {
                float z = z0 + (j + 0.5f) * cellZ;
                if (z < 0.62f || z > 0.93f) continue;
                float hd = HeadHalfDepth(z);
                int inside = 0, covered = 0;
                for (int i = 0; i < w; i++)
                {
                    float y = y0 + (i + 0.5f) * cellY;
                    if (MathF.Abs(y) > hd) continue;
                    inside++;
                    if (opaque[j * w + i]) covered++;
                }
                if (inside >= 4 && covered >= 0.55f * inside) return true;
            }
            return false;
        }

        /// <summary>Hauteur maximale des pointes posées sur l'anneau d'une couronne (au-delà : objet à part, gonflé).</summary>
        private const float SpikeHeight = 0.15f;
        /// <summary>À hauteur de la tête, la coque ne s'écarte de la tête de plus que cela que sur une bande fine (le bord d'un chapeau).</summary>
        private const float HeadMargin = 0.12f, BrimHeight = 0.07f;

        /// <summary>Disposition de la coque d'un chapeau.</summary>
        private sealed class ShellPlan
        {
            public float[] Radius, Center; // par ligne : demi-profondeur de l'anneau et son centre (Y) ; rayon 0 = pas de coque
            public bool[] Shell;           // par pixel : posé sur la coque
            public int[] LidRow;           // par colonne : ligne du haut de la coque à fermer d'un couvercle, ou -1
        }

        /// <summary>
        /// Disposition de la coque. À hauteur de la tête, chaque ligne du dessin devient un anneau autour de la tête :
        /// la partie symétrique devant / derrière l'axe (une visière de casquette, qui ne dépasse que devant, reste à part),
        /// jamais moins que la tête, et pas beaucoup plus sauf sur une bande fine (le bord d'un chapeau).
        /// Au-dessus de la tête, les lignes pleines continuent la coque (bonnet, haut-de-forme, même penché) : chaque anneau
        /// suit le milieu de sa ligne. Juste au-dessus, les petites pointes (couronne, oreilles) se posent sur l'anneau ;
        /// ce qui monte plus haut (antennes, étoiles, plumes) reste à part et sera gonflé. Le haut de la coque est fermé
        /// colonne par colonne en suivant le contour du dessin.
        /// </summary>
        private static ShellPlan PlanShell(bool[] opaque, int w, int h, float y0, float cellY, float z0, float cellZ)
        {
            int n = w * h;
            float half = MathF.Abs(cellY) * 0.5f;
            var plan = new ShellPlan { Radius = new float[h], Center = new float[h], Shell = new bool[n], LidRow = new int[w] };
            for (int i = 0; i < w; i++) plan.LidRow[i] = -1;
            var dense = new bool[h];
            var filledRow = new int[h];
            var front = new float[h];
            var back = new float[h];
            var ymin = new float[h];
            var ymax = new float[h];
            var hug = new bool[h];
            var hd = new float[h];
            for (int j = 0; j < h; j++)
            {
                int first = -1, last = -1, filled = 0, onHead = 0, headCells = 0;
                hd[j] = HeadHalfDepth(z0 + (j + 0.5f) * cellZ);
                ymin[j] = float.MaxValue; ymax[j] = float.MinValue;
                for (int i = 0; i < w; i++)
                {
                    float y = y0 + (i + 0.5f) * cellY;
                    bool inHead = MathF.Abs(y) <= hd[j];
                    if (inHead) headCells++;
                    if (!opaque[j * w + i]) continue;
                    if (inHead) onHead++;
                    if (y >= 0f) front[j] = MathF.Max(front[j], y + half);
                    else back[j] = MathF.Max(back[j], half - y);
                    ymin[j] = MathF.Min(ymin[j], y - half);
                    ymax[j] = MathF.Max(ymax[j], y + half);
                    if (first < 0) first = i;
                    last = i;
                    filled++;
                }
                filledRow[j] = filled;
                dense[j] = filled > 0 && filled >= 0.75f * (last - first + 1);
                // Le dessin colle à la tête s'il atteint son bord (devant ou derrière) et en couvre une bonne partie ;
                // une couronne plus étroite que la tête reste un anneau posé dessus, en partie caché dans la tête.
                hug[j] = hd[j] > 0f && headCells > 0 && onHead >= 0.45f * headCells
                         && (front[j] >= hd[j] - 0.02f || back[j] >= hd[j] - 0.02f);
            }

            // rayon voulu à hauteur de la tête
            var want = new float[h];
            for (int j = 0; j < h; j++)
            {
                if (filledRow[j] == 0 || hd[j] <= 0f) continue;
                float r = MathF.Min(front[j], back[j]);
                if (hug[j]) r = MathF.Max(r, hd[j] + ShellGap);
                want[j] = r;
            }
            int brimRows = Math.Max(1, (int)MathF.Round(BrimHeight / cellZ));
            for (int j = 0; j < h;)
            {
                if (want[j] <= hd[j] + HeadMargin) { j++; continue; }
                int e = j;
                while (e < h && want[e] > hd[e] + HeadMargin) e++;
                if (e - j > brimRows) // pas un bord de chapeau : plusieurs objets côte à côte, une coiffure large… on rabote
                    for (int q = j; q < e; q++) want[q] = hd[q] + HeadMargin;
                j = e;
            }

            // 1) la pile de lignes de la coque
            int topRow = -1;
            float headRing = 0f;
            for (int j = 0; j < h; j++)
            {
                if (filledRow[j] == 0)
                {
                    if (hd[j] <= 0f && topRow >= 0) break;
                    continue;
                }
                float r, c;
                if (hd[j] > 0f)
                {
                    r = want[j];
                    c = 0f;
                    if (r <= 0f) continue;
                    headRing = MathF.Max(headRing, r);
                }
                else
                {
                    if (!dense[j] || topRow < 0) break;
                    // un chapeau penché glisse peu à peu : le centre suit le milieu de la ligne, sans sauter ;
                    // seule la partie symétrique autour de lui fait l'anneau (un bord qui dépasse d'un côté reste à part)
                    float jump = hd[topRow] > 0f ? 0.06f : 1.5f * cellZ + 2f * half;
                    c = M.Clamp(0.5f * (ymin[j] + ymax[j]), plan.Center[topRow] - jump, plan.Center[topRow] + jump);
                    r = MathF.Min(MathF.Min(ymax[j] - c, c - ymin[j]), MathF.Max(headRing, CrewShape.BodyRadiusY) * 1.15f);
                    if (r <= 2f * half) break;
                }
                plan.Radius[j] = MathF.Min(r, ShellMaxRadius);
                plan.Center[j] = c;
                topRow = j;
                for (int i = 0; i < w; i++)
                {
                    int k = j * w + i;
                    if (opaque[k] && MathF.Abs(y0 + (i + 0.5f) * cellY - c) <= plan.Radius[j] + half) plan.Shell[k] = true;
                }
            }
            if (topRow < 0) return plan;

            // 2) couvercles : en haut de chaque colonne de la pile, au-dessus du sommet de la tête
            for (int i = 0; i < w; i++)
                for (int j = topRow; j >= 0; j--)
                {
                    if (!plan.Shell[j * w + i]) continue;
                    if (z0 + (j + 1) * cellZ >= 0.97f) plan.LidRow[i] = j;
                    break;
                }

            // 3) petites pointes posées sur l'anneau du haut de la coque
            if (topRow + 1 < h)
            {
                var seen = new bool[n];
                var stack = new Stack<int>();
                var comp = new List<int>();
                var widths = new Dictionary<int, int>();
                for (int start = (topRow + 1) * w; start < n; start++)
                {
                    if (!opaque[start] || seen[start]) continue;
                    comp.Clear();
                    widths.Clear();
                    seen[start] = true;
                    stack.Push(start);
                    int minRow = h, maxRow = -1;
                    while (stack.Count > 0)
                    {
                        int k = stack.Pop();
                        comp.Add(k);
                        int i = k % w, j = k / w;
                        if (j < minRow) minRow = j;
                        if (j > maxRow) maxRow = j;
                        widths[j] = widths.TryGetValue(j, out int wj) ? wj + 1 : 1;
                        for (int dir = 0; dir < 4; dir++)
                        {
                            int ni = i + (dir == 0 ? 1 : dir == 1 ? -1 : 0), nj = j + (dir == 2 ? 1 : dir == 3 ? -1 : 0);
                            if (ni < 0 || ni >= w || nj <= topRow || nj >= h) continue;
                            int q = nj * w + ni;
                            if (!opaque[q] || seen[q]) continue;
                            seen[q] = true;
                            stack.Push(q);
                        }
                    }
                    if (minRow != topRow + 1 || (maxRow - minRow + 1) * cellZ > SpikeHeight) continue;
                    int widest = minRow, widestCount = 0;
                    foreach (var kv in widths)
                        if (kv.Value > widestCount || (kv.Value == widestCount && kv.Key < widest)) { widest = kv.Key; widestCount = kv.Value; }
                    if ((widest - minRow) * 2 > maxRow - minRow) continue; // plus large en haut qu'en bas : pas une pointe
                    foreach (int k in comp)
                    {
                        plan.Shell[k] = true;
                        int row = k / w;
                        plan.Radius[row] = MathF.Max(plan.Radius[row], plan.Radius[topRow]);
                        plan.Center[row] = plan.Center[topRow];
                    }
                }
            }
            return plan;
        }

        /// <summary>
        /// Coque : chaque pixel du dessin est posé sur l'ellipse de sa ligne, des deux côtés de la tête, avec une face
        /// intérieure plus sombre, une tranche sur les bords et des couvercles en haut.
        /// </summary>
        private static void AddShell(ShapeMesh m, ShellPlan plan, float[] fill, int w, int h, float y0, float cellY, float z0, float cellZ)
        {
            bool[] shell = plan.Shell;
            int cw = w + 1, ch = h + 1;
            // rayon, centre et couleur à chaque coin : moyenne des pixels de coque qui le touchent
            var rc = new float[cw * ch];
            var cc = new float[cw * ch];
            var on = new bool[cw * ch];
            var cr = new float[cw * ch];
            var cg = new float[cw * ch];
            var cb = new float[cw * ch];
            for (int j = 0; j <= h; j++)
            for (int i = 0; i <= w; i++)
            {
                float rs = 0f, cs = 0f, r = 0f, g = 0f, b = 0f;
                int k0 = 0;
                for (int dj = -1; dj <= 0; dj++)
                for (int di = -1; di <= 0; di++)
                {
                    int pi = i + di, pj = j + dj;
                    if (pi < 0 || pi >= w || pj < 0 || pj >= h) continue;
                    int k = pj * w + pi;
                    if (!shell[k]) continue;
                    k0++;
                    rs += plan.Radius[pj];
                    cs += plan.Center[pj];
                    r += fill[k * 3]; g += fill[k * 3 + 1]; b += fill[k * 3 + 2];
                }
                if (k0 == 0) continue;
                int c = j * cw + i;
                on[c] = true;
                rc[c] = rs / k0;
                cc[c] = cs / k0;
                cr[c] = r / k0; cg[c] = g / k0; cb[c] = b / k0;
            }

            var outP = new int[cw * ch];
            var outM = new int[cw * ch];
            var inP = new int[cw * ch];
            var inM = new int[cw * ch];
            var xo = new float[cw * ch];
            var xi = new float[cw * ch];
            const float In = 0.5f; // intérieur dans l'ombre
            for (int j = 0; j <= h; j++)
            for (int i = 0; i <= w; i++)
            {
                int c = j * cw + i;
                outP[c] = outM[c] = inP[c] = inM[c] = -1;
                if (!on[c]) continue;
                float y = y0 + i * cellY, z = z0 + j * cellZ, r = rc[c], dy = y - cc[c];
                float ri = MathF.Max(0f, r - ShellThickness);
                xo[c] = HeadKx * MathF.Sqrt(MathF.Max(0f, r * r - dy * dy));
                xi[c] = HeadKx * MathF.Sqrt(MathF.Max(0f, ri * ri - dy * dy));
                float up = j < h && on[c + cw] ? rc[c + cw] : r, down = j > 0 && on[c - cw] ? rc[c - cw] : r;
                float nzo = -(up - down) / (2f * cellZ) * r;
                outP[c] = Vertex(m, xo[c], y, z, xo[c] / (HeadKx * HeadKx), dy, nzo, cr[c], cg[c], cb[c]);
                outM[c] = Vertex(m, -xo[c], y, z, -xo[c] / (HeadKx * HeadKx), dy, nzo, cr[c], cg[c], cb[c]);
                inP[c] = m.AddColored(xi[c], y, z, -xi[c] / (HeadKx * HeadKx), -dy, -nzo, B(cr[c] * In), B(cg[c] * In), B(cb[c] * In));
                inM[c] = m.AddColored(-xi[c], y, z, xi[c] / (HeadKx * HeadKx), -dy, -nzo, B(cr[c] * In), B(cg[c] * In), B(cb[c] * In));
            }

            float signY = MathF.Sign(cellY);
            for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                int k = j * w + i;
                if (!shell[k]) continue;
                int a = j * cw + i, b = a + 1, c = a + cw, d = c + 1;
                Quad(m, outP[a], outP[b], outP[d], outP[c]);
                Quad(m, outM[a], outM[b], outM[d], outM[c]);
                Quad(m, inP[a], inP[b], inP[d], inP[c]);
                Quad(m, inM[a], inM[b], inM[d], inM[c]);

                // tranche : là où la coque s'arrête, entre la face extérieure et la face intérieure
                float pr = fill[k * 3], pg = fill[k * 3 + 1], pb = fill[k * 3 + 2];
                for (int dir = 0; dir < 4; dir++)
                {
                    int di = dir == 0 ? 1 : (dir == 1 ? -1 : 0);
                    int dj = dir == 2 ? 1 : (dir == 3 ? -1 : 0);
                    int ni = i + di, nj = j + dj;
                    if (ni >= 0 && ni < w && nj >= 0 && nj < h && shell[nj * w + ni]) continue;
                    int c0, c1;
                    if (di == 1) { c0 = b; c1 = d; }
                    else if (di == -1) { c0 = a; c1 = c; }
                    else if (dj == 1) { c0 = c; c1 = d; }
                    else { c0 = a; c1 = b; }
                    float ny = di * signY, nz = dj;
                    float dark = dj == -1 ? 0.45f : 0.62f;
                    float ya = y0 + (c0 % cw) * cellY, za = z0 + (c0 / cw) * cellZ;
                    float yb = y0 + (c1 % cw) * cellY, zb = z0 + (c1 / cw) * cellZ;
                    for (int side = -1; side <= 1; side += 2)
                    {
                        float x0o = side * xo[c0], x0i = side * xi[c0], x1o = side * xo[c1], x1i = side * xi[c1];
                        if (MathF.Abs(x0o - x0i) < 1e-5f && MathF.Abs(x1o - x1i) < 1e-5f) continue;
                        int v0 = m.AddColored(x0o, ya, za, 0f, ny, nz, B(pr * dark), B(pg * dark), B(pb * dark));
                        int v1 = m.AddColored(x1o, yb, zb, 0f, ny, nz, B(pr * dark), B(pg * dark), B(pb * dark));
                        int v2 = m.AddColored(x1i, yb, zb, 0f, ny, nz, B(pr * dark), B(pg * dark), B(pb * dark));
                        int v3 = m.AddColored(x0i, ya, za, 0f, ny, nz, B(pr * dark), B(pg * dark), B(pb * dark));
                        Quad(m, v0, v1, v2, v3);
                    }
                }
            }

            // couvercles : une bande horizontale en haut de chaque colonne, et une marche entre deux colonnes voisines
            for (int i = 0; i < w; i++)
            {
                int j = plan.LidRow[i];
                if (j < 0) continue;
                int k = j * w + i;
                float pr = fill[k * 3] * 0.95f, pg = fill[k * 3 + 1] * 0.95f, pb = fill[k * 3 + 2] * 0.95f;
                float zt = z0 + (j + 1) * cellZ;
                int ca = (j + 1) * cw + i, cbb = ca + 1;
                float ya = y0 + i * cellY, yb = y0 + (i + 1) * cellY;
                int l0 = Vertex(m, xo[ca], ya, zt, 0f, 0f, 1f, pr, pg, pb);
                int l1 = Vertex(m, -xo[ca], ya, zt, 0f, 0f, 1f, pr, pg, pb);
                int l2 = Vertex(m, -xo[cbb], yb, zt, 0f, 0f, 1f, pr, pg, pb);
                int l3 = Vertex(m, xo[cbb], yb, zt, 0f, 0f, 1f, pr, pg, pb);
                Quad(m, l0, l1, l2, l3);

                if (i + 1 >= w) continue;
                int jn = plan.LidRow[i + 1];
                if (jn < 0 || jn == j) continue;
                // marche entre la colonne i et la colonne i + 1, sur leur frontière
                int lo = Math.Min(j, jn), hi = Math.Max(j, jn);
                int cLo = (lo + 1) * cw + i + 1, cHi = (hi + 1) * cw + i + 1;
                float zl = z0 + (lo + 1) * cellZ, zh = z0 + (hi + 1) * cellZ, yw = y0 + (i + 1) * cellY;
                float ny = (j < jn ? -1f : 1f) * signY; // tournée vers la colonne la plus basse
                int kHi = hi * w + (j < jn ? i + 1 : i);
                float sr = fill[kHi * 3] * 0.7f, sg = fill[kHi * 3 + 1] * 0.7f, sb = fill[kHi * 3 + 2] * 0.7f;
                int s0 = Vertex(m, xo[cLo], yw, zl, 0f, ny, 0f, sr, sg, sb);
                int s1 = Vertex(m, -xo[cLo], yw, zl, 0f, ny, 0f, sr, sg, sb);
                int s2 = Vertex(m, -xo[cHi], yw, zh, 0f, ny, 0f, sr, sg, sb);
                int s3 = Vertex(m, xo[cHi], yw, zh, 0f, ny, 0f, sr, sg, sb);
                Quad(m, s0, s1, s2, s3);
            }
        }

        private static void Quad(ShapeMesh m, int a, int b, int c, int d)
        {
            if (a < 0 || b < 0 || c < 0 || d < 0) return;
            CrewShape.AddTriangle(m, a, b, c);
            CrewShape.AddTriangle(m, a, c, d);
        }

        /// <summary>Ajoute un maillage à un autre (mêmes sommets, mêmes triangles).</summary>
        private static void Append(ShapeMesh m, ShapeMesh p)
        {
            int off = m.VertexCount;
            for (int i = 0; i < p.VertexCount; i++)
            {
                uint c = p.HasColors ? p.Rgba[i] : 0xFFFFFFFFu;
                m.AddColored(p.X[i], p.Y[i], p.Z[i], p.Nx[i], p.Ny[i], p.Nz[i], (byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8));
            }
            foreach (int t in p.Tris) m.Tris.Add(t + off);
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

        /// <summary>
        /// Couleur du dessin au point (y, z) du profil, dans le même repère que <see cref="Build"/> :
        /// faux si le dessin y est transparent.
        /// </summary>
        public static bool SampleProfile(byte[] rgba, int w, int h, float y0, float cellY, float z0, float cellZ, float y, float z,
                                         out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            if (rgba == null || MathF.Abs(cellY) < 1e-9f || MathF.Abs(cellZ) < 1e-9f) return false;
            int i = (int)MathF.Floor((y - y0) / cellY), j = (int)MathF.Floor((z - z0) / cellZ);
            if (i < 0 || j < 0 || i >= w || j >= h) return false;
            int k = j * w + i;
            if (rgba[k * 4 + 3] < AlphaCut) return false;
            Unpremultiply(rgba, k, out float fr, out float fg, out float fb);
            r = B(fr); g = B(fg); b = B(fb);
            return true;
        }

        /// <summary>
        /// Comme <see cref="SampleProfile"/>, mais en rentrant un peu vers le centre (cy, cz) de la forme pour éviter
        /// le contour noir du dessin quand le point est au bord de la silhouette.
        /// </summary>
        public static bool SampleInside(byte[] rgba, int w, int h, float y0, float cellY, float z0, float cellZ, float y, float z,
                                        float cy, float cz, out byte r, out byte g, out byte b)
        {
            bool any = false;
            byte dr = 0, dg = 0, db = 0;
            for (int step = 0; step < 4; step++)
            {
                float k = 1f - 0.12f * step;
                if (!SampleProfile(rgba, w, h, y0, cellY, z0, cellZ, cy + (y - cy) * k, cz + (z - cz) * k, out r, out g, out b)) continue;
                if (r + g + b > 90) return true;
                if (!any) { any = true; dr = r; dg = g; db = b; }
            }
            r = dr; g = dg; b = db;
            return any;
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
