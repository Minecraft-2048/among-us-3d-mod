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
        private float _lastRadius = -1f, _lastAmbient = -1f;
        private bool _lastFull;
        private GameObject _arrowGo;
        private Mesh _arrowMesh;
        private Material _arrowMat;
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
        public void UpdateFloor(float px, float py, in VisionParams v, int floorMask)
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

        /// <summary>Point du monde 3D vu par la caméra 3D : coordonnées écran (0..1), vrai s'il est devant la caméra.</summary>
        public bool Project(float x, float y, float z, out float vx, out float vy)
        {
            Vector3 v = _cam3D.WorldToViewportPoint(U.V3(x, y, z));
            vx = v.x;
            vy = v.y;
            return v.z > 0.3f;
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
