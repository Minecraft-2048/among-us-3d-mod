using System;
using System.Collections.Generic;
using UnityEngine;

namespace AmongUs3D
{
    internal struct ProxyFrame
    {
        public PlayerControl Local;
        public float CamX, CamY;      // position de la caméra 3D au sol
        public float Yaw;             // orientation de la caméra
        public VisionParams Vision;
        public bool ThirdPerson;
        public bool SimulateVision;
        public int GameMask;          // calques visibles par la caméra 2D (gère les fantômes)
        public float MaxDistance;
        public float DeltaTime;
        public WallSystem Walls;
    }

    /// <summary>
    /// Personnages de la vue 3D.
    ///  - Joueurs, fantômes et cadavres : de vrais modèles 3D (<see cref="CrewModel"/>) aux couleurs du joueur, tant
    ///    que le personnage est debout, marche ou flotte (fantôme). Le chapeau devient un objet 3D posé sur la tête
    ///    (<see cref="HatShape"/>) ; le nom reste une image tournée vers la caméra.
    ///  - Tout le reste (animaux, animations spéciales : bouche d'aération, échelle, apparition..., ou tout si le
    ///    réglage Personnages3D est coupé) : panneaux façon "Doom", recopiés à chaque image depuis les dessins du jeu.
    ///  - La main qui caresse et les cœurs, qui font partie du dessin du joueur dans le jeu, sont affichés sur le
    ///    panneau de l'animal caressé (sinon ils flotteraient à côté du joueur en 3D).
    /// Les objets du jeu ne sont jamais modifiés (sauf un masquage instantané pendant le rendu du sol).
    /// </summary>
    internal sealed class ProxySystem
    {
        private enum Kind { Player, Pet, Body }

        // Rôle d'un morceau dans le dessin d'un joueur.
        private const byte RoleOther = 0, RoleBody = 1, RoleSkin = 2, RoleVisor = 3, RoleHatFront = 4, RoleHatBack = 5, RolePetting = 6;

        private sealed class Part
        {
            public Renderer Src;
            public GameObject SrcGo;
            public Transform SrcT;
            public SpriteRenderer SrcSprite;
            public MeshFilter SrcFilter;
            public bool IsText;
            public byte Role;

            public GameObject Go;
            public Transform DstT;
            public Renderer Dst;
            public SpriteRenderer DstSprite;
            public MeshFilter DstFilter;
            public bool DstEnabled;
            public int DstOrder = int.MinValue;

            public IntPtr SrcMatPtr;
            public Material DstMat;
            public bool OwnsMat, HadBlock;

            public bool Active;
            public int SortLayer, SortOrder;
            public float Z;
            public float ScaleX = 1f;
            public float RestX = float.NaN, RestY = float.NaN; // place du morceau quand le personnage est immobile
        }

        private sealed class Entity
        {
            public Kind Kind;
            public Component Source;
            public PlayerControl Player;
            public IntPtr Key;
            public GameObject Root;
            public Transform RootT, MirrorT, TextT;
            public readonly List<Part> Parts = new List<Part>();
            public float NextRescan;
            public float Feet = float.NaN;
            public float LastX, LastY, Speed;
            public bool HasLast, FacingInit, Mirrored;
            public float FaceX = 1f, FaceY;
            public float Dist;
            public float Alpha;
            public bool Visible, WasVisible;

            // modèles 3D
            public CrewModel Model, GhostModel;
            public Part BodyPart;
            public bool NormalBody, HasAnim, Ghost;
            public Game.AnimRefs Anim;
            public int ColorId = -1;
            public float BodyHeight = float.NaN;
            public float BornAt = -100f; // cadavre : moment où il est apparu (il tombe pendant CrewShape.FallDuration)
            // dissolution (Vipère) : dernière valeur lue dans le jeu, et sens dans lequel son compteur avance
            public float DissolveLast = float.NaN;
            public bool DissolveDown;

            // chapeau 3D
            public IntPtr HatKey;
            public int HatColor = int.MinValue;
            public bool HatResolved;
            public HatMeshes Hat;

            // visière et combinaison 3D
            public IntPtr VisorKey, SkinKey;
            public int VisorColor = int.MinValue, SkinColor = int.MinValue;
            public bool VisorResolved, SkinResolved;
            public HatMeshes Visor;
            public SkinPaint Skin;

            // animal
            public bool BeingPet;
            public PetModel PetModel;
            public IntPtr PetKey;
            public bool PetResolved;
            public HatMeshes PetMesh;
        }

        private readonly Dictionary<IntPtr, Entity> _entities = new Dictionary<IntPtr, Entity>();
        private readonly List<Entity> _list = new List<Entity>();
        private readonly List<Entity> _sorted = new List<Entity>();
        private readonly List<PlayerControl> _players = new List<PlayerControl>();
        private readonly List<Component> _scan = new List<Component>();
        private readonly HashSet<IntPtr> _seenPlayers = new HashSet<IntPtr>();
        private readonly HashSet<IntPtr> _seenWorld = new HashSet<IntPtr>();
        private readonly List<Renderer> _hidden = new List<Renderer>();
        private readonly List<Part> _partSort = new List<Part>();
        // Accessoires 3D fabriqués une fois pour toutes : chapeau (par chapeau et couleur), visière, combinaison et animal
        // (par dessin au repos et couleur).
        private readonly Dictionary<(IntPtr, int), HatMeshes> _hatCache = new Dictionary<(IntPtr, int), HatMeshes>();
        private readonly Dictionary<(IntPtr, int), HatMeshes> _visorCache = new Dictionary<(IntPtr, int), HatMeshes>();
        private readonly Dictionary<IntPtr, HatMeshes> _petCache = new Dictionary<IntPtr, HatMeshes>();
        private readonly Dictionary<(IntPtr, int), SkinPaint> _skinCache = new Dictionary<(IntPtr, int), SkinPaint>();
        private bool _accBroken;
        private readonly System.Random _rng = new System.Random();

        private Transform _parent;
        private int _layer;
        private int _excludedLayers;
        private float _nextWorldScan;
        private bool _worldScanned;
        private bool _firstBodyScan = true;
        private MaterialPropertyBlock _mpb;
        private bool _mpbBroken, _copyBroken, _boundsBroken;
        private CrewKit _kit;
        private bool _kitBroken;
        private float _typicalHeight = 0.8f;
        private HatCapture _capture;
        private int _captureLayer = -1;
        private bool _captureBroken;
        private int _bakesThisFrame;

        public int Count => _list.Count;

        /// <summary>Une ligne de diagnostic par joueur ou cadavre (pourquoi un modèle 3D est affiché ou non).</summary>
        public void Describe(System.Text.StringBuilder sb)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            sb.AppendLine($"Chapeaux 3D : {_hatCache.Count} en cache, photo {(_captureBroken ? "en panne" : "OK")} (calque {_captureLayer})");
            foreach (Entity e in _list)
            {
                string anim = "absente";
                if (e.Kind == Kind.Player && e.HasAnim) anim = Game.AnimKind(e.Anim).ToString();
                sb.AppendLine($"  {e.Kind} pos=({e.LastX.ToString("0.0", inv)}, {e.LastY.ToString("0.0", inv)}) visible={e.Visible} " +
                              $"alpha={e.Alpha.ToString("0.00", inv)} corpsNormal={e.NormalBody} anim={anim} fantome={e.Ghost} " +
                              $"corpsTrouvé={(e.BodyPart != null)} morceaux={e.Parts.Count} modèle={(e.Model != null && e.Model.Shown)} " +
                              $"modèleFantôme={(e.GhostModel != null && e.GhostModel.Shown)} chapeau3D={(e.Hat != null)} caressé={e.BeingPet}");
                if (e.Kind == Kind.Body && Game.TryBodyState(e.Source, out Game.BodyState st))
                    sb.AppendLine($"    cadavre : âge={(Time.time - e.BornAt).ToString("0.0", inv)} s montré={st.Shown} opacité={st.Alpha.ToString("0.00", inv)} " +
                                  $"vipère={st.Viper} compteur={st.Dissolve.ToString("0.000", inv)} (descend={e.DissolveDown})");
            }
        }

        /// <summary>Nombre de personnages actuellement affichés en vrai modèle 3D.</summary>
        public int ModelCount
        {
            get
            {
                int n = 0;
                foreach (Entity e in _list)
                    if ((e.Model != null && e.Model.Shown) || (e.GhostModel != null && e.GhostModel.Shown)) n++;
                return n;
            }
        }

        public void Reset(Transform parent, int layer, int extraExcludedMask)
        {
            Clear();
            _parent = parent;
            _layer = layer;
            _excludedLayers = U.MaskFromNames("Shadow,DrawShadows") | (1 << layer) | extraExcludedMask;
            _nextWorldScan = 0f;
            _firstBodyScan = true;
            if (_captureLayer < 0)
            {
                var avoid = new HashSet<int> { layer };
                for (int i = 0; i < 32; i++)
                    if ((extraExcludedMask & (1 << i)) != 0) avoid.Add(i);
                _captureLayer = U.PickFreeLayer(-1, avoid);
            }
        }

        public void Clear()
        {
            RestoreHidden();
            foreach (Entity e in _list) DestroyEntity(e);
            _list.Clear();
            _entities.Clear();
            foreach (HatMeshes m in _hatCache.Values)
            {
                if (m == null) continue;
                U.SafeDestroy(m.Solid);
                U.SafeDestroy(m.Outline);
            }
            _hatCache.Clear();
            foreach (HatMeshes m in _visorCache.Values) m?.Destroy();
            foreach (HatMeshes m in _petCache.Values) m?.Destroy();
            _visorCache.Clear();
            _petCache.Clear();
            _skinCache.Clear();
            if (_capture != null)
            {
                _capture.Destroy();
                _capture = null;
            }
            if (_kit != null)
            {
                _kit.Destroy();
                _kit = null;
            }
        }

        // ------------------------------------------------------------------ liste des entités

        private void Sync(PlayerControl local)
        {
            _seenPlayers.Clear();
            Game.CollectPlayers(_players);
            foreach (PlayerControl pc in _players)
            {
                IntPtr key = pc.Pointer;
                _seenPlayers.Add(key);
                if (!_entities.ContainsKey(key)) Add(Kind.Player, pc, pc);
            }

            _worldScanned = false;
            if (Time.unscaledTime >= _nextWorldScan)
            {
                _nextWorldScan = Time.unscaledTime + 0.2f;
                _worldScanned = true;
                _seenWorld.Clear();
                _scan.Clear();
                Game.CollectPets(_scan);
                foreach (Component c in _scan) { _seenWorld.Add(c.Pointer); if (!_entities.ContainsKey(c.Pointer)) Add(Kind.Pet, c, null); }
                _scan.Clear();
                Game.CollectDeadBodies(_scan);
                foreach (Component c in _scan) { _seenWorld.Add(c.Pointer); if (!_entities.ContainsKey(c.Pointer)) Add(Kind.Body, c, null); }
                _firstBodyScan = false;
            }

            for (int i = _list.Count - 1; i >= 0; i--)
            {
                Entity e = _list[i];
                bool keep = U.Alive(e.Source) && U.Alive(e.Root);
                if (keep && e.Kind == Kind.Player) keep = _seenPlayers.Contains(e.Key);
                else if (keep && _worldScanned) keep = _seenWorld.Contains(e.Key);
                if (keep) continue;
                DestroyEntity(e);
                _entities.Remove(e.Key);
                _list.RemoveAt(i);
            }
        }

        private void Add(Kind kind, Component source, PlayerControl player)
        {
            if (_parent == null) return;
            var e = new Entity { Kind = kind, Source = source, Player = player, Key = source.Pointer };
            e.Root = NewChild("AU3D_" + kind, _parent);
            e.RootT = e.Root.transform;
            e.MirrorT = NewChild("mirror", e.RootT).transform;
            e.TextT = NewChild("text", e.RootT).transform;
            if (kind == Kind.Body)
            {
                e.ColorId = Game.DeadBodyColorId(source);
                // Un corps déjà là quand la vue 3D démarre est couché ; un corps qui apparaît ensuite tombe.
                if (!_firstBodyScan) e.BornAt = Time.time;
                InheritFacing(e);
            }
            Rescan(e);
            _entities[e.Key] = e;
            _list.Add(e);
        }

        /// <summary>
        /// Avancement de la dissolution d'un corps (0 = intact, 1 = dissous) d'après le compteur du jeu.
        /// On ne suppose pas le sens du compteur : on regarde s'il monte ou s'il descend.
        /// </summary>
        private static float Dissolve(Entity e, float ratio)
        {
            // Dans le jeu (2026.9.29) le compteur part du maximum et descend : c'est le sens supposé au départ,
            // sauf pour un corps tout neuf dont le compteur est encore près de zéro.
            if (float.IsNaN(e.DissolveLast)) e.DissolveDown = !(Time.time - e.BornAt < 1.5f && ratio < 0.5f);
            else if (ratio > e.DissolveLast + 1e-4f) e.DissolveDown = false;
            else if (ratio < e.DissolveLast - 1e-4f) e.DissolveDown = true;
            e.DissolveLast = ratio;
            return e.DissolveDown ? 1f - ratio : ratio;
        }

        /// <summary>Le corps tombe dans la direction où regardait le joueur tué.</summary>
        private void InheritFacing(Entity body)
        {
            if (!Game.TryBodyState(body.Source, out Game.BodyState st) || st.ParentId < 0) return;
            foreach (Entity p in _list)
            {
                if (p.Kind != Kind.Player || !p.FacingInit || Game.PlayerId(p.Player) != st.ParentId) continue;
                body.FaceX = p.FaceX; body.FaceY = p.FaceY; body.FacingInit = true;
                return;
            }
        }

        private GameObject NewChild(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.layer = _layer;
            go.transform.SetParent(parent, false);
            return go;
        }

        private void DestroyEntity(Entity e)
        {
            foreach (Part p in e.Parts)
                if (p.OwnsMat) U.SafeDestroy(p.DstMat);
            e.Parts.Clear();
            e.Model?.Destroy();
            e.GhostModel?.Destroy();
            e.Model = e.GhostModel = null;
            e.PetModel?.Destroy();
            e.PetModel = null;
            U.SafeDestroy(e.Root);
        }

        // ------------------------------------------------------------------ morceaux (sprites) d'une entité

        private void Rescan(Entity e)
        {
            e.NextRescan = Time.unscaledTime + 1.2f + (float)_rng.NextDouble() * 0.6f;
            var keep = new HashSet<IntPtr>();
            try
            {
                foreach (Renderer r in e.Source.GetComponentsInChildren<Renderer>(true))
                    TryKeep(e, r, keep, RoleOther);

                // Animal : la main du maître qui le caresse et les cœurs sont dessinés sur son panneau.
                if (e.Kind == Kind.Pet)
                {
                    PlayerControl owner = Game.PetOwner(e.Source);
                    if (owner != null && Game.TryPettingRenderers(owner, out SpriteRenderer hand, out SpriteRenderer hearts))
                    {
                        if (hand != null) TryKeep(e, hand, keep, RolePetting);
                        if (hearts != null) TryKeep(e, hearts, keep, RolePetting);
                    }
                }
            }
            catch (Exception ex)
            {
                AmongUs3DPlugin.Logger.LogDebug("Rescan : " + ex.Message);
            }

            for (int i = e.Parts.Count - 1; i >= 0; i--)
            {
                Part p = e.Parts[i];
                if (keep.Contains(p.Src.Pointer) && U.Alive(p.Src)) continue;
                if (p.OwnsMat) U.SafeDestroy(p.DstMat);
                U.SafeDestroy(p.Go);
                e.Parts.RemoveAt(i);
            }

            if (e.Kind == Kind.Player) Classify(e);
        }

        private void TryKeep(Entity e, Renderer r, HashSet<IntPtr> keep, byte role)
        {
            if (r == null) return;
            IntPtr k = r.Pointer;
            if (keep.Contains(k)) return;
            Part existing = e.Parts.Find(p => p.Src.Pointer == k);
            if (existing != null)
            {
                if (role != RoleOther) existing.Role = role;
                keep.Add(k);
                return;
            }
            Part part = TryCreatePart(e, r);
            if (part == null) return;
            part.Role = role;
            e.Parts.Add(part);
            keep.Add(k);
        }

        /// <summary>Repère, dans le dessin d'un joueur, le corps, la combinaison, la visière, le chapeau et la main qui caresse.</summary>
        private static void Classify(Entity e)
        {
            e.BodyPart = null;
            e.NormalBody = false;
            foreach (Part p in e.Parts) p.Role = RoleOther;
            if (Game.TryBodyInfo(e.Player, out BodyInfo info))
            {
                e.NormalBody = info.Normal;
                e.ColorId = info.ColorId;
                IntPtr body = Ptr(info.Body), skin = Ptr(info.Skin), visor = Ptr(info.Visor),
                       hatFront = Ptr(info.HatFront), hatBack = Ptr(info.HatBack);
                foreach (Part p in e.Parts)
                {
                    IntPtr k = p.Src.Pointer;
                    if (k == body) { p.Role = RoleBody; e.BodyPart = p; }
                    else if (k == skin) p.Role = RoleSkin;
                    else if (k == visor) p.Role = RoleVisor;
                    else if (k == hatFront) p.Role = RoleHatFront;
                    else if (k == hatBack) p.Role = RoleHatBack;
                }
            }
            if (Game.TryPettingRenderers(e.Player, out SpriteRenderer hand, out SpriteRenderer hearts))
            {
                IntPtr h1 = Ptr(hand), h2 = Ptr(hearts);
                foreach (Part p in e.Parts)
                {
                    IntPtr k = p.Src.Pointer;
                    if (k != IntPtr.Zero && (k == h1 || k == h2)) p.Role = RolePetting;
                }
            }
            e.HasAnim = Game.TryAnimRefs(e.Player, out e.Anim);
        }

        private static IntPtr Ptr(UnityEngine.Object o) => o != null ? o.Pointer : IntPtr.Zero;

        private Part TryCreatePart(Entity e, Renderer r)
        {
            GameObject go = r.gameObject;
            if ((_excludedLayers & (1 << go.layer)) != 0) return null;
            string name = go.name ?? "";
            if (name.IndexOf("shadow", StringComparison.OrdinalIgnoreCase) >= 0) return null;

            SpriteRenderer sprite = r.TryCast<SpriteRenderer>();
            MeshFilter filter = null;
            if (sprite == null)
            {
                if (r.TryCast<MeshRenderer>() == null) return null;     // particules, lignes... ignorés
                filter = r.GetComponent<MeshFilter>();
                if (filter == null) return null;
            }
            if (e.Kind == Kind.Player && Game.BelongsToPet(r)) return null; // l'animal a son propre panneau

            // Chez un personnage, les MeshRenderer sont les textes TextMeshPro (nom, texte daltonien...) :
            // on ne les met pas en miroir pour qu'ils restent lisibles.
            bool isText = sprite == null;

            var p = new Part
            {
                Src = r, SrcGo = go, SrcT = r.transform, SrcSprite = sprite, SrcFilter = filter, IsText = isText,
            };
            p.Go = NewChild(isText ? "txt" : "spr", isText ? e.TextT : e.MirrorT);
            p.DstT = p.Go.transform;
            if (sprite != null)
            {
                p.DstSprite = p.Go.AddComponent<SpriteRenderer>();
                p.Dst = p.DstSprite;
                try { p.DstSprite.maskInteraction = SpriteMaskInteraction.None; } catch { /* ignoré */ }
            }
            else
            {
                p.DstFilter = p.Go.AddComponent<MeshFilter>();
                p.Dst = p.Go.AddComponent<MeshRenderer>();
            }
            p.Dst.enabled = false;
            return p;
        }

        // ------------------------------------------------------------------ modèles 3D

        private CrewModel NewModel(ModelKind kind)
        {
            if (_kitBroken || _parent == null) return null;
            try
            {
                if (_kit == null) _kit = CrewKit.Create();
                return CrewModel.Create(_kit, _parent, _layer, kind);
            }
            catch (Exception e)
            {
                _kitBroken = true;
                AmongUs3DPlugin.Logger.LogWarning("Personnages 3D indisponibles (retour aux panneaux plats) : " + e);
                return null;
            }
        }

        /// <summary>
        /// Un joueur est montré en modèle 3D s'il a le corps classique, qu'il est affiché par le jeu,
        /// et qu'il est simplement debout, en train de marcher, ou fantôme.
        /// </summary>
        private static bool Wants3D(Entity e, in ProxyFrame f, out bool running, out float bodyAlpha, out bool ghost, out int special)
        {
            running = false;
            ghost = false;
            bodyAlpha = 1f;
            special = 0;
            Part body = e.BodyPart;
            if (!e.NormalBody || !e.HasAnim || body == null || body.SrcSprite == null) return false;
            try
            {
                if (!body.Src.enabled || !body.SrcGo.activeInHierarchy || ((f.GameMask >> body.SrcGo.layer) & 1) == 0) return false;
                bodyAlpha = body.SrcSprite.color.a;
            }
            catch { return false; }
            int kind = Game.AnimKind(e.Anim);
            if (kind == Game.AnimSpecial) return false;
            if (kind >= Game.AnimEnterVent)
            {
                // bouche d'aération, apparition, échelle : jouées par le modèle 3D (réglage Animations3D)
                if (!ModConfig.Animations3D.Value) return false;
                special = kind;
                running = kind == Game.AnimClimb;
                return bodyAlpha > 0.02f;
            }
            ghost = kind == Game.AnimGhost;
            running = kind == Game.AnimRun;
            return bodyAlpha > 0.02f;
        }

        // ------------------------------------------------------------------ mise à jour par image

        public void Update(in ProxyFrame f)
        {
            Sync(f.Local);
            _bakesThisFrame = 0;
            _sorted.Clear();
            foreach (Entity e in _list)
            {
                try
                {
                    UpdateEntity(e, f);
                }
                catch (Exception ex)
                {
                    e.Visible = false;
                    HideAll(e);
                    AmongUs3DPlugin.Logger.LogDebug("Entité ignorée : " + ex.Message);
                }
                if (e.Visible) _sorted.Add(e);
            }

            // Tri du plus loin au plus proche (chaque entité a sa plage d'ordres de rendu).
            _sorted.Sort((a, b) => b.Dist.CompareTo(a.Dist));
            int baseOrder = 100;
            foreach (Entity e in _sorted)
            {
                AssignOrders(e, baseOrder);
                baseOrder += 42;
                if (baseOrder > 30000) baseOrder = 30000;
            }
        }

        private void UpdateEntity(Entity e, in ProxyFrame f)
        {
            // Position au sol
            Vector2 g;
            switch (e.Kind)
            {
                case Kind.Player: g = Game.TruePosition(e.Player); break;
                case Kind.Body: g = Game.DeadBodyPosition(e.Source); break;
                default:
                {
                    Vector3 tp = e.Source.transform.position;
                    g = U.V2(tp.x, tp.y);
                    break;
                }
            }

            // Vitesse, et direction du regard = direction du déplacement
            e.Speed = 0f;
            if (e.HasLast && f.DeltaTime > 0.0001f)
            {
                float vx = (g.x - e.LastX) / f.DeltaTime, vy = (g.y - e.LastY) / f.DeltaTime;
                float sp = vx * vx + vy * vy;
                if (sp < 400f) e.Speed = MathF.Sqrt(sp);
                if (sp > 0.09f && sp < 400f)
                {
                    float l = MathF.Sqrt(sp);
                    e.FaceX = vx / l; e.FaceY = vy / l;
                    e.FacingInit = true;
                }
            }
            e.LastX = g.x; e.LastY = g.y; e.HasLast = true;

            if (e.Kind == Kind.Pet) e.BeingPet = Game.PetBeingPet(e.Source);
            bool isLocal = e.Kind == Kind.Player && f.Local != null && e.Key == f.Local.Pointer;
            float cdx = g.x - f.CamX, cdy = g.y - f.CamY;
            e.Dist = MathF.Sqrt(cdx * cdx + cdy * cdy);

            float alpha;
            if (isLocal) alpha = f.ThirdPerson ? 1f : 0f;
            else if (e.Dist > f.MaxDistance) alpha = 0f;
            // collé à la caméra (ex. son propre animal qui suit) : masqué, sauf l'animal qu'on caresse
            else if (!f.ThirdPerson && e.Dist < 0.6f && (e.Kind == Kind.Player || (e.Kind == Kind.Pet && !e.BeingPet))) alpha = 0f;
            else if (!f.SimulateVision || f.Vision.Full) alpha = 1f;
            else
            {
                float vdx = g.x - f.Vision.Cx, vdy = g.y - f.Vision.Cy;
                alpha = M.VisionAlpha(MathF.Sqrt(vdx * vdx + vdy * vdy), f.Vision.Radius, false);
                if (alpha > 0.01f && f.Walls != null && f.Walls.LineBlocked(f.Vision.Cx, f.Vision.Cy, g.x, g.y)) alpha = 0f;
            }
            e.Alpha = alpha;
            e.Visible = alpha > 0.01f;

            if (Time.unscaledTime >= e.NextRescan) Rescan(e);

            if (!e.Visible)
            {
                if (e.WasVisible) HideAll(e);
                e.WasVisible = false;
                return;
            }
            e.WasVisible = true;

            // ---- modèle 3D (vivant, fantôme ou cadavre) ou panneaux plats ?
            bool use3D = false, running = false, ghost = false;
            float bodyAlpha = 1f;
            int special = 0;
            bool animated = ModConfig.Animations3D.Value;
            Game.BodyState bodyState = default;
            if (ModConfig.Characters3D.Value && !_kitBroken)
            {
                if (e.Kind == Kind.Player) use3D = Wants3D(e, f, out running, out bodyAlpha, out ghost, out special);
                else if (e.Kind == Kind.Body)
                {
                    use3D = true;
                    // Le corps suit ce que le jeu en montre (un corps dissous par la Vipère finit par disparaître).
                    if (animated && Game.TryBodyState(e.Source, out bodyState))
                    {
                        use3D = bodyState.Shown && bodyState.Alpha > 0.02f;
                        bodyAlpha = bodyState.Alpha;
                    }
                }
                if (ghost && !ModConfig.Ghosts3D.Value) use3D = false;
            }
            bool busy = running || special != 0; // le dessin 2D bouge : il ne sert plus de référence
            e.Ghost = use3D && ghost;
            CrewModel model = null;
            if (use3D)
            {
                if (ghost)
                {
                    if (e.GhostModel == null) e.GhostModel = NewModel(ModelKind.Ghost);
                    model = e.GhostModel;
                    running = e.Speed > 0.4f;
                }
                else
                {
                    if (e.Model == null) e.Model = NewModel(e.Kind == Kind.Body ? ModelKind.Corpse : ModelKind.Crew);
                    model = e.Model;
                }
                if (model == null) use3D = false;
            }
            bool hat3D = false;
            if (use3D && e.Kind == Kind.Player && ModConfig.Hats3D.Value && !_captureBroken)
            {
                ResolveHat(e);
                hat3D = e.Hat != null;
            }
            // Visière, combinaison et animal en 3D (réglage Accessoires3D).
            bool acc3D = use3D && !ghost && e.Kind == Kind.Player && ModConfig.Accessories3D.Value && !_captureBroken && !_accBroken;
            bool pet3D = e.Kind == Kind.Pet && ModConfig.Accessories3D.Value && ModConfig.Characters3D.Value
                         && !_kitBroken && !_captureBroken && !_accBroken;
            bool petShown3D = pet3D && e.PetMesh != null;

            float height = (float.IsNaN(e.BodyHeight) ? _typicalHeight : e.BodyHeight)
                           * M.Clamp(ModConfig.CharacterScale.Value, 0.3f, 3f);
            // Images qui restent tournées vers la caméra : devant le modèle (nom) ou derrière (dos du chapeau).
            float depth = use3D ? height * 0.36f : 0f;

            // Repère de l'objet source (2D)
            Transform st = e.Source.transform;
            Vector3 sp0 = st.position;
            Vector3 sScale = st.lossyScale;
            float sAngle = st.eulerAngles.z;
            float ssx = MathF.Abs(sScale.x) < 1e-4f ? 1f : sScale.x;
            float ssy = MathF.Abs(sScale.y) < 1e-4f ? 1f : sScale.y;
            float ca = MathF.Cos(-sAngle * M.Deg2Rad), sa = MathF.Sin(-sAngle * M.Deg2Rad);

            float minY = float.MaxValue;
            float bodySize = 0f;
            Part main = null;
            float mainArea = 0f;
            foreach (Part p in e.Parts)
            {
                bool active = false;
                try
                {
                    active = p.Src.enabled && p.SrcGo.activeInHierarchy && ((f.GameMask >> p.SrcGo.layer) & 1) != 0;
                }
                catch { /* détruit entre deux scans */ }
                p.Active = active;
                if (!active) { SetEnabled(p, false); continue; }

                // Morceaux remplacés par le modèle 3D : le corps, la combinaison et la visière d'un joueur,
                // son chapeau quand il est en 3D, tout le dessin d'un cadavre.
                // La main qui caresse et les cœurs d'un joueur sont dessinés sur le panneau de son animal.
                bool replaced = (e.Kind == Kind.Player && p.Role == RolePetting) || (petShown3D && p.Role != RolePetting)
                                || (use3D && (e.Kind == Kind.Body || p.Role == RoleBody || p.Role == RoleSkin || p.Role == RoleVisor
                                              || (hat3D && (p.Role == RoleHatFront || p.Role == RoleHatBack))));

                Vector3 wp = p.SrcT.position;
                Vector3 ls = p.SrcT.lossyScale;
                p.ScaleX = ls.x / ssx;
                if (replaced)
                {
                    SetEnabled(p, false);
                }
                else
                {
                    float rx = wp.x - sp0.x, ry = wp.y - sp0.y;
                    float lx = (rx * ca - ry * sa) / ssx;
                    float ly = (rx * sa + ry * ca) / ssy;
                    float lz = p.Role == RoleHatBack ? depth : -depth;
                    // Sur un modèle 3D, le chapeau et le nom ne suivent pas les rebonds de l'animation de marche
                    // du dessin 2D : ils gardent la place qu'ils ont quand le personnage est immobile.
                    if (use3D && e.Kind == Kind.Player && (p.IsText || p.Role == RoleHatFront || p.Role == RoleHatBack))
                    {
                        if (!busy || float.IsNaN(p.RestY)) { p.RestX = lx; p.RestY = ly; }
                        else { lx = p.RestX; ly = p.RestY; }
                    }
                    p.DstT.localPosition = U.V3(lx, ly, lz);
                    p.DstT.localRotation = U.RotationZ(p.SrcT.eulerAngles.z - sAngle);
                    p.DstT.localScale = U.V3(p.ScaleX, ls.y / ssy, 1f);
                    p.Z = wp.z;
                    CopyVisuals(p, e.Alpha);
                }

                if (p.Role == RolePetting) continue; // la main ne compte ni pour les pieds ni pour l'orientation
                if (!p.IsText && !_boundsBroken)
                {
                    try
                    {
                        Bounds b = p.Src.bounds;
                        Vector3 bmin = b.min, bsize = b.size;
                        float by = bmin.y - sp0.y;
                        if (by < minY) minY = by;
                        float area = bsize.x * bsize.y;
                        if (p.SrcSprite != null && area > mainArea) { mainArea = area; main = p; }
                        if (p.Role == RoleBody) bodySize = bsize.y;
                    }
                    catch (Exception ex)
                    {
                        _boundsBroken = true;
                        Safe.Report("Renderer.bounds", ex);
                    }
                }
                else if (main == null && p.SrcSprite != null)
                {
                    main = p;
                }
            }

            // Hauteur des pieds (lissée pour ignorer le "rebond" des animations)
            if (minY < float.MaxValue && !(use3D && busy && !float.IsNaN(e.Feet)))
            {
                float k = 1f - MathF.Exp(-f.DeltaTime * 8f);
                e.Feet = float.IsNaN(e.Feet) ? minY : M.Lerp(e.Feet, minY, k);
            }
            float feet = float.IsNaN(e.Feet) ? -0.45f : e.Feet;

            // Taille du personnage : celle de son dessin quand il est immobile (et vivant).
            if (use3D && e.Kind == Kind.Player && !busy && !ghost && bodySize > 0.3f && bodySize < 3f)
            {
                e.BodyHeight = float.IsNaN(e.BodyHeight) ? bodySize : M.Lerp(e.BodyHeight, bodySize, 0.1f);
                if (e.NormalBody) _typicalHeight = M.Lerp(_typicalHeight, e.BodyHeight, 0.05f);
            }

            // Chapeau 3D pas encore fabriqué : on le photographie maintenant (personnage immobile, au plus un par image).
            if (use3D && e.Kind == Kind.Player && !e.HatResolved && !busy && ModConfig.Hats3D.Value && !_captureBroken)
                BakeHat(e, ghost);
            if (acc3D && !busy) ResolveAccessories(e);
            if (pet3D && main != null && e.Speed < 0.05f) ResolvePet(e, main);

            // Le sprite d'origine regarde-t-il vers la droite ?
            bool srcRight = true;
            if (main != null)
            {
                srcRight = !main.SrcSprite.flipX;
                if (main.ScaleX < 0f) srcRight = !srcRight;
            }
            if (!e.FacingInit && main != null)
            {
                e.FaceX = srcRight ? 1f : -1f; e.FaceY = 0f; e.FacingInit = true;
            }
            // À l'écran, le personnage doit regarder du côté où il se déplace par rapport à la caméra.
            M.YawBasis(f.Yaw, out _, out _, out float crx, out float cry);
            bool wantRight = e.FaceX * crx + e.FaceY * cry >= 0f;
            bool mirror = wantRight != srcRight;
            if (mirror != e.Mirrored)
            {
                e.MirrorT.localScale = U.V3(mirror ? -1f : 1f, 1f, 1f);
                e.Mirrored = mirror;
            }

            // Panneau vertical tourné vers la caméra ("haut" = -Z), pieds posés au sol (z = 0)
            float bl = MathF.Max(0.001f, e.Dist);
            float fx = cdx / bl, fy = cdy / bl;
            if (e.Dist < 0.001f) M.YawBasis(f.Yaw, out fx, out fy, out _, out _);
            e.RootT.position = U.V3(g.x, g.y, feet);
            e.RootT.rotation = U.LookRotation(fx, fy, 0f, 0f, 0f, -1f);

            // ---- modèle 3D
            if (use3D)
            {
                model.SetColor(e.ColorId);
                if (e.Kind == Kind.Player) model.SetHat(hat3D ? e.Hat.Solid : null, hat3D ? e.Hat.Outline : null);
                if (e.Kind == Kind.Player && !ghost)
                {
                    model.SetVisor(acc3D ? e.Visor?.Solid : null, acc3D ? e.Visor?.Outline : null);
                    model.SetSkin(acc3D ? e.Skin : null);
                }
                float a = e.Alpha * (ghost ? M.Clamp(bodyAlpha, 0.2f, 0.6f) : bodyAlpha);
                float faceX = e.FaceX, faceY = e.FaceY, popScale = 1f, popLift = 0f;
                if (special == Game.AnimClimb)
                {
                    faceX = 0f; faceY = 1f; // sur une échelle, on fait face au mur (le nord de la carte)
                }
                else if (special != 0)
                {
                    // saut dans une bouche d'aération ; la sortie et l'apparition sont la même animation à l'envers
                    float p = Game.AnimProgress(e.Anim);
                    if (p >= 0f) CrewShape.VentPop(special == Game.AnimEnterVent ? p : 1f - p, out popScale, out popLift);
                }
                model.SetPop(popScale, popLift);
                if (e.Kind == Kind.Body)
                    model.SetCorpse(animated ? Time.time - e.BornAt : -1f, animated && bodyState.Viper ? Dissolve(e, bodyState.Dissolve) : 0f);
                model.Update(g.x, g.y, height, faceX, faceY, running, f.DeltaTime, a);
                model.Show(true);
                CrewModel other = ghost ? e.Model : e.GhostModel;
                other?.Show(false);
            }
            else
            {
                e.Model?.Show(false);
                e.GhostModel?.Show(false);
            }

            // ---- animal 3D
            if (e.Kind == Kind.Pet)
            {
                if (petShown3D && (e.PetModel == null || !e.PetModel.Alive))
                {
                    e.PetModel?.Destroy();
                    e.PetModel = NewPet();
                }
                if (petShown3D && e.PetModel != null)
                {
                    e.PetModel.SetMesh(e.PetMesh);
                    e.PetModel.Update(g.x, g.y, e.FaceX, e.FaceY, e.Speed > 0.2f, f.DeltaTime, e.Alpha);
                    e.PetModel.Show(true);
                }
                else
                {
                    e.PetModel?.Show(false);
                }
            }
        }

        private PetModel NewPet()
        {
            if (_kitBroken || _parent == null) return null;
            try
            {
                if (_kit == null) _kit = CrewKit.Create();
                return PetModel.Create(_kit, _parent, _layer);
            }
            catch (Exception ex)
            {
                _accBroken = true;
                AmongUs3DPlugin.Logger.LogWarning("Animaux 3D indisponibles (retour aux images plates) : " + ex);
                return null;
            }
        }

        // ------------------------------------------------------------------ visière, combinaison et animal 3D

        private HatCapture Capture() => _capture ?? (_capture = new HatCapture(_captureLayer));

        /// <summary>
        /// Visière et combinaison du personnage immobile : retrouvées dans le cache, sinon fabriquées
        /// (une photo par image au plus, pour tous les personnages).
        /// </summary>
        private void ResolveAccessories(Entity e)
        {
            if (e.BodyPart == null || !e.BodyPart.Active) return;
            Part visor = null, skin = null;
            foreach (Part p in e.Parts)
            {
                if (!p.Active || p.SrcSprite == null) continue;
                if (p.Role == RoleVisor) visor = p;
                else if (p.Role == RoleSkin) skin = p;
            }
            try
            {
                IntPtr vk = Accessories.SpriteKey(visor?.SrcSprite);
                if (vk != e.VisorKey || e.ColorId != e.VisorColor)
                {
                    e.VisorKey = vk; e.VisorColor = e.ColorId; e.VisorResolved = false; e.Visor = null;
                }
                if (!e.VisorResolved)
                {
                    if (vk == IntPtr.Zero) e.VisorResolved = true;
                    else if (_visorCache.TryGetValue((vk, e.ColorId), out HatMeshes m)) { e.Visor = m; e.VisorResolved = true; }
                    else if (_bakesThisFrame == 0)
                    {
                        _bakesThisFrame++;
                        m = Accessories.Puff(Capture(), null, visor.SrcSprite, e.BodyPart.SrcSprite, e.BodyPart.ScaleX, float.NaN,
                                             Accessories.VisorHalfThickness, "AU3D_Visiere");
                        _visorCache[(vk, e.ColorId)] = m;
                        e.Visor = m;
                        e.VisorResolved = true;
                    }
                }

                IntPtr sk = Accessories.SpriteKey(skin?.SrcSprite);
                if (sk != e.SkinKey || e.ColorId != e.SkinColor)
                {
                    e.SkinKey = sk; e.SkinColor = e.ColorId; e.SkinResolved = false; e.Skin = null;
                }
                if (!e.SkinResolved)
                {
                    if (sk == IntPtr.Zero) e.SkinResolved = true;
                    else if (_skinCache.TryGetValue((sk, e.ColorId), out SkinPaint paint)) { e.Skin = paint; e.SkinResolved = true; }
                    else if (_bakesThisFrame == 0)
                    {
                        _bakesThisFrame++;
                        paint = Accessories.PaintSkin(Capture(), skin.SrcSprite, e.BodyPart.SrcSprite, e.BodyPart.ScaleX);
                        _skinCache[(sk, e.ColorId)] = paint;
                        e.Skin = paint;
                        e.SkinResolved = true;
                    }
                }
            }
            catch (Exception ex)
            {
                _accBroken = true;
                AmongUs3DPlugin.Logger.LogWarning("Visières et combinaisons 3D indisponibles : " + ex);
            }
        }

        /// <summary>Animal immobile : son volume, retrouvé dans le cache ou fabriqué à partir de son dessin du moment.</summary>
        private void ResolvePet(Entity e, Part main)
        {
            IntPtr key = Accessories.SpriteKey(main.SrcSprite);
            if (key == IntPtr.Zero || (key == e.PetKey && e.PetResolved)) return;
            if (key != e.PetKey) { e.PetKey = key; e.PetResolved = false; }
            if (_petCache.TryGetValue(key, out HatMeshes m))
            {
                if (m != null) e.PetMesh = m;
                e.PetResolved = true;
                return;
            }
            if (_bakesThisFrame > 0) return;
            _bakesThisFrame++;
            try
            {
                m = Accessories.PuffPet(Capture(), main.SrcSprite, main.ScaleX);
            }
            catch (Exception ex)
            {
                _accBroken = true;
                AmongUs3DPlugin.Logger.LogWarning("Animaux 3D indisponibles (retour aux images plates) : " + ex);
                return;
            }
            _petCache[key] = m;
            if (m != null) e.PetMesh = m; // sinon on garde le volume précédent
            e.PetResolved = true;
        }

        // ------------------------------------------------------------------ chapeaux 3D

        /// <summary>Retrouve le chapeau 3D déjà fabriqué pour ce chapeau et cette couleur.</summary>
        private void ResolveHat(Entity e)
        {
            IntPtr key = Game.HatKey(e.Player);
            if (key != e.HatKey || e.ColorId != e.HatColor)
            {
                e.HatKey = key;
                e.HatColor = e.ColorId;
                e.HatResolved = false;
                e.Hat = null;
            }
            if (e.HatResolved) return;
            if (key == IntPtr.Zero) { e.HatResolved = true; return; }
            if (_hatCache.TryGetValue((key, e.HatColor), out HatMeshes m))
            {
                e.Hat = m;
                e.HatResolved = true;
            }
        }

        /// <param name="ghost">
        /// fantôme : son dessin 2D n'a pas la taille du corps vivant, on se cale sur le sommet de sa tête
        /// et sur la taille du modèle 3D
        /// </param>
        private void BakeHat(Entity e, bool ghost)
        {
            if (_bakesThisFrame > 0 || e.HatKey == IntPtr.Zero || e.BodyPart == null || !e.BodyPart.Active) return;
            Part front = null, back = null;
            foreach (Part p in e.Parts)
            {
                if (!p.Active || p.SrcSprite == null) continue;
                if (p.Role == RoleHatFront) front = p;
                else if (p.Role == RoleHatBack) back = p;
            }
            if (front == null && back == null) return; // chapeau caché pour l'instant : on réessaiera
            _bakesThisFrame++;

            HatMeshes made = null;
            try
            {
                if (_capture == null) _capture = new HatCapture(_captureLayer);
                Bounds body = e.BodyPart.Src.bounds;
                Vector3 c = body.center, mx = body.max, sz = body.size;
                float h = ghost ? (float.IsNaN(e.BodyHeight) ? _typicalHeight : e.BodyHeight) : sz.y;
                bool right = !e.BodyPart.SrcSprite.flipX;
                if (e.BodyPart.ScaleX < 0f) right = !right;
                float facing = right ? 1f : -1f;
                if (h > 0.05f && _capture.TryCapture(back?.SrcSprite, front?.SrcSprite, out byte[] rgba, out float minX, out float minY, out float cell))
                {
                    ShapeMesh s = HatShape.Build(rgba, HatCapture.Size, HatCapture.Size,
                                                 (minX - c.x) * facing / h, cell * facing / h,
                                                 1f + (minY - mx.y) / h, cell / h, HatShape.HalfThickness);
                    if (s != null)
                    {
                        made = new HatMeshes { Solid = CrewKit.BuildMesh(s, "AU3D_Chapeau", 0f, out _) };
                        made.Outline = CrewKit.BuildMesh(s, "AU3D_ChapeauContour", CrewShape.OutlineWidth * 0.8f, out var oc);
                        CrewKit.Black(made.Outline, oc);
                    }
                }
            }
            catch (Exception ex)
            {
                _captureBroken = true;
                AmongUs3DPlugin.Logger.LogWarning("Chapeaux 3D indisponibles (retour aux chapeaux plats) : " + ex);
                return;
            }
            _hatCache[(e.HatKey, e.HatColor)] = made;
            e.Hat = made;
            e.HatResolved = true;
            AmongUs3DPlugin.Logger.LogInfo(made != null
                ? $"Chapeau 3D fabriqué ({made.Solid.vertexCount} sommets)."
                : "Chapeau sans dessin exploitable : il reste plat.");
        }

        private void HideAll(Entity e)
        {
            foreach (Part p in e.Parts) SetEnabled(p, false);
            e.Model?.Show(false);
            e.GhostModel?.Show(false);
            e.PetModel?.Show(false);
        }

        private static void SetEnabled(Part p, bool on)
        {
            if (p.DstEnabled == on) return;
            p.DstEnabled = on;
            try { p.Dst.enabled = on; } catch { /* ignoré */ }
        }

        private void CopyVisuals(Part p, float alpha)
        {
            if (p.SrcSprite != null)
            {
                SpriteRenderer s = p.SrcSprite, d = p.DstSprite;
                d.sprite = s.sprite;
                d.flipX = s.flipX;
                d.flipY = s.flipY;
                Color c = s.color;
                c.a *= alpha;
                d.color = c;
            }
            else
            {
                U.SetMesh(p.DstFilter, U.SharedMesh(p.SrcFilter));
            }
            SyncMaterial(p);
            CopyPropertyBlock(p);
            SetEnabled(p, p.IsText ? alpha > 0.5f : alpha > 0.01f);
        }

        private void SyncMaterial(Part p)
        {
            Material sm = p.Src.sharedMaterial;
            IntPtr ptr = sm != null ? sm.Pointer : IntPtr.Zero;
            if (ptr != p.SrcMatPtr)
            {
                if (p.OwnsMat) U.SafeDestroy(p.DstMat);
                p.SrcMatPtr = ptr;
                p.OwnsMat = false;
                p.DstMat = sm;
                if (sm != null && NeedsStencilOverride(sm))
                {
                    try
                    {
                        p.DstMat = new Material(sm);
                        ApplyStencilOverride(p.DstMat);
                        p.OwnsMat = true;
                    }
                    catch (Exception e)
                    {
                        Safe.Report("new Material(Material)", e);
                        p.DstMat = sm;
                    }
                }
                p.Dst.sharedMaterial = p.DstMat;
            }
            else if (p.OwnsMat && sm != null && !_copyBroken)
            {
                try
                {
                    p.DstMat.CopyPropertiesFromMaterial(sm);
                    ApplyStencilOverride(p.DstMat);
                }
                catch (Exception e)
                {
                    _copyBroken = true;
                    AmongUs3DPlugin.Logger.LogWarning("CopyPropertiesFromMaterial indisponible : " + e.Message);
                }
            }
        }

        /// <summary>
        /// Les matériaux "masqués" (test de stencil) seraient invisibles dans nos caméras :
        /// on en fait une copie sans test de stencil.
        /// </summary>
        internal static bool NeedsStencilOverride(Material m)
        {
            try
            {
                foreach (string prop in new[] { "_StencilComp", "_MaskComp" })
                {
                    if (!m.HasProperty(prop)) continue;
                    float v = m.GetFloat(prop);
                    if (MathF.Abs(v - 8f) > 0.01f && MathF.Abs(v) > 0.01f) return true; // ni "Always" ni "Disabled"
                }
            }
            catch { /* ignoré */ }
            return false;
        }

        internal static void ApplyStencilOverride(Material m)
        {
            try
            {
                if (m.HasProperty("_StencilComp")) m.SetFloat("_StencilComp", 8f);
                if (m.HasProperty("_MaskComp")) m.SetFloat("_MaskComp", 8f);
            }
            catch { /* ignoré */ }
        }

        private void CopyPropertyBlock(Part p)
        {
            if (_mpbBroken) return;
            try
            {
                if (p.Src.HasPropertyBlock())
                {
                    if (_mpb == null) _mpb = new MaterialPropertyBlock();
                    p.Src.GetPropertyBlock(_mpb);
                    p.Dst.SetPropertyBlock(_mpb);
                    p.HadBlock = true;
                }
                else if (p.HadBlock)
                {
                    p.Dst.SetPropertyBlock(null);
                    p.HadBlock = false;
                }
            }
            catch (Exception e)
            {
                _mpbBroken = true;
                AmongUs3DPlugin.Logger.LogWarning("Copie des MaterialPropertyBlock désactivée : " + e.Message);
            }
        }

        /// <summary>
        /// Ordre de dessin : le modèle 3D d'abord, puis les images, en reproduisant l'empilement 2D
        /// (calque de tri, ordre, puis profondeur z) à l'intérieur d'une plage réservée à l'entité.
        /// </summary>
        private void AssignOrders(Entity e, int baseOrder)
        {
            if (e.Model != null && e.Model.Shown) e.Model.SetOrder(baseOrder);
            if (e.GhostModel != null && e.GhostModel.Shown) e.GhostModel.SetOrder(baseOrder);
            if (e.PetModel != null && e.PetModel.Shown) e.PetModel.SetOrder(baseOrder);
            _partSort.Clear();
            foreach (Part p in e.Parts)
            {
                if (!p.Active || !p.DstEnabled) continue;
                try
                {
                    p.SortLayer = SortingLayer.GetLayerValueFromID(p.Src.sortingLayerID);
                    p.SortOrder = p.Src.sortingOrder;
                }
                catch { /* garde les valeurs précédentes */ }
                _partSort.Add(p);
            }
            // Même règle que la caméra 2D : calque de tri, puis ordre, puis le plus éloigné (z le plus grand) d'abord.
            _partSort.Sort(ComparePartsForDrawing);
            for (int i = 0; i < _partSort.Count; i++)
            {
                Part p = _partSort[i];
                int order = baseOrder + 2 + Math.Min(i, 39);
                if (p.DstOrder == order) continue;
                p.DstOrder = order;
                p.Dst.sortingOrder = order;
            }
        }

        private static int ComparePartsForDrawing(Part a, Part b)
        {
            int c = a.SortLayer.CompareTo(b.SortLayer);
            if (c != 0) return c;
            c = a.SortOrder.CompareTo(b.SortOrder);
            if (c != 0) return c;
            return b.Z.CompareTo(a.Z);
        }

        // ------------------------------------------------------------------ rendu du sol

        /// <summary>
        /// Masque un instant les personnages d'origine pendant que la caméra du sol dessine la carte,
        /// pour qu'ils n'apparaissent pas "à plat" au sol. Toujours suivi de <see cref="RestoreHidden"/>.
        /// </summary>
        public void HideSourcesForFloor()
        {
            _hidden.Clear();
            foreach (Entity e in _list)
            {
                foreach (Part p in e.Parts)
                {
                    try
                    {
                        if (p.Src.enabled)
                        {
                            p.Src.enabled = false;
                            _hidden.Add(p.Src);
                        }
                    }
                    catch { /* objet détruit */ }
                }
            }
        }

        public void RestoreHidden()
        {
            foreach (Renderer r in _hidden)
            {
                try { r.enabled = true; } catch { /* objet détruit */ }
            }
            _hidden.Clear();
        }
    }
}
