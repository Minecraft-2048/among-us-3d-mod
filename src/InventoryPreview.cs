using System;
using System.Collections.Generic;
using UnityEngine;

namespace AmongUs3D
{
    /// <summary>
    /// Inventaire (menu de personnalisation du jeu) : l'aperçu du personnage peut être montré en 3D.
    /// Un modèle 3D (couleurs du joueur, chapeau, visière et combinaison en volume) est rendu par une petite caméra
    /// dans une image, affichée à la place de l'aperçu 2D, et tourne doucement sur lui-même.
    /// La touche "Basculer3D" (F5) passe de la 3D à la 2D tant que le menu est ouvert ; le choix est retenu.
    /// </summary>
    internal sealed class InventoryPreview
    {
        private const int RtSize = 512;
        private const float Fov = 28f, Dist = 3.2f, LookZ = 0.62f; // caméra : à 3,2 du modèle, visant 0,62 au-dessus des pieds
        private const float Spot = 7000f;                           // le modèle est posé loin de tout le reste

        private bool _configRead, _on, _broken;
        private int _layer = -1;
        private GameObject _camGo, _modelRoot, _quadGo;
        private Camera _cam;
        private RenderTexture _rt;
        private Material _quadMat;
        private Mesh _quadMesh;
        private MeshRenderer _quadR;
        private CrewKit _kit;
        private CrewModel _model;
        private HatCapture _capture;
        private HatMeshes _hat, _visor;
        private SkinPaint _skin;
        private string _signature = "";
        private float _nextBake, _spin;
        private Vector3 _localFeet, _localTop;
        private readonly List<SpriteRenderer> _hidden = new List<SpriteRenderer>();
        private readonly HashSet<IntPtr> _hiddenKeys = new HashSet<IntPtr>();

        /// <summary>Vrai tant que le menu de personnalisation est ouvert (F5 bascule alors l'aperçu).</summary>
        public bool MenuOpen { get; private set; }

        public void Toggle()
        {
            _on = !_on;
            ModConfig.Inventory3D.Value = _on;
            AmongUs3DPlugin.Logger.LogInfo("Inventaire : aperçu du personnage en " + (_on ? "3D" : "2D"));
        }

        public void Update(float dt, int avoidA, int avoidB)
        {
            if (!_configRead) { _configRead = true; _on = ModConfig.Inventory3D.Value; }
            MenuOpen = Game.CustomizationMenuOpen(out CosmeticsLayer cosmetics, out Transform preview);
            if (!MenuOpen || !_on || _broken || cosmetics == null || preview == null || !preview.gameObject.activeInHierarchy)
            {
                Hide();
                return;
            }
            try
            {
                Show(cosmetics, preview, dt, avoidA, avoidB);
            }
            catch (Exception e)
            {
                _broken = true;
                Hide();
                AmongUs3DPlugin.Logger.LogWarning("Aperçu 3D de l'inventaire indisponible (aperçu 2D normal) : " + e);
            }
        }

        private void Show(CosmeticsLayer cosmetics, Transform preview, float dt, int avoidA, int avoidB)
        {
            if (!Game.TryCosmeticsInfo(cosmetics, out BodyInfo info) || info.Body == null)
            {
                Hide();
                return;
            }
            Ensure(avoidA, avoidB);

            float scaleX = info.Body.transform.lossyScale.x;
            string sig = info.ColorId + "|" + Accessories.SpriteKey(info.HatFront) + "|" + Accessories.SpriteKey(info.HatBack) + "|"
                         + Accessories.SpriteKey(info.Visor) + "|" + Accessories.SpriteKey(info.Skin) + "|" + info.Body.flipX + "|" + (scaleX < 0f);
            if (sig != _signature && Time.unscaledTime >= _nextBake)
            {
                _signature = sig;
                _nextBake = Time.unscaledTime + 0.3f;
                RestoreSprites(); // la photo et la taille du dessin demandent les calques affichés
                Bake(info, scaleX, preview);
            }

            _spin += dt * 40f;
            float a = _spin * M.Deg2Rad;
            _model.SetColor(info.ColorId);
            _model.SetHat(_hat?.Solid, _hat?.Outline);
            _model.SetVisor(_visor?.Solid, _visor?.Outline);
            _model.SetSkin(_skin);
            _model.Update(Spot, Spot, 1f, MathF.Sin(a), -MathF.Cos(a), false, dt, 1f);
            _model.Show(true);
            if (!_camGo.activeSelf) _camGo.SetActive(true);
            PlaceQuad(preview, info.Body);
            HideSprites(preview);
        }

        // ------------------------------------------------------------------ fabrication

        private void Bake(BodyInfo info, float scaleX, Transform preview)
        {
            if (_capture == null) _capture = new HatCapture(_layer);
            _hat?.Destroy();
            _visor?.Destroy();
            _hat = Accessories.Puff(_capture, info.HatBack, info.HatFront, info.Body, scaleX, float.NaN, HatShape.HalfThickness, "AU3D_InvChapeau");
            _visor = Accessories.Puff(_capture, null, info.Visor, info.Body, scaleX, float.NaN, Accessories.VisorHalfThickness, "AU3D_InvVisiere");
            _skin = Accessories.PaintSkin(_capture, info.Skin, info.Body, scaleX);

            // Pieds et sommet du dessin, dans le repère de l'aperçu (qui peut bouger avec le menu).
            Bounds b = info.Body.bounds;
            Vector3 c = b.center, mn = b.min, mx = b.max;
            _localFeet = preview.InverseTransformPoint(U.V3(c.x, mn.y, c.z));
            _localTop = preview.InverseTransformPoint(U.V3(c.x, mx.y, c.z));
        }

        private void Ensure(int avoidA, int avoidB)
        {
            if (_layer < 0)
            {
                var avoid = new HashSet<int>();
                if (avoidA >= 0) avoid.Add(avoidA);
                if (avoidB >= 0) avoid.Add(avoidB);
                _layer = U.PickFreeLayer(-1, avoid);
            }
            if (_kit == null) _kit = CrewKit.Create();
            if (_rt == null)
            {
                _rt = new RenderTexture(RtSize, RtSize, 16) { name = "AU3D_Inventaire" };
                _rt.antiAliasing = 4;
                _rt.Create();
            }
            if (!U.Alive(_camGo))
            {
                _camGo = new GameObject("AU3D_InventaireCamera");
                UnityEngine.Object.DontDestroyOnLoad(_camGo);
                _cam = _camGo.AddComponent<Camera>();
                _cam.orthographic = false;
                _cam.fieldOfView = Fov;
                _cam.clearFlags = CameraClearFlags.SolidColor;
                _cam.backgroundColor = U.Col(0f, 0f, 0f, 0f);
                _cam.cullingMask = 1 << _layer;
                _cam.nearClipPlane = 0.05f;
                _cam.farClipPlane = 50f;
                _cam.useOcclusionCulling = false;
                _cam.allowHDR = false;
                _cam.depth = -50f;
                _cam.targetTexture = _rt;
                _camGo.transform.position = U.V3(Spot, Spot - Dist, -LookZ);
                _camGo.transform.rotation = U.LookRotation(0f, 1f, 0f, 0f, 0f, -1f);
            }
            if (_model == null || !U.Alive(_model.Root))
            {
                _model?.Destroy();
                if (!U.Alive(_modelRoot))
                {
                    _modelRoot = new GameObject("AU3D_InventaireModele");
                    _modelRoot.layer = _layer;
                    UnityEngine.Object.DontDestroyOnLoad(_modelRoot);
                }
                _model = CrewModel.Create(_kit, _modelRoot.transform, _layer, ModelKind.Crew);
                _signature = "";
            }
            if (_quadMesh == null)
            {
                _quadMesh = new Mesh { name = "AU3D_InventaireImage" };
                _quadMesh.vertices = new[] { U.V3(-0.5f, -0.5f, 0f), U.V3(0.5f, -0.5f, 0f), U.V3(0.5f, 0.5f, 0f), U.V3(-0.5f, 0.5f, 0f) };
                _quadMesh.uv = new[] { U.V2(0f, 0f), U.V2(1f, 0f), U.V2(1f, 1f), U.V2(0f, 1f) };
                var white = U.C32(255, 255, 255);
                _quadMesh.colors32 = new[] { white, white, white, white };
                _quadMesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
                _quadMesh.RecalculateBounds();
            }
            if (_quadMat == null)
            {
                _quadMat = new Material(Shader.Find("Sprites/Default")) { name = "AU3D_Inventaire" };
                _quadMat.mainTexture = _rt;
            }
            if (!U.Alive(_quadGo))
            {
                _quadGo = new GameObject("AU3D_InventaireApercu3D");
                _quadGo.AddComponent<MeshFilter>().sharedMesh = _quadMesh;
                _quadR = _quadGo.AddComponent<MeshRenderer>();
                _quadR.sharedMaterial = _quadMat;
            }
        }

        /// <summary>L'image du modèle 3D, posée sur l'aperçu 2D : mêmes pieds, même taille de personnage.</summary>
        private void PlaceQuad(Transform preview, SpriteRenderer body)
        {
            Vector3 feet = preview.TransformPoint(_localFeet), top = preview.TransformPoint(_localTop);
            float h = MathF.Abs(top.y - feet.y);
            if (h < 1e-3f) h = 1f;
            float side = h * 2f * Dist * MathF.Tan(Fov * 0.5f * M.Deg2Rad); // hauteur de l'image, en taille de personnage
            _quadGo.layer = preview.gameObject.layer;
            Transform t = _quadGo.transform;
            t.position = U.V3(feet.x, feet.y + LookZ * h, feet.z - 0.05f);
            t.rotation = U.Quat(0f, 0f, 0f, 1f);
            t.localScale = U.V3(side, side, 1f);
            try
            {
                _quadR.sortingLayerID = body.sortingLayerID;
                _quadR.sortingOrder = body.sortingOrder + 50;
            }
            catch { /* ordre par défaut */ }
            if (!_quadGo.activeSelf) _quadGo.SetActive(true);
        }

        // ------------------------------------------------------------------ aperçu 2D masqué

        private void HideSprites(Transform preview)
        {
            foreach (SpriteRenderer r in preview.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (r == null || !r.enabled) continue;
                string n = r.gameObject.name ?? "";
                if (n.IndexOf("shadow", StringComparison.OrdinalIgnoreCase) >= 0) continue; // l'ombre reste sous le modèle
                if (Game.BelongsToPet(r)) continue;                                           // l'animal reste en 2D ici
                r.enabled = false;
                if (_hiddenKeys.Add(r.Pointer)) _hidden.Add(r);
            }
        }

        private void RestoreSprites()
        {
            foreach (SpriteRenderer r in _hidden)
            {
                try { if (U.Alive(r)) r.enabled = true; } catch { /* détruit avec le menu */ }
            }
            _hidden.Clear();
            _hiddenKeys.Clear();
        }

        private void Hide()
        {
            RestoreSprites();
            _model?.Show(false);
            if (U.Alive(_camGo) && _camGo.activeSelf) _camGo.SetActive(false);
            if (U.Alive(_quadGo) && _quadGo.activeSelf) _quadGo.SetActive(false);
        }
    }
}
