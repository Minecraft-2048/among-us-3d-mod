using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace AmongUs3D
{
    /// <summary>
    /// Outil de mise au point (réglage OutilsDeTest) : dans l'inventaire, fait défiler les chapeaux, visières,
    /// combinaisons ou animaux du jeu sur l'aperçu (sans rien équiper), et enregistre pour chacun une image du modèle 3D
    /// sous trois angles (trois quarts avant, profil, trois quarts arrière), pour repérer ceux qui rendent mal.
    /// Images : BepInEx/AmongUs3D-captures/catalogue/&lt;sorte&gt;/&lt;numéro&gt;_&lt;identifiant&gt;.png
    /// </summary>
    internal sealed partial class InventoryPreview
    {
        private enum CatStep { Apply, Load, Settle, Render }

        private const int CatSize = 256;
        private static readonly float[] CatAngles = { 35f, 90f, 145f };

        private bool _catActive;
        private string _catKind = "", _catDir = "", _catId = "", _catName = "";
        private int _catStart, _catNext, _catEnd, _catDone, _catFrames;
        private CatStep _catStep;
        private float _catDeadline;
        private RenderTexture _catRt;
        private Texture2D _catTex;
        private StringBuilder _catList;
        private HatMeshes _catPet;          // animal du catalogue, gonflé
        private PetModel _catPetModel;
        private GameObject _catPetRoot;     // parent mis à l'échelle : tous les animaux ont la même taille à l'image

        /// <summary>Lance le catalogue : "chapeaux", "visieres" ou "combinaisons", à partir de <paramref name="start"/>.</summary>
        public bool StartCatalog(string kind, int start, int count)
        {
            kind = (kind ?? "").ToLowerInvariant();
            if (kind.StartsWith("hat") || kind.StartsWith("chap")) kind = "chapeaux";
            else if (kind.StartsWith("vis")) kind = "visieres";
            else if (kind.StartsWith("skin") || kind.StartsWith("comb")) kind = "combinaisons";
            else if (kind.StartsWith("pet") || kind.StartsWith("anim")) kind = "animaux";
            else
            {
                AmongUs3DPlugin.Logger.LogWarning("Catalogue : sorte inconnue « " + kind + " » (chapeaux, visieres, combinaisons ou animaux).");
                return false;
            }
            if (!MenuOpen)
            {
                AmongUs3DPlugin.Logger.LogWarning("Catalogue : ouvre d'abord l'inventaire.");
                return false;
            }
            int total = CatalogCount(kind);
            if (total <= 0)
            {
                AmongUs3DPlugin.Logger.LogWarning("Catalogue : liste des " + kind + " introuvable.");
                return false;
            }
            _catKind = kind;
            _catStart = _catNext = Math.Max(0, Math.Min(start, total));
            _catEnd = Math.Min(total, _catNext + Math.Max(1, count));
            _catDir = Path.Combine(BepInEx.Paths.BepInExRootPath, "AmongUs3D-captures", "catalogue", kind);
            Directory.CreateDirectory(_catDir);
            _catList = new StringBuilder();
            _catDone = 0;
            _catStep = CatStep.Apply;
            _catActive = true;
            AmongUs3DPlugin.Logger.LogInfo($"Catalogue : {kind} {_catNext} à {_catEnd - 1} (sur {total}), images dans {_catDir}");
            return true;
        }

        private static int CatalogCount(string kind)
        {
            HatManager hm = HatManager.Instance;
            if (hm == null) return -1;
            switch (kind)
            {
                case "chapeaux": return hm.allHats?.Length ?? -1;
                case "visieres": return hm.allVisors?.Length ?? -1;
                case "animaux": return hm.allPets?.Length ?? -1;
                default: return hm.allSkins?.Length ?? -1;
            }
        }

        private void AbortCatalog(string why)
        {
            if (!_catActive) return;
            _catActive = false;
            AmongUs3DPlugin.Logger.LogWarning("Catalogue interrompu (" + why + ") après " + _catDone + " images.");
            FinishCatalogFiles();
        }

        /// <summary>Une étape du catalogue par image (appelé par <see cref="Show"/>, menu ouvert, modèle prêt).</summary>
        private void CatalogStep(BodyInfo info, float scaleX, Transform preview)
        {
            PlayerCustomizationMenu menu = PlayerCustomizationMenu.Instance;
            PoolablePlayer pp = menu != null ? menu.PreviewArea : null;
            if (pp == null) { AbortCatalog("aperçu introuvable"); return; }
            CosmeticsLayer c = pp.cosmetics;
            float now = Time.unscaledTime;

            switch (_catStep)
            {
                case CatStep.Apply:
                {
                    if (_catNext >= _catEnd) { EndCatalog(pp); return; }
                    if (!ApplyItem(pp, info.ColorId)) { _catNext++; return; } // emplacement vide : suivant
                    _catStep = CatStep.Load;
                    _catFrames = 0;
                    _catDeadline = now + 8f;
                    return;
                }
                case CatStep.Load:
                {
                    if (++_catFrames < 3) return; // laisser le jeu commencer le chargement
                    bool loaded;
                    if (_catKind == "chapeaux") loaded = c != null && c.hat != null && c.hat.IsLoaded;
                    else if (_catKind == "visieres") loaded = c != null && c.HasVisorLoaded();
                    else if (_catKind == "animaux") loaded = MainPetRenderer(c, _catId) != null;
                    else loaded = c != null && c.HasSkinLoaded();
                    if (!loaded && now < _catDeadline) return;
                    if (!loaded) AmongUs3DPlugin.Logger.LogWarning("Catalogue : " + _catId + " pas chargé à temps.");
                    _catStep = CatStep.Settle;
                    _catFrames = 0;
                    return;
                }
                case CatStep.Settle:
                {
                    if (++_catFrames < 4) return; // dessins posés et placés par le jeu
                    RestoreSprites();             // la photo a besoin des calques 2D affichés
                    if (_catKind == "chapeaux") ExportProfile(info, scaleX);
                    if (_catKind == "animaux")
                    {
                        _catPet?.Destroy();
                        SpriteRenderer petR = MainPetRenderer(c, _catId);
                        _catPet = petR != null ? Accessories.PuffPet(_capture ??= new HatCapture(_layer), petR, petR.transform.lossyScale.x) : null;
                    }
                    else Bake(info, scaleX, preview);
                    _signature = "catalogue";
                    _catStep = CatStep.Render;
                    return;
                }
                default:
                {
                    RenderItem(info.ColorId);
                    _catNext++;
                    _catStep = CatStep.Apply;
                    return;
                }
            }
        }

        /// <summary>Met l'objet n° _catNext sur l'aperçu (seul : les autres emplacements sont vidés). Faux si vide.</summary>
        private bool ApplyItem(PoolablePlayer pp, int color)
        {
            HatManager hm = HatManager.Instance;
            if (hm == null) return false;
            CosmeticData d;
            switch (_catKind)
            {
                case "chapeaux":
                {
                    HatData h = hm.allHats[_catNext];
                    if (h == null || h.IsEmpty) return false;
                    pp.SetVisor(CosmeticsLayer.EMPTY_VISOR_ID, color);
                    pp.SetSkin(CosmeticsLayer.EMPTY_SKIN_ID, color);
                    pp.SetHat(h, color);
                    d = h;
                    break;
                }
                case "visieres":
                {
                    VisorData v = hm.allVisors[_catNext];
                    if (v == null || v.IsEmpty) return false;
                    pp.SetHat(CosmeticsLayer.EMPTY_HAT_ID, color);
                    pp.SetSkin(CosmeticsLayer.EMPTY_SKIN_ID, color);
                    pp.SetVisor(v, color);
                    d = v;
                    break;
                }
                case "animaux":
                {
                    PetData pd = hm.allPets[_catNext];
                    if (pd == null || pd.IsEmpty) return false;
                    pp.SetHat(CosmeticsLayer.EMPTY_HAT_ID, color);
                    pp.SetVisor(CosmeticsLayer.EMPTY_VISOR_ID, color);
                    pp.SetSkin(CosmeticsLayer.EMPTY_SKIN_ID, color);
                    pp.SetPetIdle(pd, color);
                    d = pd;
                    break;
                }
                default:
                {
                    SkinData s = hm.allSkins[_catNext];
                    if (s == null || s.IsEmpty) return false;
                    pp.SetHat(CosmeticsLayer.EMPTY_HAT_ID, color);
                    pp.SetVisor(CosmeticsLayer.EMPTY_VISOR_ID, color);
                    pp.SetSkin(s, color);
                    d = s;
                    break;
                }
            }
            _catId = d.ProductId ?? "?";
            try { _catName = d.GetItemName() ?? ""; } catch { _catName = ""; }
            return true;
        }

        /// <summary>Trois vues du modèle 3D, côte à côte, enregistrées en PNG.</summary>
        private void RenderItem(int colorId)
        {
            if (_catRt == null)
            {
                _catRt = new RenderTexture(CatSize, CatSize, 24) { name = "AU3D_Catalogue" };
                _catRt.antiAliasing = 4;
                _catRt.Create();
            }
            if (_catTex == null) _catTex = new Texture2D(CatSize * CatAngles.Length, CatSize, TextureFormat.RGBA32, false) { name = "AU3D_Catalogue" };

            bool pet = _catKind == "animaux";
            if (pet)
            {
                if (_catPet == null) { AmongUs3DPlugin.Logger.LogWarning("Catalogue : animal " + _catId + " sans volume."); return; }
                if (!U.Alive(_catPetRoot))
                {
                    _catPetRoot = new GameObject("AU3D_CatalogueAnimal");
                    _catPetRoot.layer = _layer;
                    UnityEngine.Object.DontDestroyOnLoad(_catPetRoot);
                }
                if (_catPetModel == null || !_catPetModel.Alive) _catPetModel = PetModel.Create(_kit, _catPetRoot.transform, _layer);
                Bounds pb = _catPet.Solid.bounds;
                float size = MathF.Max(pb.size.z, MathF.Max(pb.size.x, pb.size.y));
                float k = size > 1e-3f ? 0.8f / size : 1f; // même taille à l'image pour tous les animaux
                _catPetRoot.transform.localScale = U.V3(k, k, k);
                _catPetModel.SetMesh(_catPet);
                _model.Show(false);
            }
            else
            {
                _model.SetColor(colorId);
                _model.SetHat(_hat?.Solid, _hat?.Outline);
                _model.SetVisor(_visor?.Solid, _visor?.Outline);
                _model.SetSkin(_skin);
                _model.Show(true);
            }

            // Cadrage plus large que l'aperçu : les grands chapeaux dépassent la tête.
            Transform ct = _camGo.transform;
            Vector3 pos = ct.position;
            float fov = _cam.fieldOfView;
            Color bg = _cam.backgroundColor;
            RenderTexture target = _cam.targetTexture, prev = RenderTexture.active;
            try
            {
                ct.position = U.V3(Spot, Spot - 4.4f, -0.78f);
                _cam.fieldOfView = 30f;
                _cam.backgroundColor = U.Col(0.42f, 0.45f, 0.5f, 1f);
                _cam.targetTexture = _catRt;
                for (int v = 0; v < CatAngles.Length; v++)
                {
                    float a = CatAngles[v] * M.Deg2Rad;
                    if (pet)
                    {
                        _catPetModel.Update(Spot, Spot, MathF.Sin(a), -MathF.Cos(a), false, 10f, 1f);
                        _catPetModel.Show(true);
                    }
                    else _model.Update(Spot, Spot, 1f, MathF.Sin(a), -MathF.Cos(a), false, 10f, 1f); // dt long : tourné d'un coup
                    _cam.Render();
                    RenderTexture.active = _catRt;
                    _catTex.ReadPixels(new Rect(0f, 0f, CatSize, CatSize), v * CatSize, 0);
                }
                _catTex.Apply(false);
            }
            finally
            {
                if (pet) { _catPetModel?.Show(false); _model.Show(true); }
                RenderTexture.active = prev;
                _cam.targetTexture = target;
                _cam.backgroundColor = bg;
                _cam.fieldOfView = fov;
                ct.position = pos;
            }

            var png = ImageConversion.EncodeToPNG(_catTex);
            var bytes = new byte[png.Length];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = png[i];
            string safe = MakeSafe(_catId);
            File.WriteAllBytes(Path.Combine(_catDir, $"{_catNext:0000}_{safe}.png"), bytes);
            _catList.Append(_catNext).Append('\t').Append(_catId).Append('\t').Append(_catName)
                    .Append('\t').Append(_hat != null ? "chapeau3D" : "-").Append('\t').Append(_visor != null ? "visiere3D" : "-")
                    .Append('\t').Append(_skin != null ? "combinaison3D" : "-").Append('\t').Append(pet ? "animal3D" : "-").Append('\n');
            _catDone++;
        }

        /// <summary>
        /// Fichier .hat : "AUH1", largeur, hauteur (entiers), y0, cellY, z0, cellZ (réels), puis les pixels RGBA,
        /// ligne du bas en premier : de quoi refaire la forme 3D du chapeau hors du jeu.
        /// </summary>
        private void ExportProfile(BodyInfo info, float scaleX)
        {
            try
            {
                if (_capture == null) _capture = new HatCapture(_layer);
                if (!Accessories.TryProfile(_capture, info.HatBack, info.HatFront, info.Body, scaleX, float.NaN,
                                            out byte[] rgba, out float y0, out float cy, out float z0, out float cz)) return;
                using var fs = File.Create(Path.Combine(_catDir, $"{_catNext:0000}_{MakeSafe(_catId)}.hat"));
                using var bw = new BinaryWriter(fs);
                bw.Write(0x31485541);
                bw.Write(HatCapture.Size);
                bw.Write(HatCapture.Size);
                bw.Write(y0); bw.Write(cy); bw.Write(z0); bw.Write(cz);
                bw.Write(rgba);
            }
            catch (Exception e) { AmongUs3DPlugin.Logger.LogWarning("Catalogue : dessin de " + _catId + " non exporté : " + e.Message); }
        }

        /// <summary>Plus grand dessin affiché de l'animal de l'aperçu, s'il s'agit bien de l'animal <paramref name="id"/>.</summary>
        private static SpriteRenderer MainPetRenderer(CosmeticsLayer c, string id)
        {
            PetBehaviour pet = c != null ? c.CurrentPet : null;
            if (pet == null || pet.Data == null || pet.Data.ProductId != id) return null;
            SpriteRenderer best = null;
            float bestArea = 0f;
            var rs = pet.renderers;
            if (rs == null) return null;
            for (int i = 0; i < rs.Length; i++)
            {
                SpriteRenderer r = rs[i];
                if (r == null || !r.enabled || r.sprite == null) continue;
                Vector3 sz = r.bounds.size;
                float area = sz.x * sz.y;
                if (area > bestArea) { bestArea = area; best = r; }
            }
            return best;
        }

        private static string MakeSafe(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s) sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_');
            return sb.ToString();
        }

        private void EndCatalog(PoolablePlayer pp)
        {
            _catActive = false;
            FinishCatalogFiles();
            AmongUs3DPlugin.Logger.LogInfo($"Catalogue terminé : {_catDone} images ({_catKind} {_catStart} à {_catEnd - 1}).");
            _signature = ""; // l'aperçu normal se refait
            try { pp.UpdateFromDataManager(PlayerMaterial.MaskType.None); } catch { /* l'aperçu se remettra à la réouverture du menu */ }
        }

        private void FinishCatalogFiles()
        {
            try
            {
                if (_catList != null && _catList.Length > 0)
                    File.AppendAllText(Path.Combine(_catDir, "liste.txt"), _catList.ToString());
            }
            catch (Exception e) { AmongUs3DPlugin.Logger.LogWarning("Catalogue : liste non écrite : " + e.Message); }
            _catList = null;
        }
    }
}
