using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace AmongUs3D
{
    /// <summary>
    /// Ensemble des caméras ajoutées par le mod :
    ///  - FloorCam : vue de dessus (orthographique) de la carte autour du joueur, rendue dans une texture
    ///               appliquée sur le sol 3D. Les personnages en sont exclus (ils sont en 3D).
    ///  - Cam3D    : caméra perspective à hauteur des yeux ; dessine murs, sol et personnages.
    ///  - MiniCam  : petite copie exacte de la vue 2D d'origine (avec l'obscurité du jeu) + flèche de direction.
    ///  - UiCam    : redessine l'interface du jeu (calque UI) par-dessus la 3D, exactement comme la caméra 2D.
    /// La caméra principale du jeu n'est jamais modifiée (elle continue de servir aux clics, au HUD, etc.).
    /// </summary>
    internal sealed class Rig
    {
        public Camera Main { get; private set; }
        public GameObject Root { get; private set; }
        public int Layer3D { get; private set; }
        public int LayerMini { get; private set; }

        private Camera _cam3D, _floorCam, _uiCam, _miniCam;
        private RenderTexture _floorRT;
        private int _rtSize;
        private float _floorHalf;
        private GameObject _floorGo;
        private Mesh _floorMesh;
        private Material _floorMat;
        private Il2CppStructArray<Color32> _floorColors;
        private float[] _floorDist;
        // relief : place de chaque sommet du sol autour de son centre, et sommets mis à jour quand le sol se déplace
        private Il2CppStructArray<Vector3> _floorVerts;
        private float _floorZx = float.NaN, _floorZy;
        private bool _floorRaised;
        private float _lastRadius = -1f, _lastAmbient = -1f;
        private bool _lastFull;
        private GameObject _arrowGo;
        private Mesh _arrowMesh;
        private Material _arrowMat;
        // plafond : grille calée sur son motif (dalles), éclairée comme le sol
        private const float CeilTile = 4f;
        private GameObject _ceilGo;
        private Mesh _ceilMesh;
        private Material _ceilMat;
        private Texture2D _ceilTex;
        private Il2CppStructArray<Color32> _ceilColors;
        private float[] _ceilX, _ceilY;
        private float _ceilHalf = -1f;
        private int _previousAA = -1;
        // flèches posées sur le sol autour du conduit où se trouve le joueur
        private readonly System.Collections.Generic.List<GameObject> _ventArrows = new System.Collections.Generic.List<GameObject>();
        private Mesh _ventArrowMesh;
        private MeshRenderer _swappedShadowQuad;
        private int _shadowQuadLayer = -1;
        private int _uiLayer = -2;

        public Shader SpriteShader { get; private set; }

        public bool IsAlive => U.Alive(Root) && U.Alive(Main) && U.Alive(_uiCam) && U.Alive(_cam3D);

        public static Rig Create(Camera main, int layer3D, int layerMini)
        {
            var rig = new Rig { Main = main, Layer3D = layer3D, LayerMini = layerMini };
            try
            {
                rig.Build();
                return rig;
            }
            catch
            {
                rig.Destroy();
                throw;
            }
        }

        private void Build()
        {
            SpriteShader = Shader.Find("Sprites/Default");
            if (SpriteShader == null) throw new InvalidOperationException("Shader 'Sprites/Default' introuvable.");

            Root = new GameObject("AmongUs3D_World");
            Root.layer = Layer3D;

            // ---- caméra 3D
            _cam3D = NewCamera("AU3D_Camera3D", Root.transform);
            _cam3D.orthographic = false;
            _cam3D.nearClipPlane = 0.02f;
            _cam3D.farClipPlane = 150f;
            _cam3D.clearFlags = CameraClearFlags.SolidColor;
            _cam3D.cullingMask = 1 << Layer3D;
            // Lissage des bords (réglage AntiCrenelage) : l'écran entier passe en multi-échantillonnage.
            int aa = ModConfig.AntiAliasing.Value;
            if (aa == 2 || aa == 4 || aa == 8)
            {
                try
                {
                    _previousAA = QualitySettings.antiAliasing;
                    if (_previousAA != aa) QualitySettings.antiAliasing = aa;
                    _cam3D.allowMSAA = true;
                }
                catch (Exception e) { Safe.Report("QualitySettings.antiAliasing", e); }
            }

            // ---- sol : caméra orthographique vers une texture
            bool mobile = Platform.IsMobile;
            int size = ModConfig.FloorTextureSize.Value;
            if (size <= 0) size = mobile ? 1024 : 2048;
            _rtSize = Math.Max(256, Math.Min(4096, size));
            _floorRT = new RenderTexture(_rtSize, _rtSize, 16) { name = "AU3D_Floor" };
            _floorRT.useMipMap = true;
            _floorRT.autoGenerateMips = true;
            _floorRT.filterMode = FilterMode.Trilinear;
            _floorRT.wrapMode = TextureWrapMode.Clamp;
            _floorRT.anisoLevel = mobile ? 2 : 8;
            _floorRT.Create();

            _floorCam = NewCamera("AU3D_FloorCamera", Root.transform);
            _floorCam.enabled = false; // rendu manuel (voir RenderFloor)
            _floorCam.orthographic = true;
            _floorCam.clearFlags = CameraClearFlags.SolidColor;
            _floorCam.targetTexture = _floorRT;

            _floorMat = new Material(SpriteShader) { name = "AU3D_FloorMat" };
            _floorMat.mainTexture = _floorRT;
            _floorGo = new GameObject("AU3D_Floor");
            _floorGo.layer = Layer3D;
            _floorGo.transform.SetParent(Root.transform, false);
            _floorMesh = new Mesh { name = "AU3D_FloorMesh" };
            _floorGo.AddComponent<MeshFilter>().sharedMesh = _floorMesh;
            var floorRenderer = _floorGo.AddComponent<MeshRenderer>();
            floorRenderer.sharedMaterial = _floorMat;
            floorRenderer.sortingOrder = -30000;
            BuildFloorMesh(M.Clamp(ModConfig.FloorViewDistance.Value, 6f, 40f), mobile ? 48 : 72);

            // ---- caméras attachées à la caméra du jeu (même point de vue que la 2D)
            _uiCam = NewCamera("AU3D_UiCamera", Main.transform);
            _uiCam.clearFlags = CameraClearFlags.Depth;

            _miniCam = NewCamera("AU3D_MinimapCamera", Main.transform);
            _miniCam.clearFlags = CameraClearFlags.SolidColor;

            // ---- flèche de direction (visible seulement dans la mini-carte)
            _arrowMat = new Material(SpriteShader) { name = "AU3D_ArrowMat" };
            _arrowGo = new GameObject("AU3D_MinimapArrow");
            _arrowGo.layer = LayerMini;
            _arrowGo.transform.SetParent(Root.transform, false);
            _arrowMesh = BuildArrowMesh();
            _arrowGo.AddComponent<MeshFilter>().sharedMesh = _arrowMesh;
            var ar = _arrowGo.AddComponent<MeshRenderer>();
            ar.sharedMaterial = _arrowMat;
            ar.sortingOrder = 32000;

            SetActive(false);
        }

        private static Camera NewCamera(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = U.V3(0f, 0f, 0f);
            go.transform.localRotation = U.Quat(0f, 0f, 0f, 1f);
            Camera c = go.AddComponent<Camera>();
            c.useOcclusionCulling = false;
            c.allowHDR = false;
            c.allowMSAA = false;
            return c;
        }

        /// <summary>Grille du sol : la couleur des sommets assombrit le sol hors du rayon de vision.</summary>
        private void BuildFloorMesh(float half, int grid)
        {
            _floorHalf = half;
            int n = (grid + 1) * (grid + 1);
            var verts = new Vector3[n];
            var uvs = new Vector2[n];
            _floorDist = new float[n];
            for (int j = 0; j <= grid; j++)
            for (int i = 0; i <= grid; i++)
            {
                int k = j * (grid + 1) + i;
                float u = (float)i / grid, v = (float)j / grid;
                float x = -half + 2f * half * u, y = -half + 2f * half * v;
                verts[k] = U.V3(x, y, 0f);
                uvs[k] = U.V2(u, v);
                _floorDist[k] = MathF.Sqrt(x * x + y * y);
            }
            var tris = new int[grid * grid * 6];
            int t = 0;
            for (int j = 0; j < grid; j++)
            for (int i = 0; i < grid; i++)
            {
                int a = j * (grid + 1) + i, b = a + 1, c = a + grid + 1, d = c + 1;
                tris[t++] = a; tris[t++] = c; tris[t++] = b;
                tris[t++] = b; tris[t++] = c; tris[t++] = d;
            }
            _floorColors = new Il2CppStructArray<Color32>(n);
            for (int k = 0; k < n; k++) _floorColors[k] = U.C32(255, 255, 255);
            _floorVerts = new Il2CppStructArray<Vector3>(n);
            for (int k = 0; k < n; k++) _floorVerts[k] = verts[k];
            _floorZx = float.NaN;
            _floorRaised = false;
            _floorMesh.Clear();
            _floorMesh.vertices = verts;
            _floorMesh.uv = uvs;
            _floorMesh.colors32 = _floorColors;
            _floorMesh.triangles = tris;
            _floorMesh.RecalculateBounds();
            _floorMesh.MarkDynamic();
            _lastRadius = -1f;
        }

        private static Mesh BuildArrowMesh()
        {
            var m = new Mesh { name = "AU3D_Arrow" };
            m.vertices = new[]
            {
                U.V3(0f, 0.5f, 0f), U.V3(-0.32f, -0.32f, 0f), U.V3(0f, -0.12f, 0f), U.V3(0.32f, -0.32f, 0f),
            };
            var yellow = U.C32(255, 225, 40);
            var orange = U.C32(255, 140, 0);
            m.colors32 = new[] { yellow, orange, orange, orange };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            m.RecalculateBounds();
            return m;
        }

        // ------------------------------------------------------------------ état

        public void SetActive(bool on)
        {
            if (U.Alive(Root)) Root.SetActive(on);
            if (U.Alive(_uiCam)) _uiCam.enabled = on && _ownUi;
            if (U.Alive(_miniCam)) _miniCam.enabled = on;
            if (!on) RestoreShadowQuad();
        }

        public void Destroy()
        {
            RestoreShadowQuad();
            if (U.Alive(_uiCam)) U.SafeDestroy(_uiCam.gameObject);
            if (U.Alive(_miniCam)) U.SafeDestroy(_miniCam.gameObject);
            U.SafeDestroy(Root);
            if (U.Alive(_floorRT))
            {
                try { _floorRT.Release(); } catch { /* ignoré */ }
                U.SafeDestroy(_floorRT);
            }
            U.SafeDestroy(_floorMesh);
            U.SafeDestroy(_floorMat);
            U.SafeDestroy(_arrowMesh);
            U.SafeDestroy(_arrowMat);
            U.SafeDestroy(_ventArrowMesh);
            _ventArrows.Clear();
            U.SafeDestroy(_ceilMesh);
            U.SafeDestroy(_ceilMat);
            U.SafeDestroy(_ceilTex);
            _ceilGo = null;
            if (_previousAA >= 0)
            {
                try { if (QualitySettings.antiAliasing != _previousAA) QualitySettings.antiAliasing = _previousAA; } catch { /* ignoré */ }
                _previousAA = -1;
            }
            Root = null;
        }

        // ------------------------------------------------------------------ masques de calques

        public int UiMask()
        {
            if (_uiLayer == -2) _uiLayer = U.Layer("UI");
            int mask = _uiLayer >= 0 ? 1 << _uiLayer : 0;
            HudManager hud = Game.Hud;
            if (hud != null)
            {
                try { mask |= 1 << hud.gameObject.layer; } catch { /* ignoré */ }
            }
            mask |= U.MaskFromNames(ModConfig.ExtraUiLayers.Value);
            if (mask == 0) mask = 1 << 5; // calque UI standard de Unity
            return mask & ~(1 << Layer3D) & ~(1 << LayerMini);
        }

        /// <summary>
        /// Si l'obscurité 2D du jeu (HudManager.ShadowQuad) est sur le calque de l'interface, on la déplace
        /// sur le calque de la mini-carte : elle reste visible dans la mini-carte mais ne recouvre pas la 3D.
        /// </summary>
        private void SwapShadowQuad(int uiMask)
        {
            if (_swappedShadowQuad != null && U.Alive(_swappedShadowQuad)) return;
            _swappedShadowQuad = null;
            MeshRenderer sq = Game.ShadowQuad;
            if (sq == null) return;
            try
            {
                int layer = sq.gameObject.layer;
                if ((uiMask & (1 << layer)) == 0) return;
                _shadowQuadLayer = layer;
                sq.gameObject.layer = LayerMini;
                _swappedShadowQuad = sq;
            }
            catch { /* ignoré */ }
        }

        private void RestoreShadowQuad()
        {
            if (_swappedShadowQuad == null) return;
            try
            {
                if (U.Alive(_swappedShadowQuad) && _shadowQuadLayer >= 0) _swappedShadowQuad.gameObject.layer = _shadowQuadLayer;
            }
            catch { /* ignoré */ }
            _swappedShadowQuad = null;
        }

        // ------------------------------------------------------------------ mise à jour par image

        public void UpdateCameras(float camX, float camY, float camZ, float fx, float fy, float fz, Color background)
        {
            float d = Main.depth;

            _cam3D.transform.position = U.V3(camX, camY, camZ);
            _cam3D.transform.rotation = U.LookRotation(fx, fy, fz, 0f, 0f, -1f);
            _cam3D.fieldOfView = M.Clamp(ModConfig.FieldOfView.Value, 30f, 120f);
            _cam3D.backgroundColor = background;
            _cam3D.depth = d + 0.1f;   // juste après la caméra du jeu, avant toute autre caméra d'écran
            _cam3D.cullingMask = 1 << Layer3D;

            // Among Us dessine déjà son interface avec sa propre caméra, après les nôtres : la redessiner
            // doublerait tous les éléments semi-transparents. Notre caméra d'interface ne sert que si le jeu n'en a pas.
            if (Time.unscaledTime >= _nextUiCheck)
            {
                _nextUiCheck = Time.unscaledTime + 1f;
                _ownUi = !Game.HasUiCameraAbove(Main);
            }
            if (_uiCam.enabled != _ownUi) _uiCam.enabled = _ownUi;
            if (!_ownUi) return;

            int ui = UiMask();
            SwapShadowQuad(ui);

            CopyProjection(_uiCam);
            _uiCam.cullingMask = ui;
            _uiCam.depth = d + 0.3f;
        }

        private bool _ownUi = true;
        private float _nextUiCheck;

        /// <summary>Vrai si le mod dessine lui-même l'interface (le jeu n'a pas de caméra d'interface séparée).</summary>
        public bool OwnUiCamera => _ownUi;

        public void UpdateMinimap(bool show, float playerX, float playerY, float yaw, float sizeFraction, ScreenCorner corner, float topReserved)
        {
            _miniCam.enabled = show;
            _arrowGo.SetActive(show);
            if (!show) return;

            CopyProjection(_miniCam);
            int ui = UiMask();
            _miniCam.cullingMask = (Main.cullingMask & ~ui & ~(1 << Layer3D)) | (1 << LayerMini);
            _miniCam.depth = Main.depth + 0.2f;
            _miniCam.backgroundColor = Main.backgroundColor;

            float h = M.Clamp(sizeFraction, 0.1f, 0.6f);
            float w = h; // même rapport largeur/hauteur que l'écran => vue 2D identique, en petit
            float mx = 0.012f, my = 0.02f;
            if (corner == ScreenCorner.Auto) corner = Platform.IsMobile ? ScreenCorner.TopCenter : ScreenCorner.BottomLeft;
            float x, y;
            switch (corner)
            {
                case ScreenCorner.BottomRight: x = 1f - w - mx; y = my; break;
                case ScreenCorner.TopLeft: x = mx; y = 1f - h - my; break;
                case ScreenCorner.TopRight: x = 1f - w - mx; y = 1f - h - my; break;
                case ScreenCorner.TopCenter: x = 0.5f - w * 0.5f; y = 1f - h - my - topReserved; break;
                default: x = mx; y = my; break;
            }
            _miniCam.rect = new Rect(x, y, w, h);

            // Flèche : position du joueur 2D, pointe dans la direction regardée.
            float size = Main.orthographicSize * 0.12f;
            Vector3 camPos = Main.transform.position;
            _arrowGo.transform.position = U.V3(playerX, playerY, camPos.z + Math.Max(0.5f, Main.nearClipPlane + 0.5f));
            _arrowGo.transform.rotation = U.RotationZ(-yaw);
            _arrowGo.transform.localScale = U.V3(size, size, 1f);
        }

        private void CopyProjection(Camera c)
        {
            c.orthographic = Main.orthographic;
            c.orthographicSize = Main.orthographicSize;
            c.fieldOfView = Main.fieldOfView;
            c.nearClipPlane = Main.nearClipPlane;
            c.farClipPlane = Main.farClipPlane;
            c.transparencySortMode = Main.transparencySortMode;
            c.transparencySortAxis = Main.transparencySortAxis;
            if (c == _uiCam) c.rect = Main.rect;
        }

        /// <summary>Place la caméra du sol (alignée sur les texels pour éviter le scintillement) et le sol 3D.</summary>
        public void UpdateFloor(float px, float py, in VisionParams v, int floorMask, Terrain ground)
        {
            float half = M.Clamp(ModConfig.FloorViewDistance.Value, 6f, 40f);
            if (MathF.Abs(half - _floorHalf) > 0.01f) BuildFloorMesh(half, Platform.IsMobile ? 48 : 72);

            float texel = 2f * half / _rtSize;
            float cx = MathF.Round(px / texel) * texel, cy = MathF.Round(py / texel) * texel;

            Vector3 mainPos = Main.transform.position;
            _floorCam.transform.position = U.V3(cx, cy, mainPos.z);
            _floorCam.transform.rotation = U.Quat(0f, 0f, 0f, 1f);
            _floorCam.orthographicSize = half;
            _floorCam.nearClipPlane = Main.nearClipPlane;
            _floorCam.farClipPlane = Main.farClipPlane;
            _floorCam.transparencySortMode = Main.transparencySortMode;
            _floorCam.transparencySortAxis = Main.transparencySortAxis;
            Color bg = Main.backgroundColor;
            bg.a = 1f;
            _floorCam.backgroundColor = bg;
            _floorCam.cullingMask = floorMask & ~(1 << Layer3D) & ~(1 << LayerMini) & (_autoFloor ? ~EntityLayersMask() : -1);
            if (_autoFloor) _floorCam.depth = Main.depth - 1f;

            _floorGo.transform.position = U.V3(cx, cy, 0f);
            FloorCenterX = cx;
            FloorCenterY = cy;

            // Relief : chaque sommet du sol monte à la hauteur du terrain ("haut" = -Z).
            bool raised = ground != null && ground.Active;
            if ((raised || _floorRaised) && (cx != _floorZx || cy != _floorZy || raised != _floorRaised))
            {
                _floorZx = cx; _floorZy = cy; _floorRaised = raised;
                for (int k = 0; k < _floorVerts.Length; k++)
                {
                    Vector3 p = _floorVerts[k];
                    p.z = raised ? -ground.Height(cx + p.x, cy + p.y) : 0f;
                    _floorVerts[k] = p;
                }
                _floorMesh.vertices = _floorVerts;
                _floorMesh.RecalculateBounds();
            }

            // Couleurs du sol : ne changent que si le rayon de vision change (sabotage, mort...).
            if (MathF.Abs(v.Radius - _lastRadius) > 0.01f || v.Full != _lastFull || MathF.Abs(v.Ambient - _lastAmbient) > 0.001f)
            {
                _lastRadius = v.Radius; _lastFull = v.Full; _lastAmbient = v.Ambient;
                float fadeStart = half * 0.6f, fadeEnd = half * 0.97f;
                for (int k = 0; k < _floorDist.Length; k++)
                {
                    byte c = U.ToByte(M.VisionBrightness(_floorDist[k], v.Radius, v.Full, v.Ambient, fadeStart, fadeEnd));
                    _floorColors[k] = U.C32(c, c, c);
                }
                _floorMesh.colors32 = _floorColors;
            }
        }

        // ------------------------------------------------------------------ flèches du conduit

        /// <summary>Flèche blanche bordée de noir, pointe vers +Y, posée à plat (le bord est juste en dessous).</summary>
        private static Mesh BuildFloorArrowMesh()
        {
            var m = new Mesh { name = "AU3D_VentArrow" };
            const float o = 1.32f; // bord noir : la même flèche, plus grande
            m.vertices = new[]
            {
                U.V3(0f, 0.56f * o, 0f), U.V3(-0.36f * o, -0.36f * o, 0f), U.V3(0f, -0.10f * o - 0.04f, 0f), U.V3(0.36f * o, -0.36f * o, 0f),
                U.V3(0f, 0.56f, -0.004f), U.V3(-0.36f, -0.36f, -0.004f), U.V3(0f, -0.10f, -0.004f), U.V3(0.36f, -0.36f, -0.004f),
            };
            Color32 dark = U.C32(20, 22, 28), white = U.C32(255, 255, 255), grey = U.C32(205, 214, 224);
            m.colors32 = new[] { dark, dark, dark, dark, white, grey, white, grey };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6 };
            m.RecalculateBounds();
            return m;
        }

        /// <summary>
        /// Dans un conduit : une flèche par conduit voisin, posée sur le sol à l'endroit de la flèche du jeu,
        /// pointée du conduit vers l'extérieur. Liste vide ou null = aucune flèche.
        /// </summary>
        public void UpdateVentArrows(System.Collections.Generic.List<Vector2> at, float cx, float cy, float pulse, float groundHeight = 0f)
        {
            int n = at != null ? at.Count : 0;
            if (n > 0 && _ventArrowMesh == null) _ventArrowMesh = BuildFloorArrowMesh();
            while (_ventArrows.Count < n)
            {
                var go = new GameObject("AU3D_VentArrow");
                go.layer = Layer3D;
                go.transform.SetParent(Root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = _ventArrowMesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = _arrowMat;
                r.sortingOrder = -29400; // sur le sol, sous les personnages
                _ventArrows.Add(go);
            }
            for (int i = 0; i < _ventArrows.Count; i++)
            {
                GameObject go = _ventArrows[i];
                if (!U.Alive(go)) continue;
                bool on = i < n;
                if (go.activeSelf != on) go.SetActive(on);
                if (!on) continue;
                Vector2 p = at[i];
                float dx = p.x - cx, dy = p.y - cy;
                float size = 0.42f * (1f + 0.08f * pulse);
                go.transform.position = U.V3(p.x, p.y, -0.02f - groundHeight);
                go.transform.rotation = U.RotationZ(-MathF.Atan2(dx, dy) / M.Deg2Rad);
                go.transform.localScale = U.V3(size, size, 1f);
            }
        }

        // ------------------------------------------------------------------ plafond

        /// <summary>Motif du plafond : 2 x 2 dalles métalliques à joints sombres, dont une porte un luminaire.</summary>
        private static Texture2D BuildCeilingTexture()
        {
            const int n = 128, t = 64;
            var px = new Il2CppStructArray<Color32>(n * n);
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int lx = x % t, ly = y % t, tile = x / t + 2 * (y / t);
                int edge = Math.Min(Math.Min(lx, ly), Math.Min(t - 1 - lx, t - 1 - ly));
                float k = edge < 1 ? 0.42f : (edge < 3 ? 1.14f : 1f);                 // joint sombre, biseau clair
                k *= tile == 0 ? 1f : (tile == 1 ? 0.93f : (tile == 2 ? 0.97f : 1.04f)); // dalles légèrement différentes
                float r = 0.30f * k, g = 0.33f * k, b = 0.39f * k;
                if (tile == 0)
                {
                    float dx = MathF.Abs(lx - 31.5f), dy = MathF.Abs(ly - 31.5f);
                    if (dx < 19f && dy < 6f) { r = 1f; g = 0.97f; b = 0.88f; }           // luminaire
                    else if (dx < 22f && dy < 9f) { r = 0.16f; g = 0.18f; b = 0.21f; }   // son cadre
                    else
                    {
                        // halo autour du luminaire
                        float ex = MathF.Max(0f, dx - 22f), ey = MathF.Max(0f, dy - 9f);
                        float glow = MathF.Max(0f, 1f - MathF.Sqrt(ex * ex + ey * ey) / 12f) * 0.28f;
                        r += glow; g += glow * 0.97f; b += glow * 0.85f;
                    }
                }
                px[y * n + x] = U.C32(U.ToByte(r), U.ToByte(g), U.ToByte(b));
            }
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, true) { name = "AU3D_Plafond" };
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 8;
            tex.SetPixels32(px);
            tex.Apply(true, false);
            return tex;
        }

        private void BuildCeiling(float half)
        {
            _ceilHalf = half;
            if (_ceilTex == null) _ceilTex = BuildCeilingTexture();
            if (_ceilMat == null) _ceilMat = new Material(SpriteShader) { name = "AU3D_CeilingMat", mainTexture = _ceilTex };
            if (_ceilGo == null)
            {
                _ceilGo = new GameObject("AU3D_Ceiling");
                _ceilGo.layer = Layer3D;
                _ceilGo.transform.SetParent(Root.transform, false);
                _ceilMesh = new Mesh { name = "AU3D_CeilingMesh" };
                _ceilGo.AddComponent<MeshFilter>().sharedMesh = _ceilMesh;
                var r = _ceilGo.AddComponent<MeshRenderer>();
                r.sharedMaterial = _ceilMat;
                r.sortingOrder = -29500; // après le sol, avant le dessin des murs et les personnages
            }
            // Le maillage se cale sur le motif (tous les CeilTile) : il déborde donc d'une période autour de la zone du sol.
            const float cell = 1f;
            int grid = 2 * (int)MathF.Ceiling((half + CeilTile) / cell);
            float size = grid * cell * 0.5f;
            int n = (grid + 1) * (grid + 1);
            var verts = new Vector3[n];
            var uvs = new Vector2[n];
            _ceilX = new float[n];
            _ceilY = new float[n];
            for (int j = 0; j <= grid; j++)
            for (int i = 0; i <= grid; i++)
            {
                int k = j * (grid + 1) + i;
                float x = -size + i * cell, y = -size + j * cell;
                verts[k] = U.V3(x, y, 0f);
                uvs[k] = U.V2(x / CeilTile, y / CeilTile);
                _ceilX[k] = x; _ceilY[k] = y;
            }
            var tris = new int[grid * grid * 6];
            int t = 0;
            for (int j = 0; j < grid; j++)
            for (int i = 0; i < grid; i++)
            {
                int a = j * (grid + 1) + i, b = a + 1, c = a + grid + 1, d = c + 1;
                tris[t++] = a; tris[t++] = b; tris[t++] = c;
                tris[t++] = b; tris[t++] = d; tris[t++] = c;
            }
            _ceilColors = new Il2CppStructArray<Color32>(n);
            _ceilMesh.Clear();
            _ceilMesh.vertices = verts;
            _ceilMesh.uv = uvs;
            _ceilMesh.colors32 = _ceilColors;
            _ceilMesh.triangles = tris;
            _ceilMesh.RecalculateBounds();
            _ceilMesh.MarkDynamic();
        }

        /// <summary>Plafond au-dessus des murs, éclairé comme le sol (sombre hors du rayon de vision).</summary>
        public void UpdateCeiling(bool show, float px, float py, float height, in VisionParams v)
        {
            if (!show)
            {
                if (_ceilGo != null && _ceilGo.activeSelf) _ceilGo.SetActive(false);
                return;
            }
            if (_ceilGo == null || MathF.Abs(_ceilHalf - _floorHalf) > 0.01f) BuildCeiling(_floorHalf);
            if (!_ceilGo.activeSelf) _ceilGo.SetActive(true);
            float cx = MathF.Round(px / CeilTile) * CeilTile, cy = MathF.Round(py / CeilTile) * CeilTile;
            _ceilGo.transform.position = U.V3(cx, cy, -height);
            float fadeStart = _floorHalf * 0.6f, fadeEnd = _floorHalf * 0.97f;
            for (int k = 0; k < _ceilX.Length; k++)
            {
                float dx = _ceilX[k] + cx - px, dy = _ceilY[k] + cy - py;
                byte c = U.ToByte(0.94f * M.VisionBrightness(MathF.Sqrt(dx * dx + dy * dy), v.Radius, v.Full, v.Ambient, fadeStart, fadeEnd));
                _ceilColors[k] = U.C32(c, c, c);
            }
            _ceilMesh.colors32 = _ceilColors;
        }

        /// <summary>Point du monde 3D vu par la caméra 3D : coordonnées écran (0..1), vrai s'il est devant la caméra.</summary>
        public bool Project(float x, float y, float z, out float vx, out float vy)
        {
            Vector3 v = _cam3D.WorldToViewportPoint(U.V3(x, y, z));
            vx = v.x;
            vy = v.y;
            return v.z > 0.3f;
        }

        /// <summary>Point du sol (z = 0) visé à l'écran (pixels) dans la vue 3D. Faux si le rayon ne touche pas le sol.</summary>
        public bool ScreenToFloor(float sx, float sy, float groundHeight, out float x, out float y)
        {
            x = y = 0f;
            Ray r = _cam3D.ScreenPointToRay(U.V3(sx, sy, 0f));
            Vector3 o = r.origin, d = r.direction;
            if (MathF.Abs(d.z) < 1e-5f) return false;
            float t = (-groundHeight - o.z) / d.z;
            if (t <= 0f) return false;
            x = o.x + d.x * t;
            y = o.y + d.y * t;
            return true;
        }

        /// <summary>Cadre de l'interface (caméra 2D du jeu) : centre et demi-dimensions, en unités du monde.</summary>
        public void UiFrame(out float cx, out float cy, out float halfW, out float halfH)
        {
            Vector3 c = Main.transform.position;
            cx = c.x;
            cy = c.y;
            halfH = Main.orthographicSize;
            halfW = halfH * Main.aspect;
        }

        public float FloorHalf => _floorHalf;
        public float FloorCenterX { get; private set; }
        public float FloorCenterY { get; private set; }
        /// <summary>Matériau du sol (texture de la carte vue de dessus), réutilisé par les faces de murs redressées.</summary>
        public Material FloorMaterial => _floorMat;

        /// <summary>Rend la texture du sol en masquant un instant les personnages d'origine.</summary>
        public void RenderFloor(ProxySystem proxies)
        {
            if (_autoFloor) return; // mode de secours : la caméra du sol se rend toute seule
            proxies.HideSourcesForFloor();
            // L'obscurité 2D du jeu ne doit pas être "peinte" sur le sol (la 3D a sa propre vision).
            MeshRenderer shadow = Game.ShadowQuad;
            bool shadowWasOn = false;
            try { shadowWasOn = shadow != null && shadow.enabled; if (shadowWasOn) shadow.enabled = false; }
            catch { shadowWasOn = false; }
            try
            {
                _floorCam.Render();
            }
            catch (Exception e)
            {
                // Rendu manuel impossible : rendu automatique, personnages exclus par calque.
                Safe.Report("Camera.Render", e);
                _autoFloor = true;
                _floorCam.enabled = true;
            }
            finally
            {
                proxies.RestoreHidden();
                if (shadowWasOn)
                {
                    try { shadow.enabled = true; } catch { /* ignoré */ }
                }
            }
        }

        private bool _autoFloor;
        private int _entityLayersMask = -1;

        private int EntityLayersMask()
        {
            if (_entityLayersMask < 0) _entityLayersMask = U.MaskFromNames("Players,Ghost");
            return _entityLayersMask;
        }
    }
}
