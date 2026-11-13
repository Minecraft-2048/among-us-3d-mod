using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace AmongUs3D
{
    /// <summary>
    /// Ressources partagées par tous les personnages 3D : matériaux et maillages de contour.
    /// Les formes elles-mêmes (<see cref="CrewShape"/>) ne sont calculées qu'une fois par session.
    /// </summary>
    internal sealed class CrewKit
    {
        public static readonly ShapeMesh BodyShape = CrewShape.Body();
        public static readonly ShapeMesh LegShape = CrewShape.Leg();
        public static readonly ShapeMesh CorpseShape = CrewShape.Corpse();

        public Material Solid, Outline;
        public Mesh BodyOutline, LegOutline, CorpseOutline;

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
            Black(kit.BodyOutline, c1);
            Black(kit.LegOutline, c2);
            Black(kit.CorpseOutline, c3);
            return kit;
        }

        private static Material NewMaterial(Shader sh, string name, float cull)
        {
            var m = new Material(sh) { name = name };
            m.SetFloat("_ZWrite", 1f);
            m.SetFloat("_ZTest", 4f);      // LessEqual
            m.SetFloat("_Cull", cull);     // 1 = masque l'avant, 2 = masque l'arrière
            m.SetFloat("_SrcBlend", 5f);   // SrcAlpha
            m.SetFloat("_DstBlend", 10f);  // OneMinusSrcAlpha (permet le fondu au bord du champ de vision)
            m.SetColor("_Color", U.Col(1f, 1f, 1f, 1f));
            // File "transparente" : dessiné après le sol et les murs, dans l'ordre choisi par le mod.
            m.renderQueue = 3000;
            return m;
        }

        private static void Black(Mesh mesh, Il2CppStructArray<Color32> colors)
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
            mesh.vertices = verts;
            colors = new Il2CppStructArray<Color32>(n);
            Color32 white = U.C32(255, 255, 255);
            for (int i = 0; i < n; i++) colors[i] = white;
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

        public void Destroy()
        {
            U.SafeDestroy(Solid);
            U.SafeDestroy(Outline);
            U.SafeDestroy(BodyOutline);
            U.SafeDestroy(LegOutline);
            U.SafeDestroy(CorpseOutline);
        }
    }

    /// <summary>
    /// Un personnage en vrai volume : tronc, visière, sac à dos, deux jambes qui marchent, contour noir.
    /// Il est orienté dans le sens de la marche et peint aux couleurs du joueur.
    /// </summary>
    internal sealed class CrewModel
    {
        public GameObject Root { get; private set; }

        private Transform _rootT, _bodyT, _legL, _legR;
        private Mesh _bodyMesh, _legMesh;
        private Il2CppStructArray<Color32> _bodyColors, _legColors;
        private Material _solid, _outline;
        private readonly Renderer[] _renderers = new Renderer[6];
        private int _rendererCount;
        private readonly bool _corpse;
        private int _colorId = int.MinValue;
        private float _phase, _amp, _yaw;
        private bool _yawInit, _shown;
        private float _lastAlpha = -1f;
        private int _order = int.MinValue;
        private float _height = -1f;

        public bool Shown => _shown;

        private CrewModel(bool corpse) { _corpse = corpse; }

        public static CrewModel Create(CrewKit kit, Transform parent, int layer, bool corpse)
        {
            var m = new CrewModel(corpse);
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
            Root = new GameObject(_corpse ? "AU3D_Corpse3D" : "AU3D_Crew3D");
            Root.layer = layer;
            _rootT = Root.transform;
            _rootT.SetParent(parent, false);
            _solid = new Material(kit.Solid);
            _outline = new Material(kit.Outline);

            if (_corpse)
            {
                _bodyMesh = CrewKit.BuildMesh(CrewKit.CorpseShape, "AU3D_Corpse", 0f, out _bodyColors);
                _bodyT = AddPart("corps", _rootT, layer, _bodyMesh, _solid).transform;
                AddPart("contour", _bodyT, layer, kit.CorpseOutline, _outline);
            }
            else
            {
                _bodyMesh = CrewKit.BuildMesh(CrewKit.BodyShape, "AU3D_CrewBody", 0f, out _bodyColors);
                _legMesh = CrewKit.BuildMesh(CrewKit.LegShape, "AU3D_CrewLeg", 0f, out _legColors);
                _bodyT = AddPart("corps", _rootT, layer, _bodyMesh, _solid).transform;
                AddPart("contour", _bodyT, layer, kit.BodyOutline, _outline);
                _legL = AddLeg("jambe G", -CrewShape.LegSpacing, kit, layer);
                _legR = AddLeg("jambe D", CrewShape.LegSpacing, kit, layer);
            }
            Root.SetActive(false);
        }

        private Transform AddLeg(string name, float x, CrewKit kit, int layer)
        {
            GameObject pivot = AddPart(name, _rootT, layer, _legMesh, _solid);
            pivot.transform.localPosition = U.V3(x, 0f, -CrewShape.HipHeight);
            AddPart("contour", pivot.transform, layer, kit.LegOutline, _outline);
            return pivot.transform;
        }

        private GameObject AddPart(string name, Transform parent, int layer, Mesh mesh, Material material)
        {
            var go = new GameObject(name);
            go.layer = layer;
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            _renderers[_rendererCount++] = r;
            return go;
        }

        /// <summary>Repeint le personnage si sa couleur a changé.</summary>
        public void SetColor(int colorId)
        {
            if (colorId == _colorId) return;
            _colorId = colorId;
            Game.TryPlayerColors(colorId, out Color32 main, out Color32 shadow, out Color32 visor);
            CrewKit.Paint(_corpse ? CrewKit.CorpseShape : CrewKit.BodyShape, _bodyColors, main, shadow, visor);
            _bodyMesh.colors32 = _bodyColors;
            if (_legMesh != null)
            {
                CrewKit.Paint(CrewKit.LegShape, _legColors, main, shadow, visor);
                _legMesh.colors32 = _legColors;
            }
        }

        public void Show(bool on)
        {
            if (on == _shown) return;
            _shown = on;
            if (U.Alive(Root)) Root.SetActive(on);
        }

        /// <summary>Ordre de dessin (les personnages lointains d'abord).</summary>
        public void SetOrder(int order)
        {
            if (order == _order) return;
            _order = order;
            for (int i = 0; i < _rendererCount; i++) _renderers[i].sortingOrder = order;
        }

        /// <param name="x">position des pieds sur la carte</param>
        /// <param name="height">taille du personnage (du sol au sommet de la tête)</param>
        /// <param name="faceX">direction regardée (vecteur unitaire sur la carte)</param>
        /// <param name="running">vrai si le personnage marche</param>
        /// <param name="alpha">opacité (fondu au bord du champ de vision, rôles invisibles...)</param>
        public void Update(float x, float y, float height, float faceX, float faceY, bool running, float dt, float alpha)
        {
            if (MathF.Abs(height - _height) > 0.002f)
            {
                _height = height;
                _rootT.localScale = U.V3(height, height, height);
            }

            // orientation : on tourne en douceur vers la direction de marche
            float target = MathF.Atan2(faceX, faceY) / M.Deg2Rad;
            if (!_yawInit) { _yaw = target; _yawInit = true; }
            else _yaw += M.DeltaAngle(_yaw, target) * (1f - MathF.Exp(-dt * 14f));
            _rootT.position = U.V3(x, y, 0f);
            _rootT.rotation = U.RotationZ(-_yaw);

            if (!_corpse)
            {
                // marche : les jambes se balancent en opposition, le corps sautille un peu
                float wanted = running ? 1f : 0f;
                _amp += (wanted - _amp) * (1f - MathF.Exp(-dt * 12f));
                if (_amp > 0.01f) _phase += dt * 13f;
                else _phase = 0f;
                float swing = MathF.Sin(_phase) * 0.62f * _amp; // radians
                SetLeg(_legL, swing);
                SetLeg(_legR, -swing);
                _bodyT.localPosition = U.V3(0f, 0f, -MathF.Abs(MathF.Sin(_phase)) * 0.03f * _amp);
            }

            if (MathF.Abs(alpha - _lastAlpha) > 0.004f)
            {
                _lastAlpha = alpha;
                Color c = U.Col(1f, 1f, 1f, M.Clamp01(alpha));
                _solid.SetColor("_Color", c);
                _outline.SetColor("_Color", c);
            }
        }

        private static void SetLeg(Transform leg, float angle)
        {
            float h = angle * 0.5f;
            leg.localRotation = U.Quat(MathF.Sin(h), 0f, 0f, MathF.Cos(h));
        }

        public void Destroy()
        {
            U.SafeDestroy(Root);
            U.SafeDestroy(_bodyMesh);
            U.SafeDestroy(_legMesh);
            U.SafeDestroy(_solid);
            U.SafeDestroy(_outline);
            Root = null;
        }
    }
}
