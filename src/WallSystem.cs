using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace AmongUs3D
{
    /// <summary>Paramètres de vision de l'image en cours.</summary>
    internal struct VisionParams
    {
        public float Cx, Cy;          // centre (pieds du joueur)
        public float Radius;          // rayon de vision du jeu
        public bool Full;             // fantôme, lobby, ou simulation désactivée
        public float Ambient;         // luminosité hors vision
        public float FadeStart, FadeEnd; // fondu vers le noir au bord de la zone affichée
    }

    /// <summary>
    /// Décor 3D construit à partir des collisions 2D de la carte :
    ///  - calques "vision" (Shadow...) : murs pleine hauteur, là où le jeu coupe la vue (portes comprises) ;
    ///  - calques "physiques" (Ship, ShortObjects) : là où le personnage bute réellement. Selon ce qui se
    ///    trouve derrière, un morceau devient un mur plein, un mur dont le dessin 2D (fenêtres, consoles...)
    ///    est redressé à la verticale, un muret, ou un meuble en volume avec son dessus.
    /// Le dessin redressé est la texture du sol (carte vue de dessus) : dans l'art du jeu, un point situé à
    /// la hauteur h au-dessus du point (x, y) du sol est dessiné en (x, y + h).
    /// Couleurs calculées par sommet (éclairage façon "lampe torche").
    /// </summary>
    internal sealed partial class WallSystem
    {
        private sealed class WallMesh
        {
            public Collider2D Col;
            public GameObject Go;
            public Mesh Mesh;
            public float[] X, Y, Shade;
            public Il2CppStructArray<Color32> Colors;
            public int VertexCount;
            public float MinX, MinY, MaxX, MaxY;
            public bool Dynamic, Visible = true, Dark, Scan;
            public float R, G, B;
            public float[] CR, CG, CB; // couleur propre de chaque sommet (meubles 3D), sinon R, G, B
            // Maillages texturés par la carte 2D : coordonnées "art" de chaque sommet.
            public float[] ArtX, ArtY;
            public Il2CppStructArray<Vector2> Uvs;
            public bool UvFresh;
            public byte[] A; // opacité de chaque sommet (pièces de sol fondues sur les bords), sinon opaque
        }

        private struct Poly
        {
            public float[] X, Y;
            public bool Closed;
        }

        /// <summary>Listes de travail pour fabriquer un maillage.</summary>
        private sealed class Builder
        {
            public readonly List<float> X = new List<float>(), Y = new List<float>(), Z = new List<float>();
            public readonly List<float> Shade = new List<float>(), ArtX = new List<float>(), ArtY = new List<float>();
            public readonly List<int> Tris = new List<int>();
            public readonly List<uint> Rgb = new List<uint>(); // facultatif : couleur de chaque sommet (0xRRGGBB)

            public void Clear()
            {
                X.Clear(); Y.Clear(); Z.Clear(); Shade.Clear(); ArtX.Clear(); ArtY.Clear(); Tris.Clear(); Rgb.Clear();
            }

            public int Vertex(float x, float y, float z, float shade, float artX, float artY)
            {
                X.Add(x); Y.Add(y); Z.Add(z); Shade.Add(shade); ArtX.Add(artX); ArtY.Add(artY);
                return X.Count - 1;
            }

            public void Quad(int b0, int t0, int b1, int t1)
            {
                Tris.Add(b0); Tris.Add(t0); Tris.Add(b1);
                Tris.Add(t0); Tris.Add(t1); Tris.Add(b1);
            }
        }

        // Nature d'un morceau de collision physique.
        private const int Hidden = WallRules.Hidden, Low = WallRules.Low, Face = WallRules.Face, Tall = WallRules.Tall;

        private sealed class Edge
        {
            public float Ax, Ay, Bx, By, Len;
            public bool Steep;
            public int[] Kind;
            public float[] D;
        }

        private sealed class Solid
        {
            public Collider2D Col;
            public List<Poly> Polys;
            public readonly List<Edge> Edges = new List<Edge>();
            public bool ShortLayer, Furniture;
            // Chaque polygone de la collision est une chaîne de bords : premier bord, nombre de bords, refermée ou non.
            public readonly List<int> ChainStart = new List<int>(), ChainCount = new List<int>();
            public readonly List<bool> ChainClosed = new List<bool>();
            // Objets bas adossés à un mur (voir WallRules.FindBumps) : leur contour refermé.
            public List<List<float>> BumpX, BumpY;
        }

        private struct BaseBox { public float X0, X1, Y, D; }

        private readonly List<WallMesh> _meshes = new List<WallMesh>();
        private readonly List<WallMesh> _textured = new List<WallMesh>();
        // Dessins du jeu déjà montrés en 3D (meubles, portes) : retirés du rendu du sol seul.
        private readonly List<SpriteRenderer> _groundHide = new List<SpriteRenderer>();
        private readonly HashSet<IntPtr> _groundHideKeys = new HashSet<IntPtr>();
        private readonly List<SpriteRenderer> _groundHidden = new List<SpriteRenderer>();
        private readonly List<WallSeg> _segments = new List<WallSeg>();   // murs "vision" (ligne de vue)
        private readonly List<WallSeg> _solidSegs = new List<WallSeg>();  // murs physiques (caméra 3e personne)
        private readonly List<(float X, float Y, int Owner)> _visionEnds = new List<(float, float, int)>(); // bouts des murs "vision" ouverts
        private readonly Builder _b = new Builder(), _bt = new Builder();
        private Terrain _terrain = Terrain.Flat;
        /// <summary>Relief de la carte (plat sauf sur The Fungle).</summary>
        public Terrain Ground => _terrain;
        private GameObject _root;
        private Material _material, _floorMaterial, _groundMaterial;
        private bool _includeTriggers;
        private int _roundRobin;
        private float _lastCx = float.NaN, _lastCy, _lastRadius, _lastAmbient;
        private bool _lastFull, _lightDirty = true;
        private float _uvCx = float.NaN, _uvCy, _uvHalf;

        public string Summary { get; private set; } = "(aucune carte)";
        /// <summary>Carte en cours (pour le plafond, réservé aux cartes d'intérieur).</summary>
        public MapKind Kind { get; private set; }
        public int SegmentCount => _segments.Count;

        // ------------------------------------------------------------------ construction

        /// <param name="floorMaterial">carte entière vue de dessus : dessin des meubles et des portes 3D</param>
        /// <param name="groundMaterial">même vue sans les meubles ni les portes : dessin des murs (un pupitre posé devant un
        /// mur ne doit pas y être peint une seconde fois, à plat)</param>
        public void Build(MonoBehaviour mapRoot, bool isLobby, Transform parent, int layer, Material floorMaterial, Material groundMaterial)
        {
            Clear();
            _root = new GameObject("AU3D_Walls");
            _root.layer = layer;
            _root.transform.SetParent(parent, false);
            _material = CreateMaterial();
            _floorMaterial = floorMaterial;
            _groundMaterial = groundMaterial ?? floorMaterial;

            int wallMask = ResolveMask(ModConfig.WallLayers.Value, Game.ShadowMask, "Shadow");
            int obstacleMask = ModConfig.ObstacleHeight.Value > 0.01f || ModConfig.RaiseWallArt.Value
                ? ResolveMask(ModConfig.ObstacleLayers.Value, Game.PhysicalMask, "Ship,ShortObjects")
                : 0;
            obstacleMask &= ~wallMask;

            var colliders = new List<Collider2D>();
            Collect(mapRoot, wallMask | obstacleMask, wallMask, colliders);
            if (ModConfig.DumpGeometry.Value) { DumpGeometry(colliders, wallMask); DumpLinks(mapRoot); }

            int wallCount = 0, wallTriggers = 0;
            foreach (Collider2D c in colliders)
            {
                if ((wallMask & (1 << c.gameObject.layer)) == 0) continue;
                wallCount++;
                if (c.isTrigger) wallTriggers++;
            }
            // Si presque tous les murs "vision" sont des triggers, c'est le fonctionnement normal de la carte.
            _includeTriggers = wallCount > 0 && wallTriggers > wallCount * 0.7f;

            MapKind kind = Game.CurrentMapKind(mapRoot, isLobby);
            Kind = kind;
            _terrain = BuildTerrain(mapRoot, colliders, obstacleMask, kind);
            Color wallColor = WallColor(kind);
            Color doorColor = U.ParseColor(ModConfig.DoorColor.Value, U.Col(0.7f, 0.7f, 0.75f));
            Color obstacleColor = U.ParseColor(ModConfig.ObstacleColor.Value, U.Col(0.3f, 0.32f, 0.38f));
            float wallH = M.Clamp(ModConfig.WallHeight.Value, 0.3f, 10f);
            float obstacleH = M.Clamp(ModConfig.ObstacleHeight.Value, 0f, 2f);

            var polys = new List<Poly>();
            int walls = 0, doors = 0;
            bool trim = ModConfig.WallTrim.Value;

            // 1) murs "vision" pleine hauteur
            foreach (Collider2D c in colliders)
            {
                if ((wallMask & (1 << c.gameObject.layer)) == 0) continue;
                polys.Clear();
                Extract(c, polys);
                bool door = Game.IsDoor(c);
                _b.Clear();
                int owner = _meshes.Count;
                foreach (Poly p in polys)
                {
                    if (!door && !p.Closed && p.X.Length >= 2)
                    {
                        _visionEnds.Add((p.X[0], p.Y[0], owner));
                        _visionEnds.Add((p.X[p.X.Length - 1], p.Y[p.X.Length - 1], owner));
                    }
                    if (_terrain.Active && !door)
                        AddWallOnRelief(p, trim, wallH, owner);
                    else if (trim && !door)
                        WallGeometry.AddPanelStrips(p.X, p.Y, p.Closed, wallH, 0.5f, owner, _b.X, _b.Y, _b.Z, _b.Shade, _b.Tris, _segments);
                    else
                        WallGeometry.AddStrips(p.X, p.Y, p.Closed, wallH, 0.5f, 0.62f, owner, _b.X, _b.Y, _b.Z, _b.Shade, _b.Tris, _segments);
                }
                if (Commit(_b, c, door ? doorColor : wallColor, layer, door, door ? "AU3D_Door" : "AU3D_Wall", false) != null)
                {
                    walls++;
                    if (door) doors++;
                }
            }

            // 1 bis) portes : leur dessin est redressé à l'entrée du passage
            int doorPanels = ModConfig.RaiseWallArt.Value && floorMaterial != null ? BuildDoorPanels(mapRoot, wallH, doorColor, layer) : 0;

            // 2) collisions physiques
            string detail = "";
            if (obstacleMask != 0)
            {
                bool raise = ModConfig.RaiseWallArt.Value && floorMaterial != null;
                if (raise && ModConfig.Decor3D.Value) FindPropHints(mapRoot);
                if (raise)
                    detail = BuildSolids(colliders, obstacleMask, kind, wallH, obstacleH, wallColor, obstacleColor, layer);
                else
                    detail = BuildLowObstacles(colliders, obstacleMask, obstacleH, obstacleColor, layer);
            }

            int patches = BuildGroundPatches(layer);
            _lightDirty = true;
            Summary = $"{walls} murs ({doors} portes, {doorPanels} dressées), {detail}, {_groundHide.Count} dessins retirés du sol, " +
                      $"{patches} ombres sous les meubles dessinés dans le sol, {_segments.Count} segments, carte={kind}, " +
                      $"triggers inclus={_includeTriggers} ({wallTriggers}/{wallCount}), calques murs={U.MaskToNames(wallMask)}, physiques={U.MaskToNames(obstacleMask)}";
            AmongUs3DPlugin.Logger.LogInfo("Carte 3D construite : " + Summary);
        }

        /// <summary>Ancien rendu (réglage MursDebout = false) : simples murets là où le personnage bute.</summary>
        private string BuildLowObstacles(List<Collider2D> colliders, int obstacleMask, float obstacleH, Color obstacleColor, int layer)
        {
            if (obstacleH <= 0.01f) return "0 obstacles";
            var grid = BuildGrid();
            var polys = new List<Poly>();
            int obstacles = 0;
            foreach (Collider2D c in colliders)
            {
                if ((obstacleMask & (1 << c.gameObject.layer)) == 0) continue;
                polys.Clear();
                Extract(c, polys);
                List<Poly> kept = FilterAgainstWalls(polys, grid);
                _b.Clear();
                foreach (Poly p in kept)
                    WallGeometry.AddStrips(p.X, p.Y, p.Closed, obstacleH, 0.5f, 0.8f, _meshes.Count, _b.X, _b.Y, _b.Z, _b.Shade, _b.Tris, null);
                if (Commit(_b, c, obstacleColor, layer, Game.IsDoor(c), "AU3D_Obstacle", false) != null) obstacles++;
            }
            return $"{obstacles} obstacles";
        }

        /// <summary>"auto" = masque fourni par le jeu (Constants), sinon liste de noms de calques.</summary>
        private static int ResolveMask(string setting, int gameMask, string fallbackNames)
        {
            string s = (setting ?? "").Trim();
            if (s.Length == 0 || s.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                int ui = U.Layer("UI"), players = U.Layer("Players"), ghost = U.Layer("Ghost");
                int m = gameMask;
                if (ui >= 0) m &= ~(1 << ui);
                if (players >= 0) m &= ~(1 << players);
                if (ghost >= 0) m &= ~(1 << ghost);
                return m != 0 ? m : U.MaskFromNames(fallbackNames);
            }
            return U.MaskFromNames(s);
        }

        private static void Collect(MonoBehaviour mapRoot, int mask, int wallMask, List<Collider2D> into)
        {
            into.Clear();
            foreach (Collider2D c in mapRoot.GetComponentsInChildren<Collider2D>(true))
                if (c != null && (mask & (1 << c.gameObject.layer)) != 0) into.Add(c);

            int wallsFound = 0;
            foreach (Collider2D c in into)
                if ((wallMask & (1 << c.gameObject.layer)) != 0) wallsFound++;
            if (wallsFound >= 5) return;

            // Certaines cartes (ou le lobby) peuvent avoir leurs murs hors de la hiérarchie principale.
            into.Clear();
            foreach (Collider2D c in UnityEngine.Object.FindObjectsOfType<Collider2D>(true))
            {
                if (c == null || (mask & (1 << c.gameObject.layer)) == 0) continue;
                if (c.attachedRigidbody != null) continue; // joueurs, objets mobiles
                into.Add(c);
            }
        }

        private static void DumpGeometry(List<Collider2D> colliders, int wallMask)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            var polys = new List<Poly>();
            foreach (Collider2D c in colliders)
            {
                try
                {
                    polys.Clear();
                    Extract(c, polys);
                    bool wall = (wallMask & (1 << c.gameObject.layer)) != 0;
                    string type = c.GetIl2CppType().Name;
                    bool on = c.enabled && c.gameObject.activeInHierarchy;
                    foreach (Poly p in polys)
                    {
                        sb.Clear();
                        sb.Append("GEO|").Append(wall ? 'W' : 'O').Append('|').Append(U.LayerName(c.gameObject.layer)).Append('|').Append(type)
                          .Append('|').Append(c.isTrigger ? 'T' : '-').Append(on ? 'E' : '-').Append(Game.IsDoor(c) ? 'D' : '-').Append(p.Closed ? 'C' : '-')
                          .Append('|').Append(c.gameObject.name).Append('|');
                        for (int i = 0; i < p.X.Length; i++)
                            sb.Append(p.X[i].ToString("0.###", inv)).Append(',').Append(p.Y[i].ToString("0.###", inv)).Append(' ');
                        AmongUs3DPlugin.Logger.LogInfo(sb.ToString());
                    }
                }
                catch (Exception e) { AmongUs3DPlugin.Logger.LogInfo("GEO|erreur|" + e.Message); }
            }
        }

        private static Color WallColor(MapKind kind)
        {
            string hex;
            switch (kind)
            {
                case MapKind.Lobby: hex = ModConfig.WallColorLobby.Value; break;
                case MapKind.Mira: hex = ModConfig.WallColorMira.Value; break;
                case MapKind.Polus: hex = ModConfig.WallColorPolus.Value; break;
                case MapKind.Airship: hex = ModConfig.WallColorAirship.Value; break;
                case MapKind.Fungle: hex = ModConfig.WallColorFungle.Value; break;
                default: hex = ModConfig.WallColorSkeld.Value; break;
            }
            return U.ParseColor(hex, U.Col(0.45f, 0.5f, 0.58f));
        }

        private static Material CreateMaterial()
        {
            // Shader interne de Unity (toujours présent dans les builds) : couleur par sommet + écriture du Z.
            Shader sh = null;
            foreach (string n in new[] { "Hidden/Internal-Colored", "Unlit/Color", "Sprites/Default" })
            {
                try { sh = Shader.Find(n); } catch { sh = null; }
                if (sh != null) break;
            }
            if (sh == null) throw new InvalidOperationException("Aucun shader disponible pour les murs.");
            var m = new Material(sh) { name = "AU3D_Walls" };
            if (m.HasProperty("_ZWrite"))
            {
                m.SetFloat("_ZWrite", 1f);
                m.SetFloat("_ZTest", 4f);     // LessEqual
                m.SetFloat("_Cull", 0f);      // double face
                m.SetFloat("_SrcBlend", 1f);  // One
                m.SetFloat("_DstBlend", 0f);  // Zero  => opaque
            }
            if (m.HasProperty("_Color")) m.SetColor("_Color", U.Col(1f, 1f, 1f, 1f));
            m.renderQueue = 2000;
            AmongUs3DPlugin.Logger.LogInfo("Shader des murs : " + sh.name);
            return m;
        }

        // ------------------------------------------------------------------ extraction des formes 2D

        /// <summary>Matrice affine 2D locale -> monde d'un Transform.</summary>
        private struct Affine
        {
            public float A, B, Tx, C, D, Ty;
            public void Apply(float x, float y, out float ox, out float oy)
            {
                ox = A * x + B * y + Tx;
                oy = C * x + D * y + Ty;
            }
        }

        private static Affine GetAffine(Transform t)
        {
            try
            {
                Matrix4x4 m = t.localToWorldMatrix;
                return new Affine { A = m.m00, B = m.m01, Tx = m.m03, C = m.m10, D = m.m11, Ty = m.m13 };
            }
            catch
            {
                Vector3 p = t.position, s = t.lossyScale;
                float a = t.eulerAngles.z * M.Deg2Rad, cos = MathF.Cos(a), sin = MathF.Sin(a);
                return new Affine { A = cos * s.x, B = -sin * s.y, Tx = p.x, C = sin * s.x, D = cos * s.y, Ty = p.y };
            }
        }

        private static void Extract(Collider2D c, List<Poly> into)
        {
            try
            {
                Affine m = GetAffine(c.transform);
                Vector2 off = c.offset;

                EdgeCollider2D edge = c.TryCast<EdgeCollider2D>();
                if (edge != null)
                {
                    Il2CppStructArray<Vector2> pts = edge.points;
                    AddPoly(into, pts, off, m, false);
                    return;
                }

                PolygonCollider2D poly = c.TryCast<PolygonCollider2D>();
                if (poly != null)
                {
                    int paths = poly.pathCount;
                    for (int i = 0; i < paths; i++) AddPoly(into, poly.GetPath(i), off, m, true);
                    return;
                }

                BoxCollider2D box = c.TryCast<BoxCollider2D>();
                if (box != null)
                {
                    Vector2 s = box.size;
                    float r = 0f;
                    try { r = Math.Max(0f, box.edgeRadius); } catch { /* ignoré */ }
                    float hx = s.x * 0.5f + r, hy = s.y * 0.5f + r;
                    float[] lx = { off.x - hx, off.x + hx, off.x + hx, off.x - hx };
                    float[] ly = { off.y - hy, off.y - hy, off.y + hy, off.y + hy };
                    into.Add(Transform(lx, ly, m, true));
                    return;
                }

                CircleCollider2D circle = c.TryCast<CircleCollider2D>();
                if (circle != null)
                {
                    const int N = 16;
                    float r = circle.radius;
                    var lx = new float[N];
                    var ly = new float[N];
                    for (int i = 0; i < N; i++)
                    {
                        float a = i * (MathF.PI * 2f / N);
                        lx[i] = off.x + MathF.Cos(a) * r;
                        ly[i] = off.y + MathF.Sin(a) * r;
                    }
                    into.Add(Transform(lx, ly, m, true));
                    return;
                }

                // Autre type (capsule, composite...) : boîte englobante en coordonnées monde.
                Bounds b = c.bounds;
                Vector3 mn = b.min, mx = b.max;
                into.Add(new Poly
                {
                    X = new[] { mn.x, mx.x, mx.x, mn.x },
                    Y = new[] { mn.y, mn.y, mx.y, mx.y },
                    Closed = true,
                });
            }
            catch (Exception e)
            {
                AmongUs3DPlugin.Logger.LogDebug("Collider ignoré : " + e.Message);
            }
        }

        private static void AddPoly(List<Poly> into, Il2CppStructArray<Vector2> pts, Vector2 off, Affine m, bool closed)
        {
            if (pts == null) return;
            int n = pts.Length;
            if (n < 2) return;
            var lx = new float[n];
            var ly = new float[n];
            for (int i = 0; i < n; i++)
            {
                Vector2 p = pts[i];
                lx[i] = p.x + off.x;
                ly[i] = p.y + off.y;
            }
            into.Add(Transform(lx, ly, m, closed));
        }

        private static Poly Transform(float[] lx, float[] ly, Affine m, bool closed)
        {
            var wx = new float[lx.Length];
            var wy = new float[lx.Length];
            for (int i = 0; i < lx.Length; i++) m.Apply(lx[i], ly[i], out wx[i], out wy[i]);
            return new Poly { X = wx, Y = wy, Closed = closed };
        }

        // ------------------------------------------------------------------ ancien filtrage des obstacles

        private Dictionary<long, List<int>> BuildGrid()
        {
            var grid = new Dictionary<long, List<int>>();
            for (int i = 0; i < _segments.Count; i++)
            {
                WallSeg s = _segments[i];
                int x0 = (int)MathF.Floor(MathF.Min(s.ax, s.bx) - 0.2f), x1 = (int)MathF.Floor(MathF.Max(s.ax, s.bx) + 0.2f);
                int y0 = (int)MathF.Floor(MathF.Min(s.ay, s.by) - 0.2f), y1 = (int)MathF.Floor(MathF.Max(s.ay, s.by) + 0.2f);
                for (int gx = x0; gx <= x1; gx++)
                for (int gy = y0; gy <= y1; gy++)
                {
                    long key = ((long)gx << 32) ^ (uint)gy;
                    if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<int>(4);
                    list.Add(i);
                }
            }
            return grid;
        }

        private bool NearWall(Dictionary<long, List<int>> grid, float x, float y, float tolerance)
        {
            long key = ((long)(int)MathF.Floor(x) << 32) ^ (uint)(int)MathF.Floor(y);
            if (!grid.TryGetValue(key, out var list)) return false;
            foreach (int i in list)
            {
                WallSeg s = _segments[i];
                if (Geo.DistPointSeg(x, y, s.ax, s.ay, s.bx, s.by) < tolerance) return true;
            }
            return false;
        }

        private List<Poly> FilterAgainstWalls(List<Poly> polys, Dictionary<long, List<int>> grid)
        {
            var result = new List<Poly>();
            var runX = new List<float>();
            var runY = new List<float>();
            foreach (Poly p in polys)
            {
                int n = p.X.Length;
                int segCount = p.Closed ? n : n - 1;
                for (int s = 0; s < segCount; s++)
                {
                    float ax = p.X[s], ay = p.Y[s], bx = p.X[(s + 1) % n], by = p.Y[(s + 1) % n];
                    float len = MathF.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                    int pieces = Math.Max(1, (int)MathF.Ceiling(len / 0.5f));
                    runX.Clear(); runY.Clear();
                    for (int k = 0; k < pieces; k++)
                    {
                        float t0 = (float)k / pieces, t1 = (float)(k + 1) / pieces;
                        float x0 = ax + (bx - ax) * t0, y0 = ay + (by - ay) * t0;
                        float x1 = ax + (bx - ax) * t1, y1 = ay + (by - ay) * t1;
                        bool hidden = NearWall(grid, (x0 + x1) * 0.5f, (y0 + y1) * 0.5f, 0.14f);
                        if (hidden)
                        {
                            Flush(result, runX, runY);
                            continue;
                        }
                        if (runX.Count == 0) { runX.Add(x0); runY.Add(y0); }
                        runX.Add(x1); runY.Add(y1);
                    }
                    Flush(result, runX, runY);
                }
            }
            return result;
        }

        private static void Flush(List<Poly> into, List<float> xs, List<float> ys)
        {
            if (xs.Count >= 2) into.Add(new Poly { X = xs.ToArray(), Y = ys.ToArray(), Closed = false });
            xs.Clear();
            ys.Clear();
        }

        // ------------------------------------------------------------------ collisions physiques -> décor 3D

        private const float NearHide = 0.14f;   // collé à un mur "vision" : inutile de le doubler
        private const float NearTall = 0.40f;   // tout près d'un mur "vision" : c'est le pied de ce mur
        private const float MinFace = 0.25f;
        private const float SkinOffset = 0.02f; // le dessin est posé juste devant son support opaque

        private string BuildSolids(List<Collider2D> colliders, int obstacleMask, MapKind kind, float wallH, float obstacleH,
                                   Color wallColor, Color obstacleColor, int layer)
        {
            int shortLayer = U.Layer("ShortObjects");
            bool trim = ModConfig.WallTrim.Value;
            float maxFace = MathF.Min(wallH, 2.2f);
            bool indoor = kind != MapKind.Polus && kind != MapKind.Fungle;
            switch (ModConfig.OuterWalls.Value)
            {
                case ToggleMode.Always: indoor = true; break;
                case ToggleMode.Never: indoor = false; break;
            }

            // ---- inventaire
            var solids = new List<Solid>();
            var physical = new List<WallSeg>();
            var grid = BuildGrid();
            int lowObstacles = 0;
            foreach (Collider2D c in colliders)
            {
                int l = c.gameObject.layer;
                if ((obstacleMask & (1 << l)) == 0) continue;
                bool trigger, on;
                try { trigger = c.isTrigger; on = c.enabled && c.gameObject.activeInHierarchy; }
                catch { continue; }
                if (trigger) continue; // zones de son, de salle... : rien de solide
                var polys = new List<Poly>();
                Extract(c, polys);
                if (polys.Count == 0) continue;
                bool door = Game.IsDoor(c);
                if (door || !on)
                {
                    // Peut apparaître plus tard (porte, obstacle désactivé) : simple muret suivi à chaque image.
                    if (obstacleH <= 0.01f) continue;
                    List<Poly> kept = FilterAgainstWalls(polys, grid);
                    _b.Clear();
                    foreach (Poly p in kept)
                        WallGeometry.AddStrips(p.X, p.Y, p.Closed, door ? wallH : obstacleH, 0.5f, 0.8f, _meshes.Count, _b.X, _b.Y, _b.Z, _b.Shade, _b.Tris, null);
                    if (Commit(_b, c, obstacleColor, layer, true, "AU3D_Obstacle", false) != null) lowObstacles++;
                    continue;
                }
                var solid = new Solid { Col = c, Polys = polys, ShortLayer = l == shortLayer };
                int owner = solids.Count;
                foreach (Poly p in polys)
                {
                    int n = p.X.Length;
                    int segCount = p.Closed ? n : n - 1;
                    solid.ChainStart.Add(solid.Edges.Count);
                    solid.ChainCount.Add(segCount);
                    solid.ChainClosed.Add(IsLoop(p));
                    for (int s = 0; s < segCount; s++)
                    {
                        var e = new Edge { Ax = p.X[s], Ay = p.Y[s], Bx = p.X[(s + 1) % n], By = p.Y[(s + 1) % n] };
                        float dx = e.Bx - e.Ax, dy = e.By - e.Ay;
                        e.Len = MathF.Sqrt(dx * dx + dy * dy);
                        e.Steep = MathF.Abs(dy) > 2.5f * MathF.Abs(dx);
                        int pieces = e.Len < 0.01f ? 0 : Math.Max(1, (int)MathF.Ceiling(e.Len / 0.5f));
                        e.Kind = new int[pieces];
                        e.D = new float[pieces];
                        solid.Edges.Add(e);
                        if (pieces > 0) physical.Add(new WallSeg { ax = e.Ax, ay = e.Ay, bx = e.Bx, by = e.By, owner = owner });
                    }
                }
                solids.Add(solid);
            }

            // ---- nature de chaque morceau
            var measured = new List<float>();
            var pendingDefault = new List<(Edge e, int k)>();
            foreach (Solid s in solids)
            {
                foreach (Edge e in s.Edges)
                {
                    for (int k = 0; k < e.Kind.Length; k++)
                    {
                        float t = (k + 0.5f) / e.Kind.Length;
                        float mx = e.Ax + (e.Bx - e.Ax) * t, my = e.Ay + (e.By - e.Ay) * t;
                        float near = NearestWall(mx, my);
                        if (near < NearHide && Covered(e, k)) { e.Kind[k] = Hidden; continue; }
                        if (s.ShortLayer) { e.Kind[k] = Low; continue; }  // objets bas : jamais des murs
                        if (!e.Steep)
                        {
                            float d = ScanNorth(mx, my, maxFace, physical);
                            if (d >= MinFace) { e.Kind[k] = Face; e.D[k] = d; measured.Add(d); continue; }

                        }
                        if (near < NearTall) { e.Kind[k] = Tall; continue; }
                        e.Kind[k] = Low;
                        if (!indoor) continue;
                        // Carte d'intérieur : s'il n'y a rien du tout d'un côté (le vide), c'est un mur extérieur.
                        float nx = -(e.By - e.Ay) / e.Len, ny = (e.Bx - e.Ax) / e.Len;
                        bool voidA = !AnythingAlong(mx, my, nx, ny, 30f, physical);
                        bool voidB = !AnythingAlong(mx, my, -nx, -ny, 30f, physical);
                        if (voidA == voidB) continue;
                        float insideY = voidA ? -ny : ny; // normale vers l'intérieur de la carte
                        if (insideY < -0.35f && !e.Steep) { e.Kind[k] = Face; pendingDefault.Add((e, k)); }
                        else e.Kind[k] = Tall;
                    }
                }
            }
            float defaultFace = M.Clamp(Geo.Median(measured, 1f), 0.5f, maxFace);
            foreach ((Edge e, int k) in pendingDefault) e.D[k] = defaultFace;

            // ---- petits meubles (chaises, pupitres...) : repérés avant que la règle suivante ne les prenne pour des murs
            MarkFurniture(solids, 2.5f);

            // ---- côtés des blocs de mur : morceaux restés bas mais situés dans la zone d'une face voisine
            var bases = new List<BaseBox>();
            foreach (Solid s in solids)
                foreach (Edge e in s.Edges)
                    for (int k = 0; k < e.Kind.Length; k++)
                    {
                        if (e.Kind[k] != Face) continue;
                        PieceEnds(e, k, out float x0, out float y0, out float x1, out float y1);
                        bases.Add(new BaseBox { X0 = MathF.Min(x0, x1), X1 = MathF.Max(x0, x1), Y = (y0 + y1) * 0.5f, D = e.D[k] });
                    }
            foreach (Solid s in solids)
            {
                if (s.ShortLayer || s.Furniture) continue;
                foreach (Edge e in s.Edges)
                    for (int k = 0; k < e.Kind.Length; k++)
                    {
                        if (e.Kind[k] != Low) continue;
                        float t = (k + 0.5f) / e.Kind.Length;
                        float mx = e.Ax + (e.Bx - e.Ax) * t, my = e.Ay + (e.By - e.Ay) * t;
                        foreach (BaseBox b in bases)
                        {
                            if (mx < b.X0 - 0.6f || mx > b.X1 + 0.6f || my < b.Y - 0.15f || my > b.Y + b.D + 0.15f) continue;
                            e.Kind[k] = Tall;
                            break;
                        }
                    }
            }

            // ---- meubles : formes fermées, basses, de taille raisonnable
            MarkFurniture(solids, 7f);

            // ---- murs restés bas à compléter, objets bas adossés à un mur (réglage MursComplets)
            bool props = ModConfig.Decor3D.Value;
            int completed = 0, bumps = 0;
            // En plein air (Polus, The Fungle) un contour bas est un rocher ou un bord de falaise, pas un mur à compléter.
            if (ModConfig.CompleteWalls.Value && indoor)
            {
                var scanned = new List<WallSeg>();
                foreach (WallSeg sg in _segments)
                    if (_meshes[sg.owner].Scan) scanned.Add(sg);
                foreach (Solid s in solids)
                {
                    if (s.Furniture || s.ShortLayer) continue;
                    for (int ci = 0; ci < s.ChainStart.Count; ci++)
                    {
                        WallRules.Piece[] chain = ToChain(s, ci);
                        if (chain.Length == 0) continue;
                        completed += WallRules.CompleteWalls(chain, s.ChainClosed[ci], scanned, physical);
                        if (props && obstacleH > 0.01f)
                        {
                            if (s.BumpX == null) { s.BumpX = new List<List<float>>(); s.BumpY = new List<List<float>>(); }
                            WallRules.FindBumps(chain, s.ChainClosed[ci], physical, s.BumpX, s.BumpY);
                        }
                        FromChain(s, ci, chain);
                    }
                }
            }

            // Relief : le long d'une pente (pied ou bord d'une falaise) un mur plein boucherait la vue ; il devient un rebord.
            // Le dessin de la falaise est sur la pente elle-même.
            if (_terrain.Active)
                foreach (Solid s in solids)
                {
                    if (s.Furniture) continue;
                    foreach (Edge e in s.Edges)
                        for (int k = 0; k < e.Kind.Length; k++)
                        {
                            if (e.Kind[k] != Tall && e.Kind[k] != Face) continue;
                            PieceEnds(e, k, out float x0, out float y0, out float x1, out float y1);
                            if (OnSlope(x0, y0, x1, y1)) e.Kind[k] = Low;
                        }
                }

            if (ModConfig.DumpGeometry.Value) DumpPieces(solids);

            // ---- maillages
            int tallCount = 0, faceCount = 0, lowCount = 0, furniture = 0;
            _propCounts.Clear();
            _emergencySolid = props && solids.Exists(x => x.Furniture && NameOf(x).IndexOf("emergency", StringComparison.OrdinalIgnoreCase) >= 0);
            var runX = new List<float>();
            var runY = new List<float>();
            var runSegs = new List<WallSeg>();
            foreach (Solid s in solids)
            {
                if (s.Furniture)
                {
                    if (obstacleH > 0.01f && (props ? BuildProp(s, kind, obstacleH, obstacleColor, layer)
                                                    : BuildFurniture(s, obstacleH, obstacleColor, layer))) furniture++;
                    continue;
                }

                // murs pleins (support opaque des faces compris)
                _b.Clear();
                int owner = _meshes.Count;
                foreach (Edge e in s.Edges)
                    EmitRuns(e, runX, runY, kd => kd == Face || kd == Tall,
                             () =>
                             {
                                 runSegs.Add(new WallSeg { ax = runX[0], ay = runY[0], bx = runX[1], by = runY[1] });
                                 if (trim) WallGeometry.AddPanelStrips(runX, runY, false, wallH, 0.5f, owner, _b.X, _b.Y, _b.Z, _b.Shade, _b.Tris, _solidSegs);
                                 else WallGeometry.AddStrips(runX, runY, false, wallH, 0.5f, 0.62f, owner, _b.X, _b.Y, _b.Z, _b.Shade, _b.Tris, _solidSegs);
                             });
                if (Commit(_b, s.Col, wallColor, layer, false, "AU3D_Mur", false) != null) tallCount++;

                // murets
                if (obstacleH > 0.01f)
                {
                    _b.Clear();
                    int lowOwner = _meshes.Count;
                    foreach (Edge e in s.Edges)
                        EmitRuns(e, runX, runY, kd => kd == Low,
                                 () => WallGeometry.AddStrips(runX, runY, false, obstacleH, 0.5f, 0.8f, lowOwner, _b.X, _b.Y, _b.Z, _b.Shade, _b.Tris, null));
                    if (Commit(_b, s.Col, obstacleColor, layer, false, "AU3D_Muret", false) != null) lowCount++;
                }

                // objets adossés à un mur : leur contour refermé, en volume
                if (s.BumpX != null)
                    for (int bi = 0; bi < s.BumpX.Count; bi++)
                        if (BuildProp(s.Col, "", s.BumpX[bi], s.BumpY[bi], true, kind, obstacleH, obstacleColor, layer)) { furniture++; bumps++; }

                // dessin du mur redressé à la verticale
                _bt.Clear();
                foreach (Edge e in s.Edges)
                {
                    int n = e.Kind.Length;
                    if (n == 0) continue;
                    float nx = (e.By - e.Ay) / e.Len, ny = -(e.Bx - e.Ax) / e.Len; // normale côté sud
                    if (ny > 0f) { nx = -nx; ny = -ny; }
                    for (int k = 0; k < n; k++)
                    {
                        if (e.Kind[k] != Face) continue;
                        PieceEnds(e, k, out float x0, out float y0, out float x1, out float y1);
                        float d0 = k > 0 && e.Kind[k - 1] == Face ? 0.5f * (e.D[k - 1] + e.D[k]) : e.D[k];
                        float d1 = k < n - 1 && e.Kind[k + 1] == Face ? 0.5f * (e.D[k] + e.D[k + 1]) : e.D[k];
                        float ox = nx * SkinOffset, oy = ny * SkinOffset;
                        int b0 = _bt.Vertex(x0 + ox, y0 + oy, 0f, 1f, x0, y0);
                        int t0 = _bt.Vertex(x0 + ox, y0 + oy, -d0, 1f, x0, y0 + d0);
                        int b1 = _bt.Vertex(x1 + ox, y1 + oy, 0f, 1f, x1, y1);
                        int t1 = _bt.Vertex(x1 + ox, y1 + oy, -d1, 1f, x1, y1 + d1);
                        _bt.Quad(b0, t0, b1, t1);
                        faceCount++;
                    }
                }
                Commit(_bt, s.Col, U.Col(1f, 1f, 1f), layer, false, "AU3D_Face", true, true);
            }

            int joins = ModConfig.CompleteWalls.Value && solids.Count > 0 ? BuildJoins(runSegs, solids[0].Col, wallH, wallColor, layer) : 0;

            return $"{tallCount} murs physiques, {faceCount} faces redressées (hauteur type {defaultFace:0.00}), " +
                   $"{lowCount + lowObstacles} murets, {furniture} meubles" + PropSummary() +
                   $", {completed} morceaux de mur complétés, {bumps} objets adossés, {joins} raccords";
        }

        /// <summary>
        /// Un morceau n'est "caché" par un mur vision que s'il le longe d'un bout à l'autre : sinon son extrémité
        /// dépasserait du mur vision sans être dessinée, et laisserait un trou.
        /// </summary>
        private bool Covered(Edge e, int k)
        {
            PieceEnds(e, k, out float x0, out float y0, out float x1, out float y1);
            const float inset = 0.12f, limit = NearHide * 1.4f;
            return NearestWall(x0 + (x1 - x0) * inset, y0 + (y1 - y0) * inset) < limit
                && NearestWall(x1 + (x0 - x1) * inset, y1 + (y0 - y1) * inset) < limit;
        }

        private const float RimHeight = 0.25f;

        /// <summary>Vrai si le terrain n'est pas au même niveau des deux côtés du segment (pied ou bord de falaise).</summary>
        private bool OnSlope(float ax, float ay, float bx, float by)
        {
            float dx = bx - ax, dy = by - ay, len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 1e-4f) return false;
            float nx = -dy / len * 0.9f, ny = dx / len * 0.9f, mx = (ax + bx) * 0.5f, my = (ay + by) * 0.5f;
            return MathF.Abs(_terrain.Height(mx + nx, my + ny) - _terrain.Height(mx - nx, my - ny)) >= 0.6f;
        }

        /// <summary>Mur "vision" sur une carte en relief : pleine hauteur sur le plat, simple rebord le long des pentes.</summary>
        private void AddWallOnRelief(Poly p, bool trim, float wallH, int owner)
        {
            int n = p.X.Length, count = p.Closed ? n : n - 1;
            var rx = new List<float>();
            var ry = new List<float>();
            bool current = false;
            for (int s = 0; s < count; s++)
            {
                float ax = p.X[s], ay = p.Y[s], bx = p.X[(s + 1) % n], by = p.Y[(s + 1) % n];
                bool slope = OnSlope(ax, ay, bx, by);
                if (rx.Count > 0 && slope != current) { Flush(current); rx.Clear(); ry.Clear(); }
                if (rx.Count == 0) { rx.Add(ax); ry.Add(ay); }
                rx.Add(bx); ry.Add(by);
                current = slope;
            }
            if (rx.Count > 1) Flush(current);

            void Flush(bool slope)
            {
                if (slope) WallGeometry.AddStrips(rx, ry, false, RimHeight, 0.5f, 0.8f, owner, _b.X, _b.Y, _b.Z, _b.Shade, _b.Tris, _segments);
                else if (trim) WallGeometry.AddPanelStrips(rx, ry, false, wallH, 0.5f, owner, _b.X, _b.Y, _b.Z, _b.Shade, _b.Tris, _segments);
                else WallGeometry.AddStrips(rx, ry, false, wallH, 0.5f, 0.62f, owner, _b.X, _b.Y, _b.Z, _b.Shade, _b.Tris, _segments);
            }
        }

        private static bool SamePoint(float ax, float ay, float bx, float by)
        {
            float dx = ax - bx, dy = ay - by;
            return dx * dx + dy * dy < 1e-6f;
        }

        /// <summary>
        /// Petits raccords pleine hauteur entre un bout de mur resté libre et le mur voisin tout proche (moins de 0,45 :
        /// plus étroit qu'un passage). Sans eux, on voit le jour entre deux murs qui ne se touchent pas tout à fait.
        /// </summary>
        private int BuildJoins(List<WallSeg> runs, Collider2D anchor, float wallH, Color wallColor, int layer)
        {
            const float touch = 0.03f, reach = 0.45f;
            var ends = new List<(float X, float Y)>();
            foreach (WallSeg r in runs) { ends.Add((r.ax, r.ay)); ends.Add((r.bx, r.by)); }
            foreach ((float x, float y, int owner) in _visionEnds)
                if (owner < _meshes.Count && _meshes[owner].Scan) ends.Add((x, y));

            _b.Clear();
            int count = 0, mesh = _meshes.Count;
            foreach ((float px, float py) in ends)
            {
                float best = reach, qx = 0f, qy = 0f;
                bool connected = false, found = false;
                for (int pass = 0; pass < 2 && !connected; pass++)
                {
                    List<WallSeg> list = pass == 0 ? runs : _segments;
                    for (int i = 0; i < list.Count; i++)
                    {
                        WallSeg s = list[i];
                        if (pass == 1 && !_meshes[s.owner].Scan) continue;
                        // rejet rapide
                        if (px < MathF.Min(s.ax, s.bx) - reach || px > MathF.Max(s.ax, s.bx) + reach ||
                            py < MathF.Min(s.ay, s.by) - reach || py > MathF.Max(s.ay, s.by) + reach) continue;
                        // le mur dont ce point est un bout ne compte pas
                        if (SamePoint(px, py, s.ax, s.ay) || SamePoint(px, py, s.bx, s.by)) continue;
                        float sx = s.bx - s.ax, sy = s.by - s.ay, l2 = sx * sx + sy * sy;
                        float t = l2 < 1e-9f ? 0f : M.Clamp01(((px - s.ax) * sx + (py - s.ay) * sy) / l2);
                        float cx = s.ax + sx * t, cy = s.ay + sy * t;
                        float d = MathF.Sqrt((cx - px) * (cx - px) + (cy - py) * (cy - py));
                        if (d <= touch) { connected = true; break; }
                        if (d < best) { best = d; qx = cx; qy = cy; found = true; }
                    }
                }
                if (connected || !found) continue;
                WallGeometry.AddStrips(new[] { px, qx }, new[] { py, qy }, false, wallH, 0.5f, 0.62f, mesh,
                                       _b.X, _b.Y, _b.Z, _b.Shade, _b.Tris, null);
                count++;
            }
            if (count == 0) return 0;
            return Commit(_b, anchor, wallColor, layer, false, "AU3D_Raccord", false) != null ? count : 0;
        }

        /// <summary>
        /// Meubles : formes fermées de taille raisonnable, basses partout sauf là où elles touchent un mur
        /// (un lit contre une cloison a un côté "caché" par ce mur : c'est quand même un meuble).
        /// </summary>
        private static void MarkFurniture(List<Solid> solids, float maxSize)
        {
            foreach (Solid s in solids)
            {
                if (s.Furniture || s.Polys.Count != 1) continue;
                Poly p = s.Polys[0];
                if (!IsLoop(p)) continue;
                int low = 0, total = 0;
                bool wall = false;
                foreach (Edge e in s.Edges)
                    foreach (int kd in e.Kind)
                    {
                        total++;
                        if (kd == Low) low++;
                        else if (kd != Hidden) wall = true;
                    }
                if (wall || total == 0 || low * 2 < total) continue;
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                for (int i = 0; i < p.X.Length; i++)
                {
                    minX = MathF.Min(minX, p.X[i]); maxX = MathF.Max(maxX, p.X[i]);
                    minY = MathF.Min(minY, p.Y[i]); maxY = MathF.Max(maxY, p.Y[i]);
                }
                s.Furniture = maxX - minX <= maxSize && maxY - minY <= maxSize;
            }
        }

        /// <summary>Les morceaux d'une chaîne de bords, dans l'ordre, pour <see cref="WallRules"/>.</summary>
        private static WallRules.Piece[] ToChain(Solid s, int chain)
        {
            int e0 = s.ChainStart[chain], e1 = e0 + s.ChainCount[chain], count = 0;
            for (int i = e0; i < e1; i++) count += s.Edges[i].Kind.Length;
            var pieces = new WallRules.Piece[count];
            int at = 0;
            for (int i = e0; i < e1; i++)
            {
                Edge e = s.Edges[i];
                for (int k = 0; k < e.Kind.Length; k++)
                {
                    PieceEnds(e, k, out float x0, out float y0, out float x1, out float y1);
                    pieces[at++] = new WallRules.Piece { Ax = x0, Ay = y0, Bx = x1, By = y1, Kind = e.Kind[k] };
                }
            }
            return pieces;
        }

        private static void FromChain(Solid s, int chain, WallRules.Piece[] pieces)
        {
            int e0 = s.ChainStart[chain], e1 = e0 + s.ChainCount[chain], at = 0;
            for (int i = e0; i < e1; i++)
            {
                Edge e = s.Edges[i];
                for (int k = 0; k < e.Kind.Length; k++) e.Kind[k] = pieces[at++].Kind;
            }
        }

        private static void PieceEnds(Edge e, int k, out float x0, out float y0, out float x1, out float y1)
        {
            int n = e.Kind.Length;
            float t0 = (float)k / n, t1 = (float)(k + 1) / n;
            x0 = e.Ax + (e.Bx - e.Ax) * t0; y0 = e.Ay + (e.By - e.Ay) * t0;
            x1 = e.Ax + (e.Bx - e.Ax) * t1; y1 = e.Ay + (e.By - e.Ay) * t1;
        }

        /// <summary>Regroupe les morceaux consécutifs d'un même bord qui satisfont le test, et émet chaque suite.</summary>
        private static void EmitRuns(Edge e, List<float> runX, List<float> runY, Func<int, bool> wanted, Action emit)
        {
            int n = e.Kind.Length;
            runX.Clear(); runY.Clear();
            for (int k = 0; k < n; k++)
            {
                if (!wanted(e.Kind[k]))
                {
                    if (runX.Count >= 2) emit();
                    runX.Clear(); runY.Clear();
                    continue;
                }
                PieceEnds(e, k, out float x0, out float y0, out float x1, out float y1);
                // Morceaux alignés : la suite se résume à ses deux extrémités (AddStrips la redécoupe).
                if (runX.Count == 0) { runX.Add(x0); runY.Add(y0); runX.Add(x1); runY.Add(y1); }
                else { runX[1] = x1; runY[1] = y1; }
            }
            if (runX.Count >= 2) emit();
            runX.Clear(); runY.Clear();
        }

        private static bool IsLoop(Poly p)
        {
            int n = p.X.Length;
            if (n < 3) return false;
            if (p.Closed) return true;
            float dx = p.X[0] - p.X[n - 1], dy = p.Y[0] - p.Y[n - 1];
            return n >= 4 && dx * dx + dy * dy < 0.01f * 0.01f * 25f; // chaîne refermée sur elle-même
        }

        /// <summary>Meuble : bloc opaque (côtés + dessus) habillé du dessin 2D (côtés visibles depuis le sud + dessus).</summary>
        private bool BuildFurniture(Solid s, float h, Color color, int layer)
        {
            Poly p = s.Polys[0];
            var xs = new List<float>(p.X);
            var ys = new List<float>(p.Y);
            if (!p.Closed) { xs.RemoveAt(xs.Count - 1); ys.RemoveAt(ys.Count - 1); }
            int n = xs.Count;
            if (n < 3) return false;
            if (Geo.SignedArea(xs, ys) < 0f) { xs.Reverse(); ys.Reverse(); } // sens anti-horaire

            var capTris = new List<int>();
            if (!Geo.Triangulate(xs, ys, capTris)) capTris.Clear();

            // bloc opaque
            _b.Clear();
            WallGeometry.AddStrips(xs, ys, true, h, 0.5f, 0.8f, _meshes.Count, _b.X, _b.Y, _b.Z, _b.Shade, _b.Tris, null);
            int capBase = _b.X.Count;
            for (int i = 0; i < n; i++) _b.Vertex(xs[i], ys[i], -h, 1f, 0f, 0f);
            foreach (int t in capTris) _b.Tris.Add(capBase + t);
            if (Commit(_b, s.Col, color, layer, false, "AU3D_Meuble", false) == null) return false;

            // habillage : dessin de la carte
            _bt.Clear();
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                float dx = xs[j] - xs[i], dy = ys[j] - ys[i];
                float len = MathF.Sqrt(dx * dx + dy * dy);
                if (len < 0.01f) continue;
                float nx = dy / len, ny = -dx / len; // normale extérieure (polygone anti-horaire)
                if (ny > -0.2f) continue;            // seuls les côtés tournés vers le sud sont dessinés dans l'art 2D
                float ox = nx * SkinOffset, oy = ny * SkinOffset;
                int b0 = _bt.Vertex(xs[i] + ox, ys[i] + oy, 0f, 1f, xs[i], ys[i]);
                int t0 = _bt.Vertex(xs[i] + ox, ys[i] + oy, -h, 1f, xs[i], ys[i] + h);
                int b1 = _bt.Vertex(xs[j] + ox, ys[j] + oy, 0f, 1f, xs[j], ys[j]);
                int t1 = _bt.Vertex(xs[j] + ox, ys[j] + oy, -h, 1f, xs[j], ys[j] + h);
                _bt.Quad(b0, t0, b1, t1);
            }
            int skinBase = _bt.X.Count;
            for (int i = 0; i < n; i++) _bt.Vertex(xs[i], ys[i], -h - SkinOffset, 1f, xs[i], ys[i] + h);
            foreach (int t in capTris) _bt.Tris.Add(skinBase + t);
            Commit(_bt, s.Col, U.Col(1f, 1f, 1f), layer, false, "AU3D_MeubleDessin", true);
            return true;
        }

        /// <summary>Distance au mur "vision" fixe le plus proche.</summary>
        private float NearestWall(float x, float y)
        {
            float best = float.MaxValue;
            for (int i = 0; i < _segments.Count; i++)
            {
                WallSeg s = _segments[i];
                // rejet rapide
                if (x < MathF.Min(s.ax, s.bx) - 0.5f || x > MathF.Max(s.ax, s.bx) + 0.5f ||
                    y < MathF.Min(s.ay, s.by) - 0.5f || y > MathF.Max(s.ay, s.by) + 0.5f) continue;
                if (!_meshes[s.owner].Scan) continue;
                float d = Geo.DistPointSeg(x, y, s.ax, s.ay, s.bx, s.by);
                if (d < best) best = d;
            }
            return best;
        }

        /// <summary>
        /// Hauteur de la face d'un mur nord : distance, vers le nord, jusqu'au premier mur "vision",
        /// à condition qu'aucune autre collision physique ne se trouve avant (0 sinon).
        /// </summary>
        private float ScanNorth(float px, float py, float max, List<WallSeg> physical)
        {
            const float eps = 0.12f;
            float best = float.MaxValue;
            for (int i = 0; i < _segments.Count; i++)
            {
                WallSeg s = _segments[i];
                float y = Geo.CrossY(s.ax, s.ay, s.bx, s.by, px);
                if (!(y > py + eps) || y >= best) continue;
                if (!_meshes[s.owner].Scan) continue;
                best = y;
            }
            if (best - py > max) return 0f;
            for (int i = 0; i < physical.Count; i++)
            {
                WallSeg s = physical[i];
                float y = Geo.CrossY(s.ax, s.ay, s.bx, s.by, px);
                if (y > py + eps && y < best - 0.02f) return 0f;
            }
            return best - py;
        }

        /// <summary>Vrai si une collision (vision ou physique) se trouve sur la demi-droite, entre 0,1 et max.</summary>
        private bool AnythingAlong(float ox, float oy, float dx, float dy, float max, List<WallSeg> physical)
        {
            for (int i = 0; i < _segments.Count; i++)
            {
                WallSeg s = _segments[i];
                float t = Geo.RaySeg(ox, oy, dx, dy, s.ax, s.ay, s.bx, s.by);
                if (t > 0.1f && t < max && _meshes[s.owner].Scan) return true;
            }
            for (int i = 0; i < physical.Count; i++)
            {
                WallSeg s = physical[i];
                float t = Geo.RaySeg(ox, oy, dx, dy, s.ax, s.ay, s.bx, s.by);
                if (t > 0.1f && t < max) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ maillages

        private WallMesh Commit(Builder b, Collider2D c, Color color, int layer, bool door, string name, bool textured, bool wallArt = false)
        {
            int n = b.X.Count;
            if (n == 0 || b.Tris.Count == 0) return null;
            if (textured && _floorMaterial == null) return null;

            var verts = new Vector3[n];
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                // posé sur le relief : chaque sommet monte de la hauteur du sol à cet endroit ("haut" = -Z)
                verts[i] = U.V3(b.X[i], b.Y[i], b.Z[i] - (_terrain.Active ? _terrain.Height(b.X[i], b.Y[i]) : 0f));
                if (b.X[i] < minX) minX = b.X[i];
                if (b.X[i] > maxX) maxX = b.X[i];
                if (b.Y[i] < minY) minY = b.Y[i];
                if (b.Y[i] > maxY) maxY = b.Y[i];
            }

            var go = new GameObject(name);
            go.layer = layer;
            go.transform.SetParent(_root.transform, false);
            var mesh = new Mesh { name = name };
            if (n > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = verts;
            var colors = new Il2CppStructArray<Color32>(n);
            Color32 grey = U.C32(40, 40, 40);
            for (int i = 0; i < n; i++) colors[i] = grey;
            mesh.colors32 = colors;

            var wm = new WallMesh
            {
                Col = c, Go = go, Mesh = mesh, Colors = colors, VertexCount = n,
                X = b.X.ToArray(), Y = b.Y.ToArray(), Shade = b.Shade.ToArray(),
                MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY,
                R = color.r, G = color.g, B = color.b,
            };
            if (!textured && b.Rgb.Count == n)
            {
                wm.CR = new float[n]; wm.CG = new float[n]; wm.CB = new float[n];
                for (int i = 0; i < n; i++)
                {
                    uint c32 = b.Rgb[i];
                    wm.CR[i] = ((c32 >> 16) & 0xFF) / 255f; wm.CG[i] = ((c32 >> 8) & 0xFF) / 255f; wm.CB[i] = (c32 & 0xFF) / 255f;
                }
            }
            if (textured)
            {
                wm.ArtX = b.ArtX.ToArray();
                wm.ArtY = b.ArtY.ToArray();
                wm.Uvs = new Il2CppStructArray<Vector2>(n);
                mesh.uv = wm.Uvs;
            }
            mesh.triangles = b.Tris.ToArray();
            mesh.RecalculateBounds();
            mesh.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = textured ? (wallArt ? _groundMaterial : _floorMaterial) : _material;
            if (textured) renderer.sortingOrder = -29000; // après le sol (-30000), avant les personnages

            // "Dynamique" = peut apparaître/disparaître (portes) : vérifié à chaque image, les autres de temps en temps.
            bool dynamic = door;
            bool scan = !door;
            try
            {
                bool off = !c.enabled || (c.isTrigger && !_includeTriggers) || !c.gameObject.activeInHierarchy;
                dynamic |= off;
                scan &= !off;
            }
            catch { dynamic = true; scan = false; }
            wm.Dynamic = dynamic;
            wm.Scan = scan;

            _meshes.Add(wm);
            if (textured) _textured.Add(wm);
            return wm;
        }

        /// <summary>Nombre de dessins du jeu retirés du rendu du sol (meubles et portes en 3D).</summary>
        public int GroundHideCount => _groundHide.Count;

        /// <summary>Diagnostic : ce dessin est-il retiré du rendu du sol ?</summary>
        public bool HidesOnGround(SpriteRenderer r)
        {
            try { return r != null && _groundHideKeys.Contains(r.Pointer); } catch { return false; }
        }

        private void HideOnGround(SpriteRenderer r)
        {
            try { if (r != null && _groundHideKeys.Add(r.Pointer)) _groundHide.Add(r); }
            catch { /* objet détruit */ }
        }

        /// <summary>Masque un instant les dessins des meubles et des portes 3D (rendu du sol seul).</summary>
        public void HideArtForGround()
        {
            _groundHidden.Clear();
            foreach (SpriteRenderer r in _groundHide)
            {
                try
                {
                    if (r != null && r.enabled)
                    {
                        r.enabled = false;
                        _groundHidden.Add(r);
                    }
                }
                catch { /* objet détruit */ }
            }
        }

        public void RestoreArtAfterGround()
        {
            foreach (SpriteRenderer r in _groundHidden)
            {
                try { r.enabled = true; } catch { /* objet détruit */ }
            }
            _groundHidden.Clear();
        }

        public void Clear()
        {
            _artZones.Clear();
            U.SafeDestroy(_shadowMaterial);
            _shadowMaterial = null;
            RestoreArtAfterGround();
            _groundHide.Clear();
            _groundHideKeys.Clear();
            foreach (WallMesh m in _meshes) U.SafeDestroy(m.Mesh);
            _meshes.Clear();
            _textured.Clear();
            _segments.Clear();
            _solidSegs.Clear();
            _visionEnds.Clear();
            _terrain = Terrain.Flat;
            U.SafeDestroy(_root);
            U.SafeDestroy(_material);
            _root = null;
            _material = null;
            _floorMaterial = null;
            _groundMaterial = null;
            _lastCx = float.NaN;
            _uvCx = float.NaN;
            Summary = "(aucune carte)";
        }

        // ------------------------------------------------------------------ mise à jour par image

        /// <summary>Portes : on affiche le mur seulement si le collider est actif (porte fermée).</summary>
        public void UpdateVisibility()
        {
            int count = _meshes.Count;
            if (count == 0) return;
            for (int i = 0; i < count; i++)
                if (_meshes[i].Dynamic) Check(_meshes[i]);
            // Les autres sont vérifiés petit à petit (au cas où).
            for (int k = 0; k < 6; k++)
            {
                _roundRobin = (_roundRobin + 1) % count;
                if (!_meshes[_roundRobin].Dynamic) Check(_meshes[_roundRobin]);
            }
        }

        private void Check(WallMesh m)
        {
            bool visible;
            try
            {
                Collider2D c = m.Col;
                visible = U.Alive(c) && c.enabled && c.gameObject.activeInHierarchy && (_includeTriggers || !c.isTrigger);
            }
            catch { visible = false; }
            if (visible == m.Visible) return;
            m.Visible = visible;
            m.Dark = false;
            m.UvFresh = false;
            _lightDirty = true;
            try { m.Go.SetActive(visible); } catch { /* ignoré */ }
        }

        public void UpdateLighting(in VisionParams v)
        {
            if (!_lightDirty && !float.IsNaN(_lastCx)
                && MathF.Abs(v.Cx - _lastCx) < 0.01f && MathF.Abs(v.Cy - _lastCy) < 0.01f
                && MathF.Abs(v.Radius - _lastRadius) < 0.005f && v.Full == _lastFull
                && MathF.Abs(v.Ambient - _lastAmbient) < 0.001f)
                return;
            _lastCx = v.Cx; _lastCy = v.Cy; _lastRadius = v.Radius; _lastFull = v.Full; _lastAmbient = v.Ambient;
            _lightDirty = false;

            Color32 black = U.C32(0, 0, 0);
            foreach (WallMesh m in _meshes)
            {
                if (!m.Visible) continue;
                float boxDist = WallGeometry.DistanceToBox(v.Cx, v.Cy, m.MinX, m.MinY, m.MaxX, m.MaxY);
                if (boxDist > v.FadeEnd)
                {
                    if (m.Dark) continue;
                    for (int i = 0; i < m.VertexCount; i++) m.Colors[i] = m.A != null ? U.C32(0, 0, 0, m.A[i]) : black;
                    m.Mesh.colors32 = m.Colors;
                    m.Dark = true;
                    continue;
                }
                m.Dark = false;
                bool own = m.CR != null;
                for (int i = 0; i < m.VertexCount; i++)
                {
                    float dx = m.X[i] - v.Cx, dy = m.Y[i] - v.Cy;
                    float b = M.VisionBrightness(MathF.Sqrt(dx * dx + dy * dy), v.Radius, v.Full, v.Ambient, v.FadeStart, v.FadeEnd) * m.Shade[i];
                    byte a = m.A != null ? m.A[i] : (byte)255;
                    m.Colors[i] = own ? U.C32(U.ToByte(m.CR[i] * b), U.ToByte(m.CG[i] * b), U.ToByte(m.CB[i] * b), a)
                                      : U.C32(U.ToByte(m.R * b), U.ToByte(m.G * b), U.ToByte(m.B * b), a);
                }
                m.Mesh.colors32 = m.Colors;
            }
        }

        /// <summary>
        /// Recale le dessin redressé sur la texture du sol, qui suit le joueur :
        /// (cx, cy) = centre de cette texture dans le monde, half = sa demi-taille.
        /// </summary>
        public void UpdateFaceUvs(float cx, float cy, float half)
        {
            if (_textured.Count == 0 || half < 0.5f) return;
            bool moved = cx != _uvCx || cy != _uvCy || half != _uvHalf;
            _uvCx = cx; _uvCy = cy; _uvHalf = half;
            float inv = 0.5f / half;
            float ox = half - cx, oy = half - cy;
            float reach = half + 3f;
            foreach (WallMesh m in _textured)
            {
                if (!m.Visible) continue;
                if (!moved && m.UvFresh) continue;
                // Hors de la texture du sol : inutile (ces morceaux sont de toute façon dans le noir).
                if (m.MinX > cx + reach || m.MaxX < cx - reach || m.MinY > cy + reach || m.MaxY < cy - reach)
                {
                    m.UvFresh = false;
                    continue;
                }
                for (int i = 0; i < m.VertexCount; i++)
                    m.Uvs[i] = U.V2((m.ArtX[i] + ox) * inv, (m.ArtY[i] + oy) * inv);
                m.Mesh.uv = m.Uvs;
                m.UvFresh = true;
            }
        }

        // ------------------------------------------------------------------ requêtes géométriques

        /// <summary>Vrai si un mur visible coupe la ligne A-B (ligne de vue).</summary>
        public bool LineBlocked(float ax, float ay, float bx, float by)
        {
            float minX = MathF.Min(ax, bx), maxX = MathF.Max(ax, bx), minY = MathF.Min(ay, by), maxY = MathF.Max(ay, by);
            foreach (WallSeg s in _segments)
            {
                if (MathF.Max(s.ax, s.bx) < minX || MathF.Min(s.ax, s.bx) > maxX ||
                    MathF.Max(s.ay, s.by) < minY || MathF.Min(s.ay, s.by) > maxY) continue;
                if (!_meshes[s.owner].Visible) continue;
                if (M.SegmentIntersect(ax, ay, bx, by, s.ax, s.ay, s.bx, s.by, out float t) && t > 0.001f && t < 0.999f) return true;
            }
            return false;
        }

        /// <summary>Fraction (0..1) du trajet joueur -> caméra libre de murs (caméra 3e personne).</summary>
        public float ClipFraction(float ax, float ay, float bx, float by)
        {
            float best = ClipAgainst(_segments, ax, ay, bx, by, 1f);
            return ClipAgainst(_solidSegs, ax, ay, bx, by, best);
        }

        /// <summary>
        /// Comme <see cref="ClipFraction"/>, pour une caméra qui a une épaisseur : trois trajets parallèles,
        /// écartés de <paramref name="margin"/>, pour qu'elle ne passe pas non plus un coin de mur.
        /// </summary>
        public float ClipFractionWide(float ax, float ay, float bx, float by, float margin)
        {
            float dx = bx - ax, dy = by - ay, len = MathF.Sqrt(dx * dx + dy * dy);
            float best = ClipFraction(ax, ay, bx, by);
            if (len < 1e-4f) return best;
            float nx = -dy / len * margin, ny = dx / len * margin;
            // Les trajets latéraux partent du joueur lui-même (il peut être collé à un mur) et s'écartent en chemin.
            best = MathF.Min(best, ClipFraction(ax, ay, bx + nx, by + ny));
            return MathF.Min(best, ClipFraction(ax, ay, bx - nx, by - ny));
        }

        private float ClipAgainst(List<WallSeg> segs, float ax, float ay, float bx, float by, float best)
        {
            float minX = MathF.Min(ax, bx), maxX = MathF.Max(ax, bx), minY = MathF.Min(ay, by), maxY = MathF.Max(ay, by);
            foreach (WallSeg s in segs)
            {
                if (MathF.Max(s.ax, s.bx) < minX || MathF.Min(s.ax, s.bx) > maxX ||
                    MathF.Max(s.ay, s.by) < minY || MathF.Min(s.ay, s.by) > maxY) continue;
                if (!_meshes[s.owner].Visible) continue;
                if (M.SegmentIntersect(ax, ay, bx, by, s.ax, s.ay, s.bx, s.by, out float t) && t < best) best = t;
            }
            return best;
        }
    }
}
