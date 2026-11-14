using System;
using System.Collections.Generic;

namespace AmongUs3D
{
    /// <summary>Sorte de meuble, reconnue d'après le nom et la forme de sa collision.</summary>
    internal enum PropKind { Generic, RoundTable, Drum, Table, Console, Chair, Bed, Crate, Button }

    /// <summary>Empreinte au sol d'un meuble : rectangle orienté le plus serré et rondeur.</summary>
    internal struct Footprint
    {
        public float Cx, Cy;           // centre du rectangle orienté
        public float Ux, Uy;           // grand axe (unitaire) ; le petit axe est (-Uy, Ux)
        public float HalfLen, HalfWid; // demi-longueur (grand axe) >= demi-largeur
        public float Area, Perimeter;
        public float Roundness;        // 4πA/P² : 1 = disque, 0,785 = carré
        public float Fill;             // aire / aire du rectangle : 1 = rectangle, 0,785 = ellipse

        public bool IsRound => Roundness > 0.84f && HalfWid > 0.7f * HalfLen;
        public bool IsBox => Fill > 0.9f;
    }

    /// <summary>Ce que l'on sait de l'entourage d'un meuble.</summary>
    internal struct PropHints
    {
        public bool Emergency;       // le bouton d'urgence est posé dessus
        public bool TaskConsole;     // une console de tâche est dessus ou tout contre
        public bool Indoor;          // carte d'intérieur : une grande forme ronde est une table
        public bool Blue;            // tables et bancs bleus (The Skeld)
        public float WallX, WallY;   // direction (unitaire) du mur le plus proche
        public float LookX, LookY;   // direction de ce qu'on regarde assis (console proche, sinon le mur)
        public float Height;         // hauteur des meubles non reconnus (réglage HauteurDesObstacles)
        public uint BaseColor;       // couleur des meubles non reconnus (0xRRGGBB)
    }

    /// <summary>
    /// Meubles "modelés" par le calcul (aucun fichier de modèle), à partir de l'empreinte de leur collision :
    /// tables rondes à pied central entourées de bancs, bouton d'urgence en dôme, consoles inclinées, caisses à cadre,
    /// lits, chaises, et pour le reste des formes aux arêtes arrondies posées sur une plinthe.
    /// Repère : X, Y = sol de la carte, Z = vers le haut. Testable hors du jeu (aucun type Unity).
    /// </summary>
    internal static class PropShape
    {
        // Couleurs (0xRRGGBB), dans les tons de The Skeld.
        private const uint Metal = 0x5C6470, MetalDark = 0x3A3F47, TableRim = 0xC3CBD5, BenchSeat = 0x7F8B9C, BenchBase = 0x3C424B,
                           BlueRim = 0x2C5E8E, BlueSeat = 0x3B78AE, BlueBase = 0x1E3D5C,
                           ButtonBase = 0x30343A, ButtonRing = 0xC9CED6, ButtonRed = 0xE3262B, ConsoleBody = 0x5E6674,
                           CrateBody = 0x8A8166, CrateFrame = 0x4B483F, Seat = 0x45526A, BedFrame = 0xAEB8C4,
                           Mattress = 0xE2E9F0, Blanket = 0x3F7FC4, Pillow = 0xF5F7FA;

        // ------------------------------------------------------------------ empreinte

        /// <summary>Retire les points en double (et le point de fermeture) et met le polygone dans le sens direct.</summary>
        public static void Clean(List<float> xs, List<float> ys)
        {
            for (int i = xs.Count - 1; i >= 0 && xs.Count > 1; i--)
            {
                int p = (i + xs.Count - 1) % xs.Count;
                float dx = xs[i] - xs[p], dy = ys[i] - ys[p];
                if (dx * dx + dy * dy < 1e-4f) { xs.RemoveAt(i); ys.RemoveAt(i); }
            }
            if (xs.Count >= 3 && Geo.SignedArea(xs, ys) < 0f) { xs.Reverse(); ys.Reverse(); }
        }

        public static Footprint Analyze(IList<float> xs, IList<float> ys)
        {
            var f = new Footprint();
            int n = xs.Count;
            if (n < 3) return f;
            f.Area = MathF.Abs(Geo.SignedArea(xs, ys));
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                float dx = xs[j] - xs[i], dy = ys[j] - ys[i];
                f.Perimeter += MathF.Sqrt(dx * dx + dy * dy);
            }
            // Le rectangle le plus serré a un côté aligné sur un côté du polygone (de son enveloppe convexe).
            float best = float.MaxValue;
            for (int i = 0; i <= n; i++)
            {
                float ux = 1f, uy = 0f;
                if (i < n)
                {
                    int j = (i + 1) % n;
                    float dx = xs[j] - xs[i], dy = ys[j] - ys[i], l = MathF.Sqrt(dx * dx + dy * dy);
                    if (l < 1e-4f) continue;
                    ux = dx / l; uy = dy / l;
                }
                float s0 = float.MaxValue, s1 = float.MinValue, t0 = float.MaxValue, t1 = float.MinValue;
                for (int k = 0; k < n; k++)
                {
                    float s = xs[k] * ux + ys[k] * uy, t = -xs[k] * uy + ys[k] * ux;
                    if (s < s0) s0 = s;
                    if (s > s1) s1 = s;
                    if (t < t0) t0 = t;
                    if (t > t1) t1 = t;
                }
                float a = (s1 - s0) * (t1 - t0);
                if (a >= best - 1e-6f) continue;
                best = a;
                float sm = (s0 + s1) * 0.5f, tm = (t0 + t1) * 0.5f;
                f.Cx = sm * ux - tm * uy;
                f.Cy = sm * uy + tm * ux;
                if (s1 - s0 >= t1 - t0) { f.Ux = ux; f.Uy = uy; f.HalfLen = (s1 - s0) * 0.5f; f.HalfWid = (t1 - t0) * 0.5f; }
                else { f.Ux = -uy; f.Uy = ux; f.HalfLen = (t1 - t0) * 0.5f; f.HalfWid = (s1 - s0) * 0.5f; }
            }
            if (f.Ux < -1e-6f || (MathF.Abs(f.Ux) <= 1e-6f && f.Uy < 0f)) { f.Ux = -f.Ux; f.Uy = -f.Uy; }
            f.Roundness = f.Perimeter > 1e-6f ? 4f * MathF.PI * f.Area / (f.Perimeter * f.Perimeter) : 0f;
            f.Fill = best > 1e-9f && best < float.MaxValue ? f.Area / best : 0f;
            return f;
        }

        /// <summary>Point dans le polygone (règle pair-impair).</summary>
        public static bool Contains(IList<float> xs, IList<float> ys, float x, float y)
        {
            bool inside = false;
            for (int i = 0, j = xs.Count - 1; i < xs.Count; j = i++)
                if ((ys[i] > y) != (ys[j] > y) && x < (xs[j] - xs[i]) * (y - ys[i]) / (ys[j] - ys[i]) + xs[i]) inside = !inside;
            return inside;
        }

        /// <summary>Point dans le rectangle orienté, agrandi de <paramref name="margin"/>.</summary>
        public static bool Near(in Footprint f, float x, float y, float margin)
        {
            float dx = x - f.Cx, dy = y - f.Cy;
            float u = dx * f.Ux + dy * f.Uy, v = -dx * f.Uy + dy * f.Ux;
            return MathF.Abs(u) <= f.HalfLen + margin && MathF.Abs(v) <= f.HalfWid + margin;
        }

        // ------------------------------------------------------------------ reconnaissance

        private static bool Has(string s, params string[] words)
        {
            foreach (string w in words)
                if (s.IndexOf(w, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        public static PropKind Classify(string name, in Footprint f, in PropHints h)
        {
            string n = (name ?? "").ToLowerInvariant();
            float len = 2f * f.HalfLen, wid = 2f * f.HalfWid;
            if (Has(n, "emergency")) return PropKind.Button;
            if (Has(n, "chair", "seat", "stool", "sofa", "couch", "weapon")) return PropKind.Chair;
            if (Has(n, "bed")) return PropKind.Bed;
            if (Has(n, "crate", "box", "cargo", "barrel", "drum")) return f.IsRound ? PropKind.Drum : PropKind.Crate;
            if (Has(n, "console", "panel", "admin", "bridge", "computer", "terminal", "monitor", "desk"))
                return f.IsRound ? (f.HalfLen >= 0.8f ? PropKind.RoundTable : PropKind.Drum) : PropKind.Console;
            if (h.Emergency || Has(n, "table")) return f.IsRound ? PropKind.RoundTable : PropKind.Table;
            if (f.IsRound)
            {
                if (h.Indoor && f.HalfLen >= 0.8f && f.HalfLen <= 2.2f) return PropKind.RoundTable;
                if (h.Indoor && f.HalfLen <= 0.6f) return PropKind.Drum;
                return PropKind.Generic;
            }
            if (f.IsBox)
            {
                if (h.TaskConsole && wid <= 1.8f) return PropKind.Console;
                if (len <= 1.3f && wid >= 0.3f) return PropKind.Crate;
            }
            return PropKind.Generic;
        }

        // ------------------------------------------------------------------ modèles

        private static float Sgn(float a) => a < 0f ? -1f : 1f;

        private static uint Darker(uint c, float k)
        {
            uint r = (uint)(((c >> 16) & 0xFF) * k), g = (uint)(((c >> 8) & 0xFF) * k), b = (uint)((c & 0xFF) * k);
            return (r << 16) | (g << 8) | b;
        }

        /// <summary>
        /// Construit le meuble : <paramref name="solid"/> reçoit les parties opaques (couleur par sommet),
        /// <paramref name="art"/> la "peau" à peindre avec le dessin du jeu (dessus, faces avant), posée juste devant.
        /// Renvoie la hauteur du dessus principal (pour caler le dessin).
        /// </summary>
        public static float Build(PropKind kind, in Footprint f, IList<float> xs, IList<float> ys, in PropHints h, ShapeMesh solid, ShapeMesh art)
        {
            var k = new PropKit(solid, art, f.Ux, f.Uy);
            int s0 = solid.VertexCount, a0 = art.VertexCount;
            float L = MathF.Max(f.HalfLen, 0.05f), W = MathF.Max(f.HalfWid, 0.05f);
            // directions utiles, dans le repère du meuble (u = grand axe, v = petit axe)
            float wu = h.WallX * f.Ux + h.WallY * f.Uy, wv = -h.WallX * f.Uy + h.WallY * f.Ux;
            float lu = h.LookX * f.Ux + h.LookY * f.Uy, lv = -h.LookX * f.Uy + h.LookY * f.Ux;
            float top;
            switch (kind)
            {
                case PropKind.RoundTable: top = RoundTable(k, L, W, h.Emergency, h.Blue); break;
                case PropKind.Button: top = EmergencyStand(k); break;
                case PropKind.Drum: top = Drum(k, L, W, h.BaseColor); break;
                case PropKind.Table: top = Table(k, L, W, h.Emergency); break;
                case PropKind.Console: top = Desk(k, L, W, wu, wv); break;
                case PropKind.Chair: top = Chair(k, L, W, lu, lv); break;
                case PropKind.Bed: top = Bed(k, L, W, wu); break;
                case PropKind.Crate: top = Crate(k, L, W); break;
                default:
                    var pu = new List<float>(xs.Count);
                    var pv = new List<float>(xs.Count);
                    for (int i = 0; i < xs.Count; i++)
                    {
                        float dx = xs[i] - f.Cx, dy = ys[i] - f.Cy;
                        pu.Add(dx * f.Ux + dy * f.Uy);
                        pv.Add(-dx * f.Uy + dy * f.Ux);
                    }
                    top = Generic(k, pu, pv, W, h);
                    break;
            }
            k.ToWorld(f.Cx, f.Cy, s0, a0);
            return top;
        }

        /// <summary>Table ronde à pied central ; les grandes sont entourées de quatre bancs en arc de cercle.</summary>
        private static float RoundTable(PropKit k, float L, float W, bool emergency, bool blue)
        {
            uint rim = blue ? BlueRim : TableRim, seat = blue ? BlueSeat : BenchSeat, seatBase = blue ? BlueBase : BenchBase;
            float R = L, sb = W / L; // ellipse : rayon R sur le grand axe, R·sb sur l'autre
            bool benches = R >= 0.95f;
            float rt = benches ? 0.6f * R : 0.92f * R, ht = 0.4f;
            float rb = M.Clamp(0.32f * rt, 0.14f, 0.32f), rc = M.Clamp(0.08f * rt, 0.035f, 0.07f);
            k.Lathe(0f, 0f, 1f, sb, new[] { 0f, rb, rb, rb * 0.75f, rc, rc, rc * 2.8f, 0f },
                    new[] { 0f, 0f, 0.025f, 0.05f, 0.075f, ht - 0.1f, ht - 0.055f, ht - 0.055f }, 20, Metal, false);
            // plateau : chant biseauté, dessus peint avec le dessin du jeu
            k.Lathe(0f, 0f, 1f, sb, new[] { 0f, rt - 0.02f, rt, rt + 0.012f, rt, rt - 0.03f, 0f },
                    new[] { ht - 0.055f, ht - 0.055f, ht - 0.04f, ht - 0.022f, ht - 0.004f, ht, ht }, 40, rim, false);
            k.ArtDisc(0f, 0f, 1f, sb, rt - 0.03f, ht, 40);
            if (benches)
            {
                float r0 = 0.76f * R, r1 = 0.97f * R, half = 0.52f;
                var pu = new List<float>();
                var pv = new List<float>();
                var iu = new List<float>();
                var iv = new List<float>();
                for (int b = 0; b < 4; b++)
                {
                    float mid = MathF.PI * (0.25f + 0.5f * b);
                    pu.Clear(); pv.Clear();
                    const int A = 10;
                    for (int i = 0; i <= A; i++)
                    {
                        float a = mid - half + 2f * half * i / A;
                        pu.Add(r1 * MathF.Cos(a)); pv.Add(r1 * sb * MathF.Sin(a));
                    }
                    for (int i = A; i >= 0; i--)
                    {
                        float a = mid - half + 2f * half * i / A;
                        pu.Add(r0 * MathF.Cos(a)); pv.Add(r0 * sb * MathF.Sin(a));
                    }
                    PropKit.Inset(pu, pv, 0.035f, iu, iv);
                    k.Prism(iu, iv, 0f, 0.07f, 0f, 1, seatBase, false, false);
                    k.Prism(pu, pv, 0.07f, 0.21f, 0.04f, 2, seat, false, false);
                }
            }
            if (emergency) Button(k, 0f, 0f, ht);
            return ht;
        }

        /// <summary>Collision du bouton d'urgence : colonne (cachée sous la table qui le porte) et bouton à hauteur de table.</summary>
        private static float EmergencyStand(PropKit k)
        {
            const float ht = 0.4f;
            k.Lathe(0f, 0f, 1f, 1f, new[] { 0f, 0.2f, 0.2f, 0.09f, 0.09f, 0f }, new[] { 0f, 0f, 0.03f, 0.05f, ht, ht }, 20, MetalDark, false);
            Button(k, 0f, 0f, ht);
            return ht;
        }

        /// <summary>Bouton d'urgence : socle métallique et dôme rouge.</summary>
        private static void Button(PropKit k, float cu, float cv, float z)
        {
            k.Lathe(cu, cv, 1f, 1f, new[] { 0f, 0.19f, 0.19f, 0.165f, 0f }, new[] { z, z, z + 0.03f, z + 0.045f, z + 0.045f }, 28, ButtonBase, false);
            k.Lathe(cu, cv, 1f, 1f, new[] { 0.15f, 0.15f, 0.13f, 0.13f }, new[] { z + 0.045f, z + 0.06f, z + 0.065f, z + 0.045f }, 28, ButtonRing, false);
            k.Lathe(cu, cv, 1f, 1f, new[] { 0.12f, 0.117f, 0.105f, 0.084f, 0.052f, 0f },
                    new[] { z + 0.045f, z + 0.07f, z + 0.095f, z + 0.115f, z + 0.128f, z + 0.133f }, 28, ButtonRed, true);
        }

        /// <summary>Petit objet rond : fût cerclé (tonneau, pot, tabouret).</summary>
        private static float Drum(PropKit k, float L, float W, uint color)
        {
            float R = L * 0.96f, sb = W / L, hd = M.Clamp(0.3f + 0.25f * R, 0.3f, 0.45f), m = hd * 0.5f;
            k.Lathe(0f, 0f, 1f, sb, new[] { 0f, R * 0.88f, R * 0.95f, R, R, R * 0.96f, R * 0.96f, R, R, R * 0.94f, R * 0.86f, 0f },
                    new[] { 0f, 0f, 0.015f, 0.045f, m - 0.025f, m - 0.012f, m + 0.012f, m + 0.025f, hd - 0.035f, hd - 0.008f, hd, hd }, 28, color, false);
            k.ArtDisc(0f, 0f, 1f, sb, R * 0.86f, hd, 28);
            return hd;
        }

        /// <summary>Table rectangulaire : plateau biseauté (dessus peint) sur quatre pieds.</summary>
        private static float Table(PropKit k, float L, float W, bool emergency)
        {
            const float ht = 0.38f, th = 0.055f;
            k.Box(-L + 0.01f, -W + 0.01f, L - 0.01f, W - 0.01f, ht - th, ht, 0.025f, TableRim, true, true);
            float pu = MathF.Max(L - 0.12f, 0.04f), pv = MathF.Max(W - 0.12f, 0.04f), s = M.Clamp(0.08f * W, 0.02f, 0.04f);
            for (int i = 0; i < 4; i++)
            {
                float u = (i & 1) == 0 ? -pu : pu, v = (i & 2) == 0 ? -pv : pv;
                k.Box(u - s, v - s, u + s, v + s, 0f, ht - th, 0.008f, Metal, false, false);
            }
            if (emergency) Button(k, 0f, 0f, ht);
            return ht;
        }

        /// <summary>Console : pupitre incliné vers l'avant (dessus et face avant peints), sur un socle en retrait.</summary>
        private static float Desk(PropKit k, float L, float W, float wu, float wv)
        {
            // L'avant (côté joueur) est à l'opposé du mur ; le pupitre monte vers le mur.
            bool alongV = MathF.Abs(wv) >= MathF.Abs(wu);
            float d = alongV ? W : L, front = alongV ? -Sgn(wv) : -Sgn(wu);
            float rise = M.Clamp(0.6f * d, 0.08f, 0.2f), hf = 0.36f, mid = hf + 0.5f * rise;
            float slope = -front * rise / (2f * d);
            float r = M.Clamp(0.2f * MathF.Min(L, W), 0.012f, 0.03f), inset = MathF.Min(0.05f, 0.25f * MathF.Min(L, W));
            k.Box(-L + inset, -W + inset, L - inset, W - inset, 0f, 0.06f, 0f, MetalDark, false, false);
            k.Prism(Rect(-L + 0.01f, -W + 0.01f, L - 0.01f, W - 0.01f, out var pv), pv, 0.06f, mid, r, 2, ConsoleBody, true, true,
                    alongV ? 0f : slope, alongV ? slope : 0f);
            return mid;
        }

        /// <summary>Chaise de bureau : pied central, assise et dossier rembourrés (le dossier tourne le dos à la console).</summary>
        private static float Chair(PropKit k, float L, float W, float lu, float lv)
        {
            float su = MathF.Min(L, 0.34f) * 0.88f, sv = MathF.Min(W, 0.3f) * 0.88f, rb = MathF.Min(su, sv) * 0.8f;
            k.Lathe(0f, 0f, 1f, 1f, new[] { 0f, rb, rb, rb * 0.45f, 0.035f, 0.035f, 0f }, new[] { 0f, 0f, 0.02f, 0.04f, 0.055f, 0.17f, 0.17f }, 18, MetalDark, false);
            k.RoundBox(0f, 0f, 0.2f, su, sv, 0.04f, 0.3f, Seat);
            bool alongV = MathF.Abs(lv) >= MathF.Abs(lu);
            float back = alongV ? -Sgn(lv) : -Sgn(lu);
            if (alongV) k.RoundBox(0f, back * (sv - 0.035f), 0.37f, su * 0.92f, 0.04f, 0.14f, 0.3f, Seat);
            else k.RoundBox(back * (su - 0.035f), 0f, 0.37f, 0.04f, sv * 0.92f, 0.14f, 0.3f, Seat);
            return 0.24f;
        }

        /// <summary>Lit : cadre, matelas, couverture et oreiller (côté mur).</summary>
        private static float Bed(PropKit k, float L, float W, float wu)
        {
            float head = Sgn(wu);
            k.Box(-L + 0.04f, -W + 0.04f, L - 0.04f, W - 0.04f, 0f, 0.05f, 0f, MetalDark, false, false);
            k.Box(-L, -W, L, W, 0.05f, 0.17f, 0.025f, BedFrame, false, false);
            k.RoundBox(0f, 0f, 0.2f, L - 0.03f, W - 0.03f, 0.045f, 0.25f, Mattress);
            k.RoundBox(-head * 0.17f * L, 0f, 0.225f, 0.74f * L, W - 0.015f, 0.04f, 0.3f, Blanket);
            k.RoundBox(head * (L - 0.15f), 0f, 0.265f, 0.11f, 0.62f * W, 0.04f, 0.45f, Pillow);
            return 0.25f;
        }

        /// <summary>Caisse : corps peint (dessus, face avant) dans un cadre de montants et de traverses.</summary>
        private static float Crate(PropKit k, float L, float W)
        {
            float H = M.Clamp(1.2f * W + 0.1f, 0.3f, 0.7f), t = M.Clamp(0.18f * W, 0.035f, 0.075f), b = t * 0.4f;
            k.Box(-L + b, -W + b, L - b, W - b, 0f, H - 0.012f, 0.015f, CrateBody, true, true);
            for (int i = 0; i < 4; i++)
            {
                float u = (i & 1) == 0 ? -L : L - t, v = (i & 2) == 0 ? -W : W - t;
                k.Box(u, v, u + t, v + t, 0f, H, 0.01f, CrateFrame, false, false);
            }
            for (int z = 0; z < 2; z++)
            {
                float z0 = z == 0 ? 0f : H - t, z1 = z0 + t;
                k.Box(-L, -W, L, -W + t, z0, z1, 0.01f, CrateFrame, false, false);
                k.Box(-L, W - t, L, W, z0, z1, 0.01f, CrateFrame, false, false);
                k.Box(-L, -W, -L + t, W, z0, z1, 0.01f, CrateFrame, false, false);
                k.Box(L - t, -W, L, W, z0, z1, 0.01f, CrateFrame, false, false);
            }
            return H;
        }

        /// <summary>Forme non reconnue : la collision extrudée, arêtes arrondies, posée sur une plinthe en retrait.</summary>
        private static float Generic(PropKit k, List<float> pu, List<float> pv, float W, in PropHints h)
        {
            float H = M.Clamp(h.Height, 0.08f, 2f), plinth = MathF.Min(0.05f, 0.3f * H);
            var iu = new List<float>();
            var iv = new List<float>();
            PropKit.Inset(pu, pv, MathF.Min(0.035f, 0.3f * W), iu, iv);
            k.Prism(iu, iv, 0f, plinth, 0f, 1, Darker(h.BaseColor, 0.55f), false, false);
            k.Prism(pu, pv, plinth, H, M.Clamp(0.25f * W, 0.01f, 0.05f), 2, h.BaseColor, true, true);
            return H;
        }

        private static List<float> Rect(float u0, float v0, float u1, float v1, out List<float> pv)
        {
            pv = new List<float> { v0, v0, v1, v1 };
            return new List<float> { u0, u1, u1, u0 };
        }

        // ------------------------------------------------------------------ éclairage

        /// <summary>
        /// Luminosité d'un sommet selon sa normale (lumière venant d'en haut, un peu du sud-est) ;
        /// le bas des objets est assombri, comme une ombre portée au contact du sol.
        /// </summary>
        public static float Shade(float nx, float ny, float nz, float z)
        {
            const float lx = 0.33f, ly = -0.48f, lz = 0.81f;
            float d = nx * lx + ny * ly + nz * lz;
            float s = 0.42f + 0.2f * (0.5f + 0.5f * nz) + 0.45f * (d > 0f ? d : 0f);
            float ao = 0.7f + 0.3f * M.SmoothStep01(z / 0.14f);
            return M.Clamp(s * ao, 0f, 1f);
        }
    }

    /// <summary>Petites formes de base (extrusions arrondies, tours, boîtes arrondies), dans le repère local du meuble.</summary>
    internal sealed class PropKit
    {
        public const float Skin = 0.008f; // écart entre la peau peinte et la surface opaque
        public readonly ShapeMesh Solid, Art;
        private readonly float _ux, _uy;

        public PropKit(ShapeMesh solid, ShapeMesh art, float ux, float uy)
        {
            Solid = solid; Art = art; _ux = ux; _uy = uy;
        }

        private struct V
        {
            public float X, Y, Z, Nx, Ny, Nz;
            public V(float x, float y, float z, float nx, float ny, float nz) { X = x; Y = y; Z = z; Nx = nx; Ny = ny; Nz = nz; }
        }

        /// <summary>Vrai si une normale locale regarde vers le sud de la carte (les faces dessinées dans le jeu).</summary>
        private bool South(float nu, float nv) => nu * _uy + nv * _ux < -0.5f;

        private int Put(ShapeMesh m, in V v, uint rgb, float off)
        {
            float l = MathF.Sqrt(v.Nx * v.Nx + v.Ny * v.Ny + v.Nz * v.Nz);
            if (l < 1e-6f) l = 1f;
            float x = v.X + v.Nx / l * off, y = v.Y + v.Ny / l * off, z = v.Z + v.Nz / l * off;
            if (m == Art) return m.Add(x, y, z, v.Nx, v.Ny, v.Nz, CrewZone.Body);
            return m.AddColored(x, y, z, v.Nx, v.Ny, v.Nz, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        private void Quad(in V a, in V b, in V c, in V d, uint rgb, bool art)
        {
            QuadInto(Solid, a, b, c, d, rgb, 0f);
            if (art) QuadInto(Art, a, b, c, d, rgb, Skin);
        }

        private void QuadInto(ShapeMesh m, in V a, in V b, in V c, in V d, uint rgb, float off)
        {
            int ia = Put(m, a, rgb, off), ib = Put(m, b, rgb, off), ic = Put(m, c, rgb, off), id = Put(m, d, rgb, off);
            CrewShape.AddTriangle(m, ia, ib, ic);
            CrewShape.AddTriangle(m, ia, ic, id);
        }

        /// <summary>
        /// Normales extérieures des côtés (polygone dans le sens direct), normales lissées aux sommets peu anguleux,
        /// et direction de retrait de chaque sommet (en onglet) pour une distance unité.
        /// </summary>
        private static void Directions(IList<float> pu, IList<float> pv, float[] en, float[] vn, float[] ins, bool[] smooth)
        {
            int n = pu.Count;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                float dx = pu[j] - pu[i], dy = pv[j] - pv[i], l = MathF.Sqrt(dx * dx + dy * dy);
                en[2 * i] = l < 1e-6f ? 0f : dy / l;
                en[2 * i + 1] = l < 1e-6f ? 0f : -dx / l;
            }
            for (int i = 0; i < n; i++)
            {
                int p = (i + n - 1) % n;
                float ax = en[2 * p], ay = en[2 * p + 1], bx = en[2 * i], by = en[2 * i + 1];
                float sx = ax + bx, sy = ay + by, sl = MathF.Sqrt(sx * sx + sy * sy);
                if (sl < 1e-4f) { sx = bx; sy = by; sl = MathF.Max(1e-6f, MathF.Sqrt(bx * bx + by * by)); }
                sx /= sl; sy /= sl;
                vn[2 * i] = sx; vn[2 * i + 1] = sy;
                smooth[i] = ax * bx + ay * by > 0.85f;
                float d = MathF.Max(0.4f, sx * bx + sy * by);
                ins[2 * i] = sx / d; ins[2 * i + 1] = sy / d;
            }
        }

        /// <summary>Polygone rétréci de <paramref name="d"/> (sommets décalés en onglet).</summary>
        public static void Inset(IList<float> pu, IList<float> pv, float d, List<float> ou, List<float> ov)
        {
            int n = pu.Count;
            var en = new float[2 * n];
            var vn = new float[2 * n];
            var ins = new float[2 * n];
            var sm = new bool[n];
            Directions(pu, pv, en, vn, ins, sm);
            ou.Clear(); ov.Clear();
            for (int i = 0; i < n; i++) { ou.Add(pu[i] - ins[2 * i] * d); ov.Add(pv[i] - ins[2 * i + 1] * d); }
        }

        /// <summary>
        /// Extrusion d'un polygone (sens direct) de z0 jusqu'au dessus, plan z = top + su·u + sv·v,
        /// avec l'arête du haut arrondie (rayon r, en 'steps' facettes). Dessus et faces sud peuvent être peints.
        /// </summary>
        public void Prism(IList<float> pu, IList<float> pv, float z0, float top, float r, int steps, uint rgb,
                          bool artTop, bool artFront, float su = 0f, float sv = 0f)
        {
            int n = pu.Count;
            if (n < 3 || top - z0 < 1e-4f) return;
            r = MathF.Max(0f, MathF.Min(r, 0.8f * (top - z0)));
            if (r < 1e-4f) { r = 0f; steps = 0; }
            var en = new float[2 * n];
            var vn = new float[2 * n];
            var ins = new float[2 * n];
            var sm = new bool[n];
            Directions(pu, pv, en, vn, ins, sm);

            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                float ex = en[2 * i], ey = en[2 * i + 1];
                if (ex == 0f && ey == 0f) continue;
                float ax = sm[i] ? vn[2 * i] : ex, ay = sm[i] ? vn[2 * i + 1] : ey;
                float bx = sm[j] ? vn[2 * j] : ex, by = sm[j] ? vn[2 * j + 1] : ey;
                float ta = top + su * pu[i] + sv * pv[i] - r, tb = top + su * pu[j] + sv * pv[j] - r;
                Quad(new V(pu[i], pv[i], z0, ax, ay, 0f), new V(pu[j], pv[j], z0, bx, by, 0f),
                     new V(pu[j], pv[j], tb, bx, by, 0f), new V(pu[i], pv[i], ta, ax, ay, 0f), rgb, artFront && South(ex, ey));
                for (int s = 0; s < steps; s++)
                {
                    V a0 = Round(pu[i], pv[i], ins[2 * i], ins[2 * i + 1], ax, ay, r, s, steps, top, su, sv);
                    V b0 = Round(pu[j], pv[j], ins[2 * j], ins[2 * j + 1], bx, by, r, s, steps, top, su, sv);
                    V a1 = Round(pu[i], pv[i], ins[2 * i], ins[2 * i + 1], ax, ay, r, s + 1, steps, top, su, sv);
                    V b1 = Round(pu[j], pv[j], ins[2 * j], ins[2 * j + 1], bx, by, r, s + 1, steps, top, su, sv);
                    Quad(a0, b0, b1, a1, rgb, false);
                }
            }

            // dessus
            var cu = new List<float>(n);
            var cv = new List<float>(n);
            for (int i = 0; i < n; i++) { cu.Add(pu[i] - ins[2 * i] * r); cv.Add(pv[i] - ins[2 * i + 1] * r); }
            var tris = new List<int>();
            if (!Geo.Triangulate(cu, cv, tris)) return;
            Cap(Solid, cu, cv, tris, top, su, sv, rgb, 0f);
            if (artTop) Cap(Art, cu, cv, tris, top, su, sv, rgb, Skin);
        }

        private static V Round(float u, float v, float iu, float iv, float nx, float ny, float r, int s, int steps, float top, float su, float sv)
        {
            float a = MathF.PI * 0.5f * s / steps, c = MathF.Cos(a), sn = MathF.Sin(a), k = r * (1f - c);
            float pu = u - iu * k, pv = v - iv * k;
            return new V(pu, pv, top + su * pu + sv * pv - r + r * sn, nx * c, ny * c, sn);
        }

        private void Cap(ShapeMesh m, List<float> cu, List<float> cv, List<int> tris, float top, float su, float sv, uint rgb, float off)
        {
            int first = m.VertexCount;
            for (int i = 0; i < cu.Count; i++)
                Put(m, new V(cu[i], cv[i], top + su * cu[i] + sv * cv[i], -su, -sv, 1f), rgb, off);
            for (int t = 0; t + 2 < tris.Count; t += 3)
                CrewShape.AddTriangle(m, first + tris[t], first + tris[t + 1], first + tris[t + 2]);
        }

        /// <summary>Boîte (axes du meuble) aux arêtes du haut arrondies.</summary>
        public void Box(float u0, float v0, float u1, float v1, float z0, float z1, float r, uint rgb, bool artTop, bool artFront)
        {
            if (u1 - u0 < 1e-4f || v1 - v0 < 1e-4f) return;
            r = MathF.Min(r, 0.45f * MathF.Min(u1 - u0, v1 - v0));
            Prism(new[] { u0, u1, u1, u0 }, new[] { v0, v0, v1, v1 }, z0, z1, r, r > 0f ? 2 : 0, rgb, artTop, artFront);
        }

        /// <summary>
        /// Surface de révolution (ellipse : échelles sa, sb sur les deux axes) d'un profil (rayon, hauteur).
        /// smooth = normales lissées (dôme) ; sinon chaque tronçon du profil a sa facette (arêtes nettes).
        /// </summary>
        public void Lathe(float cu, float cv, float sa, float sb, float[] r, float[] z, int segs, uint rgb, bool smooth)
        {
            int np = r.Length;
            var nr = new float[np - 1];
            var nz = new float[np - 1];
            for (int k = 0; k < np - 1; k++)
            {
                float dr = r[k + 1] - r[k], dz = z[k + 1] - z[k], l = MathF.Sqrt(dr * dr + dz * dz);
                nr[k] = l < 1e-6f ? 0f : dz / l;
                nz[k] = l < 1e-6f ? 1f : -dr / l;
            }
            for (int k = 0; k < np - 1; k++)
            {
                float dr = r[k + 1] - r[k], dz = z[k + 1] - z[k];
                if (dr * dr + dz * dz < 1e-10f || (r[k] < 1e-5f && r[k + 1] < 1e-5f)) continue;
                float n0r = nr[k], n0z = nz[k], n1r = nr[k], n1z = nz[k];
                if (smooth && k > 0) { n0r += nr[k - 1]; n0z += nz[k - 1]; }
                if (smooth && k < np - 2) { n1r += nr[k + 1]; n1z += nz[k + 1]; }
                int first = Solid.VertexCount;
                for (int j = 0; j <= segs; j++)
                {
                    float phi = 2f * MathF.PI * j / segs, c = j == segs ? 1f : MathF.Cos(phi), s = j == segs ? 0f : MathF.Sin(phi);
                    Put(Solid, new V(cu + r[k] * sa * c, cv + r[k] * sb * s, z[k], n0r * c / sa, n0r * s / sb, n0z), rgb, 0f);
                    Put(Solid, new V(cu + r[k + 1] * sa * c, cv + r[k + 1] * sb * s, z[k + 1], n1r * c / sa, n1r * s / sb, n1z), rgb, 0f);
                }
                for (int j = 0; j < segs; j++)
                {
                    int a = first + 2 * j, b = a + 1, c = a + 2, d = a + 3;
                    CrewShape.AddTriangle(Solid, a, c, b);
                    CrewShape.AddTriangle(Solid, b, c, d);
                }
            }
        }

        /// <summary>Disque horizontal peint (dessus d'une table ronde, d'un fût).</summary>
        public void ArtDisc(float cu, float cv, float sa, float sb, float rad, float z, int segs)
        {
            int c0 = Art.Add(cu, cv, z + Skin, 0f, 0f, 1f, CrewZone.Body);
            for (int j = 0; j <= segs; j++)
            {
                float phi = 2f * MathF.PI * j / segs;
                Art.Add(cu + rad * sa * MathF.Cos(phi), cv + rad * sb * MathF.Sin(phi), z + Skin, 0f, 0f, 1f, CrewZone.Body);
            }
            for (int j = 0; j < segs; j++) CrewShape.AddTriangle(Art, c0, c0 + 1 + j, c0 + 2 + j);
        }

        /// <summary>Boîte aux coins arrondis (super-ellipsoïde), alignée sur les axes du meuble.</summary>
        public void RoundBox(float cu, float cv, float cz, float ru, float rv, float rz, float e, uint rgb)
        {
            CrewShape.AddSuperEllipsoid(Solid, cu, cv, cz, ru, rv, rz, e, e, 8, 16, CrewZone.Body);
            uint rgba = (rgb << 8) | 0xFFu;
            while (Solid.Rgba.Count < Solid.VertexCount) Solid.Rgba.Add(rgba);
        }

        /// <summary>Passe du repère du meuble (centre, grand axe) à celui de la carte.</summary>
        public void ToWorld(float cx, float cy, int solidFrom, int artFrom)
        {
            Rotate(Solid, solidFrom, cx, cy);
            Rotate(Art, artFrom, cx, cy);
        }

        private void Rotate(ShapeMesh m, int from, float cx, float cy)
        {
            for (int i = from; i < m.VertexCount; i++)
            {
                float u = m.X[i], v = m.Y[i], nu = m.Nx[i], nv = m.Ny[i];
                m.X[i] = cx + u * _ux - v * _uy;
                m.Y[i] = cy + u * _uy + v * _ux;
                m.Nx[i] = nu * _ux - nv * _uy;
                m.Ny[i] = nu * _uy + nv * _ux;
            }
        }
    }
}
