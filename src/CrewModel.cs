using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace AmongUs3D
{
    internal enum ModelKind { Crew, Corpse, Ghost }

    /// <summary>
    /// Ressources partagées par tous les personnages 3D : matériaux et maillages de contour.
    /// Les formes elles-mêmes (<see cref="CrewShape"/>) ne sont calculées qu'une fois par session.
    /// </summary>
    internal sealed class CrewKit
    {
        public static readonly ShapeMesh BodyShape = CrewShape.Body();
        public static readonly ShapeMesh LegShape = CrewShape.Leg();
        public static readonly ShapeMesh CorpseShape = CrewShape.Corpse();
        public static readonly ShapeMesh GhostShape = CrewShape.Ghost();
        public static readonly ShapeMesh PuddleShape = CrewShape.Puddle();
        // Cadavre basculé de 0 à 90° (tous les 2°) : de combien le remonter pour qu'il reste posé sur le sol.
        private static readonly float[] CorpseLift = BuildCorpseLift();

        private static float[] BuildCorpseLift()
        {
            var t = new float[46];
            for (int i = 0; i < t.Length; i++) t[i] = CrewShape.RestLift(CorpseShape, i * 2f * M.Deg2Rad);
            return t;
        }

        public static float CorpseLiftAt(float angle)
        {
            float f = M.Clamp(angle / (2f * M.Deg2Rad), 0f, CorpseLift.Length - 1.001f);
            int i = (int)f;
            return CorpseLift[i] + (CorpseLift[i + 1] - CorpseLift[i]) * (f - i);
        }

        public Material Solid, Outline;
        public Mesh BodyOutline, LegOutline, CorpseOutline, GhostOutline, PuddleMesh;

        public static CrewKit Create()
        {
            Shader sh = null;
            try { sh = Shader.Find("Hidden/Internal-Colored"); } catch { sh = null; }
            if (sh == null) throw new InvalidOperationException("Shader 'Hidden/Internal-Colored' introuvable.");

            var kit = new CrewKit();
            kit.Solid = NewMaterial(sh, "AU3D_Crew", 2f);        // faces avant
            kit.Outline = NewMaterial(sh, "AU3D_CrewOutline", 1f); // faces arrière de la coque noire = contour
            kit.BodyOutline = BuildMesh(BodyShape, "AU3D_CrewBodyOutline", CrewShape.OutlineWidth, out var c1);
            kit.LegOutline = BuildMesh(LegShape, "AU3D_CrewLegOutline", CrewShape.OutlineWidth, out var c2);
            kit.CorpseOutline = BuildMesh(CorpseShape, "AU3D_CrewCorpseOutline", CrewShape.OutlineWidth, out var c3);
            kit.GhostOutline = BuildMesh(GhostShape, "AU3D_CrewGhostOutline", CrewShape.OutlineWidth, out var c4);
            Black(kit.BodyOutline, c1);
            Black(kit.LegOutline, c2);
            Black(kit.CorpseOutline, c3);
            Black(kit.GhostOutline, c4);
            kit.PuddleMesh = BuildMesh(PuddleShape, "AU3D_Flaque", 0f, out _);
            return kit;
        }

        private static Material NewMaterial(Shader sh, string name, float cull)
        {
            var m = new Material(sh) { name = name };
            m.SetFloat("_ZWrite", 1f);
            m.SetFloat("_ZTest", 4f);      // LessEqual
            m.SetFloat("_Cull", cull);     // 1 = masque l'avant, 2 = masque l'arrière
            m.SetFloat("_SrcBlend", 5f);   // SrcAlpha
            m.SetFloat("_DstBlend", 10f);  // OneMinusSrcAlpha (fondu au bord du champ de vision, fantômes)
            m.SetColor("_Color", U.Col(1f, 1f, 1f, 1f));
            // File "transparente" : dessiné après le sol et les murs, dans l'ordre choisi par le mod.
            m.renderQueue = 3000;
            return m;
        }

        public static void Black(Mesh mesh, Il2CppStructArray<Color32> colors)
        {
            Color32 black = U.C32(0, 0, 0);
            for (int i = 0; i < colors.Length; i++) colors[i] = black;
            mesh.colors32 = colors;
        }

        /// <summary>
        /// Convertit une forme en maillage Unity. Dans le monde du mod la verticale "haut" est -Z :
        /// on retourne donc l'axe Z, et l'ordre des sommets des triangles avec lui.
        /// </summary>
        public static Mesh BuildMesh(ShapeMesh s, string name, float inflate, out Il2CppStructArray<Color32> colors)
        {
            int n = s.VertexCount;
            var verts = new Vector3[n];
            for (int i = 0; i < n; i++)
                verts[i] = U.V3(s.X[i] + s.Nx[i] * inflate, s.Y[i] + s.Ny[i] * inflate, -(s.Z[i] + s.Nz[i] * inflate));
            var tris = new int[s.Tris.Count];
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                tris[t] = s.Tris[t];
                tris[t + 1] = s.Tris[t + 2];
                tris[t + 2] = s.Tris[t + 1];
            }
            var mesh = new Mesh { name = name };
            if (n > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = verts;
            colors = new Il2CppStructArray<Color32>(n);
            if (s.HasColors)
            {
                for (int i = 0; i < n; i++)
                {
                    uint c = s.Rgba[i];
                    colors[i] = U.C32((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);
                }
            }
            else
            {
                Color32 white = U.C32(255, 255, 255);
                for (int i = 0; i < n; i++) colors[i] = white;
            }
            mesh.colors32 = colors;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Peint une forme aux couleurs d'un joueur (couleur principale, ombre, visière).</summary>
        public static void Paint(ShapeMesh s, Il2CppStructArray<Color32> colors, Color32 main, Color32 shadow, Color32 visor)
        {
            int n = s.VertexCount;
            for (int i = 0; i < n; i++)
            {
                byte zone = s.Zone[i];
                float nx = s.Nx[i], ny = s.Ny[i], nz = s.Nz[i], z = s.Z[i];
                switch (zone)
                {
                    case CrewZone.Visor:
                    {
                        // bas de la visière plus sombre, reflet blanc en haut à gauche
                        float up = M.SmoothStep01((z - 0.665f) / 0.07f);
                        float k = 0.55f + 0.45f * up;
                        float shine = CrewShape.VisorShine(nx, ny, nz) * 0.9f;
                        colors[i] = U.C32(Mix(visor.r * k, 255f, shine), Mix(visor.g * k, 255f, shine), Mix(visor.b * k, 255f, shine));
                        break;
                    }
                    case CrewZone.Bone:
                    {
                        float l = 0.72f + 0.28f * CrewShape.Light(nx, ny, nz, 1f, CrewZone.Body);
                        colors[i] = U.C32(U.ToByte(0.96f * l), U.ToByte(0.96f * l), U.ToByte(0.90f * l));
                        break;
                    }
                    case CrewZone.Flesh:
                        colors[i] = U.C32(150, 24, 34);
                        break;
                    default:
                    {
                        float l = CrewShape.Light(nx, ny, nz, z, zone);
                        colors[i] = U.C32(Mix(shadow.r, main.r, l), Mix(shadow.g, main.g, l), Mix(shadow.b, main.b, l));
                        break;
                    }
                }
            }
        }

        private static byte Mix(float a, float b, float t)
        {
            float v = a + (b - a) * t;
            return (byte)(v <= 0f ? 0 : (v >= 255f ? 255 : (int)(v + 0.5f)));
        }

        public static ShapeMesh ShapeOf(ModelKind kind)
        {
            switch (kind)
            {
                case ModelKind.Corpse: return CorpseShape;
                case ModelKind.Ghost: return GhostShape;
                default: return BodyShape;
            }
        }

        public Mesh OutlineOf(ModelKind kind)
        {
            switch (kind)
            {
                case ModelKind.Corpse: return CorpseOutline;
                case ModelKind.Ghost: return GhostOutline;
                default: return BodyOutline;
            }
        }

        public void Destroy()
        {
            U.SafeDestroy(Solid);
            U.SafeDestroy(Outline);
            U.SafeDestroy(BodyOutline);
            U.SafeDestroy(LegOutline);
            U.SafeDestroy(CorpseOutline);
            U.SafeDestroy(GhostOutline);
            U.SafeDestroy(PuddleMesh);
        }
    }

    /// <summary>
    /// Un personnage en vrai volume, orienté dans le sens de la marche et peint aux couleurs du joueur :
    ///  - vivant : tronc, visière, sac à dos, deux jambes qui marchent, contour noir ;
    ///  - cadavre : la moitié basse avec l'os ;
    ///  - fantôme : le haut du corps prolongé par une queue, transparent, qui flotte en se balançant.
    /// Le chapeau (objet 3D tiré de son dessin, voir <see cref="HatShape"/>) est posé sur la tête.
    /// </summary>
    internal sealed class CrewModel
    {
        public GameObject Root { get; private set; }

        private Transform _rootT, _bodyT, _legL, _legR;
        private Mesh _bodyMesh, _legMesh;
        private Il2CppStructArray<Color32> _bodyColors, _legColors;
        private Material _solid, _outline;
        private readonly Renderer[] _renderers = new Renderer[16];
        private readonly bool[] _isOutline = new bool[16];
        private int _rendererCount;
        private readonly ModelKind _kind;
        private int _layer;
        private int _colorId = int.MinValue;
        private float _phase, _amp, _yaw, _time;
        private bool _yawInit, _shown;
        private float _lastAlpha = -1f;
        private int _order = int.MinValue;
        private float _scaleXY = -1f, _scaleZ = -1f;
        private float _lastTint = -1f;
        private CrewKit _kit;
        // Animations : cadavre (âge depuis la mort, dissolution par la Vipère) et saut dans une bouche d'aération.
        private float _corpseAge = -1f, _dissolve, _popScale = 1f, _popLift;
        private GameObject _puddleGo;
        private Transform _puddleT;
        private Material _puddleMat;
        private bool _puddleOn;
        // Accessoires 3D (chapeau, visière) : maillages partagés (caches), jamais détruits ici.
        public const int SlotHat = 0, SlotVisor = 1;
        private readonly GameObject[] _accGo = new GameObject[2], _accOutlineGo = new GameObject[2];
        private readonly MeshFilter[] _accFilter = new MeshFilter[2], _accOutlineFilter = new MeshFilter[2];
        private readonly Mesh[] _accMesh = new Mesh[2];
        private SkinPaint _skin;

        public bool Shown => _shown;
        public bool HasHat => _accMesh[SlotHat] != null;

        private CrewModel(ModelKind kind) { _kind = kind; }

        public static CrewModel Create(CrewKit kit, Transform parent, int layer, ModelKind kind)
        {
            var m = new CrewModel(kind);
            try
            {
                m.Build(kit, parent, layer);
                return m;
            }
            catch
            {
                m.Destroy();
                throw;
            }
        }

        private void Build(CrewKit kit, Transform parent, int layer)
        {
            _layer = layer;
            _kit = kit;
            Root = new GameObject(_kind == ModelKind.Corpse ? "AU3D_Corpse3D" : (_kind == ModelKind.Ghost ? "AU3D_Ghost3D" : "AU3D_Crew3D"));
            Root.layer = layer;
            _rootT = Root.transform;
            _rootT.SetParent(parent, false);
            _solid = new Material(kit.Solid);
            _outline = new Material(kit.Outline);

            _bodyMesh = CrewKit.BuildMesh(CrewKit.ShapeOf(_kind), "AU3D_" + _kind, 0f, out _bodyColors);
            _bodyT = AddPart("corps", _rootT, _bodyMesh, _solid, false).transform;
            AddPart("contour", _bodyT, kit.OutlineOf(_kind), _outline, true);
            if (_kind == ModelKind.Crew)
            {
                _legMesh = CrewKit.BuildMesh(CrewKit.LegShape, "AU3D_CrewLeg", 0f, out _legColors);
                _legL = AddLeg("jambe G", -CrewShape.LegSpacing, kit);
                _legR = AddLeg("jambe D", CrewShape.LegSpacing, kit);
            }
            Root.SetActive(false);
        }

        private Transform AddLeg(string name, float x, CrewKit kit)
        {
            GameObject pivot = AddPart(name, _rootT, _legMesh, _solid, false);
            pivot.transform.localPosition = U.V3(x, 0f, -CrewShape.HipHeight);
            AddPart("contour", pivot.transform, kit.LegOutline, _outline, true);
            return pivot.transform;
        }

        private GameObject AddPart(string name, Transform parent, Mesh mesh, Material material, bool outline)
        {
            var go = new GameObject(name);
            go.layer = _layer;
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            if (_order != int.MinValue) r.sortingOrder = _order + (outline ? 1 : 0);
            _isOutline[_rendererCount] = outline;
            _renderers[_rendererCount++] = r;
            return go;
        }

        /// <summary>Repeint le personnage si sa couleur a changé.</summary>
        public void SetColor(int colorId)
        {
            if (colorId == _colorId) return;
            _colorId = colorId;
            Repaint();
        }

        /// <summary>Combinaison 3D : couleurs par sommet du corps et des jambes (null = couleurs du joueur seules).</summary>
        public void SetSkin(SkinPaint skin)
        {
            if (ReferenceEquals(skin, _skin)) return;
            _skin = skin;
            if (_colorId != int.MinValue) Repaint();
        }

        private void Repaint()
        {
            Game.TryPlayerColors(_colorId, out Color32 main, out Color32 shadow, out Color32 visor);
            CrewKit.Paint(CrewKit.ShapeOf(_kind), _bodyColors, main, shadow, visor);
            Overlay(_bodyColors, _skin?.Body);
            _bodyMesh.colors32 = _bodyColors;
            if (_legMesh != null)
            {
                CrewKit.Paint(CrewKit.LegShape, _legColors, main, shadow, visor);
                Overlay(_legColors, _skin?.Leg);
                _legMesh.colors32 = _legColors;
            }
        }

        private static void Overlay(Il2CppStructArray<Color32> colors, Color32[] paint)
        {
            if (paint == null || paint.Length != colors.Length) return;
            for (int i = 0; i < paint.Length; i++)
                if (paint[i].a != 0) colors[i] = paint[i];
        }

        /// <summary>Pose (ou retire, avec null) le chapeau 3D sur la tête, avec son contour noir.</summary>
        public void SetHat(Mesh mesh, Mesh outline) => SetAccessory(SlotHat, mesh, outline, "chapeau");

        /// <summary>Pose (ou retire) la visière cosmétique 3D sur l'avant de la tête.</summary>
        public void SetVisor(Mesh mesh, Mesh outline) => SetAccessory(SlotVisor, mesh, outline, "visiere");

        private void SetAccessory(int slot, Mesh mesh, Mesh outline, string name)
        {
            if (ReferenceEquals(mesh, _accMesh[slot])) return;
            _accMesh[slot] = mesh;
            if (mesh == null)
            {
                if (U.Alive(_accGo[slot])) _accGo[slot].SetActive(false);
                if (U.Alive(_accOutlineGo[slot])) _accOutlineGo[slot].SetActive(false);
                return;
            }
            if (!U.Alive(_accGo[slot]))
            {
                _accGo[slot] = AddPart(name, _bodyT, mesh, _solid, false);
                _accFilter[slot] = _accGo[slot].GetComponent<MeshFilter>();
            }
            else
            {
                _accFilter[slot].sharedMesh = mesh;
            }
            _accGo[slot].SetActive(true);
            if (outline == null)
            {
                if (U.Alive(_accOutlineGo[slot])) _accOutlineGo[slot].SetActive(false);
                return;
            }
            if (!U.Alive(_accOutlineGo[slot]))
            {
                _accOutlineGo[slot] = AddPart(name + " contour", _bodyT, outline, _outline, true);
                _accOutlineFilter[slot] = _accOutlineGo[slot].GetComponent<MeshFilter>();
            }
            else
            {
                _accOutlineFilter[slot].sharedMesh = outline;
            }
            _accOutlineGo[slot].SetActive(true);
        }

        public void Show(bool on)
        {
            if (on == _shown) return;
            _shown = on;
            if (U.Alive(Root)) Root.SetActive(on);
        }

        /// <summary>Cadavre : secondes écoulées depuis la mort (négatif = debout, sans animation) et dissolution (0..1).</summary>
        public void SetCorpse(float age, float dissolve)
        {
            _corpseAge = age;
            _dissolve = M.Clamp01(dissolve);
        }

        /// <summary>Saut dans une bouche d'aération, apparition : taille (1 = normale) et hauteur des pieds (en tailles de personnage).</summary>
        public void SetPop(float scale, float lift)
        {
            _popScale = M.Clamp(scale, 0.02f, 1.5f);
            _popLift = lift;
        }

        /// <summary>Ordre de dessin (les personnages lointains d'abord). Le contour passe juste après le corps.</summary>
        public void SetOrder(int order)
        {
            if (order == _order) return;
            _order = order;
            for (int i = 0; i < _rendererCount; i++) _renderers[i].sortingOrder = order + (_isOutline[i] ? 1 : 0);
        }

        /// <param name="x">position des pieds sur la carte</param>
        /// <param name="height">taille du personnage (du sol au sommet de la tête)</param>
        /// <param name="faceX">direction regardée (vecteur unitaire sur la carte)</param>
        /// <param name="running">vrai si le personnage marche (ou, pour un fantôme, se déplace)</param>
        /// <param name="alpha">opacité (fondu au bord du champ de vision, fantômes, rôles invisibles...)</param>
        public void Update(float x, float y, float height, float faceX, float faceY, bool running, float dt, float alpha)
        {
            float scaleXY = height * _popScale, scaleZ = scaleXY, tint = 0f, fade = 1f;
            if (_kind == ModelKind.Corpse)
            {
                // le corps bascule vers l'avant autour de ses pieds et reste couché, posé sur le sol
                float angle = _corpseAge < 0f ? 0f : CrewShape.FallAngle(_corpseAge);
                SetTilt(_bodyT, angle);
                _bodyT.localPosition = U.V3(0f, 0f, -CrewKit.CorpseLiftAt(angle));
                if (_dissolve > 0.001f)
                {
                    // acide de la Vipère : le corps s'affaisse, verdit, puis disparaît dans sa flaque
                    scaleXY *= 1f + 0.22f * _dissolve;
                    scaleZ *= 1f - 0.85f * M.SmoothStep01(_dissolve / 0.9f);
                    tint = M.Clamp01(_dissolve * 1.6f);
                    fade = 1f - M.SmoothStep01((_dissolve - 0.7f) / 0.3f);
                }
            }
            if (MathF.Abs(scaleXY - _scaleXY) > 0.002f || MathF.Abs(scaleZ - _scaleZ) > 0.002f)
            {
                _scaleXY = scaleXY; _scaleZ = scaleZ;
                _rootT.localScale = U.V3(scaleXY, scaleXY, scaleZ);
            }

            // orientation : on tourne en douceur vers la direction de marche
            float target = MathF.Atan2(faceX, faceY) / M.Deg2Rad;
            if (!_yawInit) { _yaw = target; _yawInit = true; }
            else _yaw += M.DeltaAngle(_yaw, target) * (1f - MathF.Exp(-dt * 14f));
            _rootT.position = U.V3(x, y, -_popLift * height);
            _rootT.rotation = U.RotationZ(-_yaw);

            float wanted = running ? 1f : 0f;
            _amp += (wanted - _amp) * (1f - MathF.Exp(-dt * 12f));
            if (_kind == ModelKind.Crew)
            {
                // marche : les jambes se balancent en opposition, le corps sautille un peu
                if (_amp > 0.01f) _phase += dt * 13f;
                else _phase = 0f;
                float swing = MathF.Sin(_phase) * 0.62f * _amp; // radians
                SetTilt(_legL, swing);
                SetTilt(_legR, -swing);
                _bodyT.localPosition = U.V3(0f, 0f, -MathF.Abs(MathF.Sin(_phase)) * 0.03f * _amp);
            }
            else if (_kind == ModelKind.Ghost)
            {
                // flotte au-dessus du sol, monte et descend doucement, se penche en avant quand il avance
                _time += dt;
                float hover = 0.10f + 0.035f * MathF.Sin(_time * 2.3f);
                _bodyT.localPosition = U.V3(0f, 0f, -hover);
                SetTilt(_bodyT, 0.06f * MathF.Sin(_time * 1.7f) + 0.16f * _amp);
            }

            float a = M.Clamp01(alpha) * fade;
            if (MathF.Abs(a - _lastAlpha) > 0.004f || MathF.Abs(tint - _lastTint) > 0.01f)
            {
                _lastAlpha = a; _lastTint = tint;
                _solid.SetColor("_Color", U.Col(M.Lerp(1f, 0.50f, tint), M.Lerp(1f, 0.95f, tint), M.Lerp(1f, 0.30f, tint), a));
                _outline.SetColor("_Color", U.Col(1f, 1f, 1f, a));
            }
            if (_kind == ModelKind.Corpse) UpdatePuddle(scaleZ, M.Clamp01(alpha));
        }

        /// <summary>Flaque d'acide qui s'étale sous un corps en train de se dissoudre.</summary>
        private void UpdatePuddle(float scaleZ, float alpha)
        {
            bool on = _dissolve > 0.001f;
            if (on && _puddleGo == null && _kit != null && _kit.PuddleMesh != null)
            {
                _puddleMat = new Material(_kit.Solid);
                _puddleGo = AddPart("flaque", _rootT, _kit.PuddleMesh, _puddleMat, false);
                _puddleT = _puddleGo.transform;
                _puddleOn = true;
            }
            if (_puddleGo == null) return;
            if (on != _puddleOn) { _puddleOn = on; _puddleGo.SetActive(on); }
            if (!on) return;
            float s = 0.55f + 1.0f * _dissolve;
            _puddleT.localScale = U.V3(s, s, 1f);
            // toujours 1,5 cm au-dessus du sol, même quand le corps est aplati
            _puddleT.localPosition = U.V3(0f, 0f, -0.015f / MathF.Max(0.02f, scaleZ));
            _puddleMat.SetColor("_Color", U.Col(0.42f, 0.86f, 0.22f, 0.8f * M.Clamp01(_dissolve * 5f) * alpha));
        }

        /// <summary>Rotation autour de l'axe droite-gauche : positif = le haut part vers l'avant.</summary>
        private static void SetTilt(Transform t, float angle)
        {
            float h = angle * 0.5f;
            t.localRotation = U.Quat(MathF.Sin(h), 0f, 0f, MathF.Cos(h));
        }

        public void Destroy()
        {
            U.SafeDestroy(Root);
            U.SafeDestroy(_bodyMesh);
            U.SafeDestroy(_legMesh);
            U.SafeDestroy(_solid);
            U.SafeDestroy(_outline);
            U.SafeDestroy(_puddleMat);
            Root = null;
            _puddleGo = null;
            _kit = null;
            _accMesh[SlotHat] = _accMesh[SlotVisor] = null;
            _skin = null;
        }
    }

    /// <summary>Combinaison 3D : couleur de chaque sommet du corps et des jambes (a = 0 : couleur du joueur).</summary>
    internal sealed class SkinPaint
    {
        public Color32[] Body, Leg;
    }

    /// <summary>Objet 3D fabriqué à partir d'un dessin (chapeau, visière, animal) : volume et contour noir.</summary>
    internal sealed class HatMeshes
    {
        public Mesh Solid, Outline;

        public void Destroy()
        {
            U.SafeDestroy(Solid);
            U.SafeDestroy(Outline);
        }
    }

    /// <summary>Animal en volume : son dessin "gonflé", qui se tourne dans le sens de la marche et sautille en avançant.</summary>
    internal sealed class PetModel
    {
        private GameObject _root;
        private Transform _rootT, _bodyT;
        private MeshFilter _solidF, _outlineF;
        private MeshRenderer _solidR, _outlineR;
        private Material _solid, _outline;
        private HatMeshes _meshes;
        private float _yaw, _phase, _amp, _lastAlpha = -1f;
        private bool _yawInit, _shown;
        private int _order = int.MinValue;

        public bool Shown => _shown;
        public bool Alive => U.Alive(_root);

        public static PetModel Create(CrewKit kit, Transform parent, int layer)
        {
            var m = new PetModel();
            try
            {
                m._root = new GameObject("AU3D_Pet3D");
                m._root.layer = layer;
                m._rootT = m._root.transform;
                m._rootT.SetParent(parent, false);
                m._solid = new Material(kit.Solid);
                m._outline = new Material(kit.Outline);
                var body = new GameObject("corps");
                body.layer = layer;
                m._bodyT = body.transform;
                m._bodyT.SetParent(m._rootT, false);
                m._solidF = body.AddComponent<MeshFilter>();
                m._solidR = body.AddComponent<MeshRenderer>();
                m._solidR.sharedMaterial = m._solid;
                var outline = new GameObject("contour");
                outline.layer = layer;
                outline.transform.SetParent(m._bodyT, false);
                m._outlineF = outline.AddComponent<MeshFilter>();
                m._outlineR = outline.AddComponent<MeshRenderer>();
                m._outlineR.sharedMaterial = m._outline;
                m._root.SetActive(false);
                return m;
            }
            catch
            {
                m.Destroy();
                throw;
            }
        }

        public void SetMesh(HatMeshes meshes)
        {
            if (ReferenceEquals(meshes, _meshes)) return;
            _meshes = meshes;
            _solidF.sharedMesh = meshes?.Solid;
            _outlineF.sharedMesh = meshes?.Outline;
        }

        public void Show(bool on)
        {
            if (on == _shown) return;
            _shown = on;
            if (U.Alive(_root)) _root.SetActive(on);
        }

        public void SetOrder(int order)
        {
            if (order == _order) return;
            _order = order;
            _solidR.sortingOrder = order;
            _outlineR.sortingOrder = order + 1;
        }

        public void Update(float x, float y, float faceX, float faceY, bool moving, float dt, float alpha)
        {
            float target = MathF.Atan2(faceX, faceY) / M.Deg2Rad;
            if (!_yawInit) { _yaw = target; _yawInit = true; }
            else _yaw += M.DeltaAngle(_yaw, target) * (1f - MathF.Exp(-dt * 12f));
            _rootT.position = U.V3(x, y, 0f);
            _rootT.rotation = U.RotationZ(-_yaw);
            _amp += ((moving ? 1f : 0f) - _amp) * (1f - MathF.Exp(-dt * 10f));
            if (_amp > 0.01f) _phase += dt * 14f;
            else _phase = 0f;
            _bodyT.localPosition = U.V3(0f, 0f, -MathF.Abs(MathF.Sin(_phase)) * 0.05f * _amp);
            if (MathF.Abs(alpha - _lastAlpha) > 0.004f)
            {
                _lastAlpha = alpha;
                Color c = U.Col(1f, 1f, 1f, M.Clamp01(alpha));
                _solid.SetColor("_Color", c);
                _outline.SetColor("_Color", c);
            }
        }

        public void Destroy()
        {
            U.SafeDestroy(_root);
            U.SafeDestroy(_solid);
            U.SafeDestroy(_outline);
            _root = null;
            _meshes = null;
        }
    }
}
