using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace AmongUs3D
{
    /// <summary>
    /// Photographie, dans une petite image, le dessin d'un chapeau tel que le jeu l'affiche (calques avant et
    /// arrière, couleurs du joueur comprises), pour que <see cref="HatShape"/> en fasse un objet 3D.
    /// Une caméra dédiée, désactivée, rend une seule fois des copies des deux calques sur un calque Unity libre.
    /// </summary>
    internal sealed class HatCapture
    {
        public const int Size = 96; // assez fin pour garder les détails du dessin (le jeu les dessine à peu près à cette taille)

        private readonly int _layer;
        private GameObject _camGo, _spriteRoot;
        private Camera _cam;
        private RenderTexture _rt;
        private Texture2D _tex;
        private SpriteRenderer _back, _front;
        private Material _backMat, _frontMat;
        private MaterialPropertyBlock _mpb;

        public HatCapture(int layer) { _layer = layer; }

        private void Ensure()
        {
            if (_cam != null && U.Alive(_cam) && U.Alive(_spriteRoot)) return;
            Destroy();
            _camGo = new GameObject("AU3D_PhotoChapeau");
            _cam = _camGo.AddComponent<Camera>();
            _cam.enabled = false;
            _cam.orthographic = true;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = U.Col(0f, 0f, 0f, 0f);
            _cam.cullingMask = 1 << _layer;
            _cam.useOcclusionCulling = false;
            _cam.allowHDR = false;
            _cam.allowMSAA = false;
            _cam.nearClipPlane = 0.01f;
            _cam.farClipPlane = 500f;
            _rt = new RenderTexture(Size, Size, 16) { name = "AU3D_PhotoChapeau" };
            _rt.filterMode = FilterMode.Point;
            _rt.Create();
            _cam.targetTexture = _rt;
            _tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { name = "AU3D_PixelsChapeau" };
            _spriteRoot = new GameObject("AU3D_PhotoChapeauDessins");
            _spriteRoot.layer = _layer;
            _back = NewSprite("arriere");
            _front = NewSprite("avant");
            _spriteRoot.SetActive(false);
        }

        private SpriteRenderer NewSprite(string name)
        {
            var go = new GameObject(name);
            go.layer = _layer;
            go.transform.SetParent(_spriteRoot.transform, false);
            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            try { sr.maskInteraction = SpriteMaskInteraction.None; } catch { /* ignoré */ }
            return sr;
        }

        private static bool Usable(SpriteRenderer r)
        {
            try { return r != null && r.enabled && r.gameObject.activeInHierarchy && r.sprite != null; }
            catch { return false; }
        }

        /// <summary>
        /// Photographie les deux calques. <paramref name="minX"/>, <paramref name="minY"/> = coin bas-gauche de
        /// l'image dans le monde, <paramref name="cell"/> = taille d'un pixel dans le monde.
        /// </summary>
        public bool TryCapture(SpriteRenderer back, SpriteRenderer front, out byte[] rgba, out float minX, out float minY, out float cell)
        {
            rgba = null;
            minX = minY = cell = 0f;
            bool hasBack = Usable(back), hasFront = Usable(front);
            if (!hasBack && !hasFront) return false;
            Ensure();

            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue, z0 = float.MaxValue;
            if (hasBack) Grow(back, ref x0, ref y0, ref x1, ref y1, ref z0);
            if (hasFront) Grow(front, ref x0, ref y0, ref x1, ref y1, ref z0);
            float span = MathF.Max(x1 - x0, y1 - y0) * 1.08f + 1e-3f;
            if (span > 20f) return false;
            cell = span / Size;
            float cx = (x0 + x1) * 0.5f, cy = (y0 + y1) * 0.5f;
            minX = cx - span * 0.5f;
            minY = cy - span * 0.5f;

            Copy(back, _back, hasBack, ref _backMat, 0);
            Copy(front, _front, hasFront, ref _frontMat, 1);
            _cam.orthographicSize = span * 0.5f;
            _cam.transform.position = U.V3(cx, cy, z0 - 100f);
            _cam.transform.rotation = U.Quat(0f, 0f, 0f, 1f);

            RenderTexture prev = RenderTexture.active;
            _spriteRoot.SetActive(true);
            try
            {
                _cam.Render();
                RenderTexture.active = _rt;
                _tex.ReadPixels(new Rect(0f, 0f, Size, Size), 0, 0);
                _tex.Apply(false);
            }
            finally
            {
                RenderTexture.active = prev;
                _spriteRoot.SetActive(false);
            }

            Il2CppStructArray<Color32> px = _tex.GetPixels32();
            int n = Size * Size;
            if (px == null || px.Length < n) return false;
            rgba = new byte[n * 4];
            for (int k = 0; k < n; k++)
            {
                Color32 c = px[k];
                rgba[k * 4] = c.r;
                rgba[k * 4 + 1] = c.g;
                rgba[k * 4 + 2] = c.b;
                rgba[k * 4 + 3] = c.a;
            }
            return true;
        }

        private static void Grow(SpriteRenderer r, ref float x0, ref float y0, ref float x1, ref float y1, ref float z0)
        {
            Bounds b = r.bounds;
            Vector3 mn = b.min, mx = b.max;
            x0 = MathF.Min(x0, mn.x); y0 = MathF.Min(y0, mn.y);
            x1 = MathF.Max(x1, mx.x); y1 = MathF.Max(y1, mx.y);
            z0 = MathF.Min(z0, mn.z);
        }

        private void Copy(SpriteRenderer src, SpriteRenderer dst, bool has, ref Material own, int order)
        {
            if (!has)
            {
                dst.enabled = false;
                return;
            }
            dst.enabled = true;
            dst.sprite = src.sprite;
            Material sm = src.sharedMaterial;
            if (sm != null && ProxySystem.NeedsStencilOverride(sm))
            {
                if (own == null || own.shader != sm.shader)
                {
                    U.SafeDestroy(own);
                    own = new Material(sm);
                }
                else
                {
                    own.CopyPropertiesFromMaterial(sm);
                }
                ProxySystem.ApplyStencilOverride(own);
                dst.sharedMaterial = own;
            }
            else
            {
                dst.sharedMaterial = sm;
            }
            Color c = src.color;
            c.a = 1f;
            dst.color = c;
            dst.flipX = src.flipX;
            dst.flipY = src.flipY;
            try
            {
                if (src.HasPropertyBlock())
                {
                    if (_mpb == null) _mpb = new MaterialPropertyBlock();
                    src.GetPropertyBlock(_mpb);
                    dst.SetPropertyBlock(_mpb);
                }
                else
                {
                    dst.SetPropertyBlock(null);
                }
            }
            catch { /* couleurs du joueur indisponibles : dessin tel quel */ }
            Transform st = src.transform, tt = dst.transform;
            tt.position = st.position;
            tt.rotation = st.rotation;
            tt.localScale = st.lossyScale;
            dst.sortingLayerID = src.sortingLayerID;
            dst.sortingOrder = order;
        }

        public void Destroy()
        {
            U.SafeDestroy(_camGo);
            U.SafeDestroy(_spriteRoot);
            if (U.Alive(_rt))
            {
                try { _rt.Release(); } catch { /* ignoré */ }
                U.SafeDestroy(_rt);
            }
            U.SafeDestroy(_tex);
            U.SafeDestroy(_backMat);
            U.SafeDestroy(_frontMat);
            _cam = null;
            _camGo = _spriteRoot = null;
            _rt = null;
            _tex = null;
            _backMat = _frontMat = null;
        }
    }
}
