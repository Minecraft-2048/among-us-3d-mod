using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace AmongUs3D
{
    /// <summary>
    /// Accès "défensif" à l'API d'Among Us. Chaque accès réel est dans une petite méthode
    /// [NoInlining] appelée depuis un try/catch : si un membre n'existe plus après une mise à jour
    /// du jeu, l'exception (MissingMethodException...) est levée à l'appel et rattrapée ici,
    /// et seule la fonctionnalité concernée est perdue (un avertissement unique dans le journal).
    /// </summary>
    internal enum MapKind { Skeld, Mira, Polus, Airship, Fungle, Lobby }

    /// <summary>Morceaux du dessin 2D d'un personnage (pour le remplacer par un modèle 3D).</summary>
    internal struct BodyInfo
    {
        public bool Normal;                       // corps classique (pas cheval, chercheur, "long"...)
        public SpriteRenderer Body, Skin, Visor, HatFront, HatBack;
        public int ColorId;
    }

    internal static class Game
    {
        private static readonly HashSet<string> _reported = new HashSet<string>();

        private static void Report(string what, Exception e)
        {
            if (_reported.Add(what))
                AmongUs3DPlugin.Logger.LogWarning($"[API du jeu] {what} indisponible : {e.GetType().Name}: {e.Message}");
        }

        // ------------------------------------------------------------------ joueur local

        public static PlayerControl LocalPlayer
        {
            get { try { return LocalPlayerImpl(); } catch (Exception e) { Report("PlayerControl.LocalPlayer", e); return null; } }
        }
        [MethodImpl(MethodImplOptions.NoInlining)] private static PlayerControl LocalPlayerImpl() => PlayerControl.LocalPlayer;

        public static void CollectPlayers(List<PlayerControl> into)
        {
            into.Clear();
            try { CollectPlayersImpl(into); } catch (Exception e) { Report("PlayerControl.AllPlayerControls", e); }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CollectPlayersImpl(List<PlayerControl> into)
        {
            var all = PlayerControl.AllPlayerControls;
            if (all == null) return;
            int n = all.Count;
            for (int i = 0; i < n; i++)
            {
                PlayerControl p = all[i];
                if (p != null) into.Add(p);
            }
        }

        public static bool IsDead(PlayerControl p)
        {
            try { return IsDeadImpl(p); } catch (Exception e) { Report("NetworkedPlayerInfo.IsDead", e); return false; }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsDeadImpl(PlayerControl p)
        {
            NetworkedPlayerInfo d = p.Data;
            return d != null && d.IsDead;
        }

        public static bool CanMove(PlayerControl p)
        {
            try { return CanMoveImpl(p); } catch (Exception e) { Report("PlayerControl.CanMove", e); return true; }
        }
        [MethodImpl(MethodImplOptions.NoInlining)] private static bool CanMoveImpl(PlayerControl p) => p.CanMove;

        public static bool InVent(PlayerControl p)
        {
            try { return InVentImpl(p); } catch (Exception e) { Report("PlayerControl.inVent", e); return false; }
        }
        [MethodImpl(MethodImplOptions.NoInlining)] private static bool InVentImpl(PlayerControl p) => p.inVent;

        /// <summary>Position des pieds (centre du collider), celle utilisée par le jeu pour les distances.</summary>
        public static Vector2 TruePosition(PlayerControl p)
        {
            try { return TruePositionImpl(p); }
            catch (Exception e)
            {
                Report("PlayerControl.GetTruePosition", e);
                Vector3 t = p.transform.position;
                return U.V2(t.x, t.y - 0.36f);
            }
        }
        [MethodImpl(MethodImplOptions.NoInlining)] private static Vector2 TruePositionImpl(PlayerControl p) => p.GetTruePosition();

        public static Rigidbody2D Body(PlayerControl p)
        {
            try { return BodyImpl(p); } catch (Exception e) { Report("PlayerControl.MyPhysics.body", e); return null; }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Rigidbody2D BodyImpl(PlayerControl p)
        {
            PlayerPhysics ph = p.MyPhysics;
            return ph != null ? ph.body : null;
        }

        // ------------------------------------------------------------------ HUD

        public static bool HudExists
        {
            get { try { return HudExistsImpl(); } catch (Exception e) { Report("HudManager.InstanceExists", e); return false; } }
        }
        [MethodImpl(MethodImplOptions.NoInlining)] private static bool HudExistsImpl() => HudManager.InstanceExists;

        public static HudManager Hud => HudExists ? HudImpl() : null;
        [MethodImpl(MethodImplOptions.NoInlining)] private static HudManager HudImpl() => HudManager.Instance;

        /// <summary>Direction voulue par le joueur (clavier, manette ou joystick tactile), comme lue par le jeu.</summary>
        public static Vector2 JoystickDelta
        {
            get
            {
                if (!HudExists) return default;
                try { return JoystickImpl(); } catch (Exception e) { Report("HudManager.joystick.DeltaL", e); return default; }
            }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Vector2 JoystickImpl()
        {
            var j = HudManager.Instance.joystick;
            return j != null ? j.DeltaL : default;
        }

        public static MeshRenderer ShadowQuad
        {
            get
            {
                if (!HudExists) return null;
                try { return ShadowQuadImpl(); } catch (Exception e) { Report("HudManager.ShadowQuad", e); return null; }
            }
        }
        [MethodImpl(MethodImplOptions.NoInlining)] private static MeshRenderer ShadowQuadImpl() => HudManager.Instance.ShadowQuad;

        // ------------------------------------------------------------------ carte / état de partie

        /// <summary>Racine de la carte en cours : le vaisseau (partie) ou le lobby.</summary>
        public static MonoBehaviour MapRoot(out bool isLobby)
        {
            isLobby = false;
            try
            {
                MonoBehaviour ship = ShipImpl();
                if (ship != null) return ship;
            }
            catch (Exception e) { Report("ShipStatus.Instance", e); }
            try
            {
                MonoBehaviour lobby = LobbyImpl();
                if (lobby != null) { isLobby = true; return lobby; }
            }
            catch (Exception e) { Report("LobbyBehaviour.Instance", e); }
            return null;
        }
        [MethodImpl(MethodImplOptions.NoInlining)] private static MonoBehaviour ShipImpl() => ShipStatus.Instance;
        [MethodImpl(MethodImplOptions.NoInlining)] private static MonoBehaviour LobbyImpl() => LobbyBehaviour.Instance;

        /// <summary>Rayon de vision actuel du joueur (rôle, réglages, sabotage des lumières).</summary>
        public static float LightRadius(PlayerControl lp, float fallback)
        {
            try { return LightRadiusImpl(lp, fallback); } catch (Exception e) { Report("ShipStatus.CalculateLightRadius", e); return fallback; }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static float LightRadiusImpl(PlayerControl lp, float fallback)
        {
            ShipStatus ship = ShipStatus.Instance;
            if (ship == null || lp == null) return fallback;
            NetworkedPlayerInfo data = lp.Data;
            if (data == null) return fallback;
            float r = ship.CalculateLightRadius(data);
            return (float.IsNaN(r) || r <= 0.01f) ? fallback : r;
        }

        /// <summary>Réunion, éjection, cinématique d'intro ou mini-jeu ouvert : on repasse en 2D.</summary>
        public static bool FullscreenUiOpen
        {
            get
            {
                bool any = false;
                try { any |= MeetingImpl(); } catch (Exception e) { Report("MeetingHud.Instance", e); }
                try { any |= ExileImpl(); } catch (Exception e) { Report("ExileController.Instance", e); }
                try { any |= MinigameImpl(); } catch (Exception e) { Report("Minigame.Instance", e); }
                try { any |= IntroImpl(); } catch (Exception e) { Report("IntroCutscene.Instance", e); }
                return any;
            }
        }
        [MethodImpl(MethodImplOptions.NoInlining)] private static bool MeetingImpl() => MeetingHud.Instance != null;
        [MethodImpl(MethodImplOptions.NoInlining)] private static bool ExileImpl() => ExileController.Instance != null;
        [MethodImpl(MethodImplOptions.NoInlining)] private static bool MinigameImpl() => Minigame.Instance != null;
        [MethodImpl(MethodImplOptions.NoInlining)] private static bool IntroImpl() => IntroCutscene.Instance != null;

        // ------------------------------------------------------------------ objets du monde

        public static void CollectDeadBodies(List<Component> into)
        {
            try { CollectDeadBodiesImpl(into); } catch (Exception e) { Report("DeadBody", e); }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CollectDeadBodiesImpl(List<Component> into)
        {
            foreach (DeadBody b in UnityEngine.Object.FindObjectsOfType<DeadBody>())
                if (b != null) into.Add(b);
        }

        public static Vector2 DeadBodyPosition(Component c)
        {
            try { return DeadBodyPositionImpl(c); }
            catch (Exception e)
            {
                Report("DeadBody.TruePosition", e);
                Vector3 t = c.transform.position;
                return U.V2(t.x, t.y);
            }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Vector2 DeadBodyPositionImpl(Component c)
        {
            DeadBody b = c.TryCast<DeadBody>();
            if (b != null) return b.TruePosition;
            Vector3 t = c.transform.position;
            return U.V2(t.x, t.y);
        }

        public static void CollectPets(List<Component> into)
        {
            try { CollectPetsImpl(into); } catch (Exception e) { Report("PetBehaviour", e); }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CollectPetsImpl(List<Component> into)
        {
            foreach (PetBehaviour p in UnityEngine.Object.FindObjectsOfType<PetBehaviour>())
                if (p != null) into.Add(p);
        }

        public static bool BelongsToPet(Component c)
        {
            try { return BelongsToPetImpl(c); } catch (Exception e) { Report("PetBehaviour (parent)", e); return false; }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool BelongsToPetImpl(Component c) => c.GetComponentInParent<PetBehaviour>() != null;

        /// <summary>Masque des calques qui bloquent la vision (ombres) selon le jeu.</summary>
        public static int ShadowMask
        {
            get { try { return ShadowMaskImpl(); } catch (Exception e) { Report("Constants.ShadowMask", e); return 0; } }
        }
        [MethodImpl(MethodImplOptions.NoInlining)] private static int ShadowMaskImpl() => Constants.ShadowMask;

        /// <summary>Masque des calques du vaisseau qui bloquent les déplacements selon le jeu.</summary>
        public static int ShipOnlyMask
        {
            get { try { return ShipOnlyMaskImpl(); } catch (Exception e) { Report("Constants.ShipOnlyMask", e); return 0; } }
        }
        [MethodImpl(MethodImplOptions.NoInlining)] private static int ShipOnlyMaskImpl() => Constants.ShipOnlyMask;

        public static bool IsDoor(Component c)
        {
            try { return IsDoorImpl(c); } catch (Exception e) { Report("OpenableDoor", e); return false; }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsDoorImpl(Component c) => c.GetComponentInParent<OpenableDoor>() != null;

        /// <summary>Masque de tout ce qui bloque les déplacements (vaisseau, objets, objets bas comme les tables).</summary>
        public static int PhysicalMask
        {
            get { try { return PhysicalMaskImpl(); } catch (Exception e) { Report("Constants.ShipAndAllObjectsMask", e); return ShipOnlyMask; } }
        }
        [MethodImpl(MethodImplOptions.NoInlining)] private static int PhysicalMaskImpl() => Constants.ShipAndAllObjectsMask;

        public static MapKind CurrentMapKind(MonoBehaviour mapRoot, bool isLobby)
        {
            if (isLobby) return MapKind.Lobby;
            string name = "";
            try { name = (mapRoot.gameObject.name ?? "").ToLowerInvariant(); } catch { /* ignoré */ }
            if (name.Contains("mira") || name.Contains("hq")) return MapKind.Mira;
            if (name.Contains("polus") || name.Contains("pb")) return MapKind.Polus;
            if (name.Contains("airship")) return MapKind.Airship;
            if (name.Contains("fungle")) return MapKind.Fungle;
            return MapKind.Skeld;
        }

        // ------------------------------------------------------------------ interface (souris)

        /// <summary>Carte (tâches, sabotage, admin) ouverte : il faut le curseur pour cliquer dessus.</summary>
        public static bool MapOpen
        {
            get { try { return MapOpenImpl(); } catch (Exception e) { Report("MapBehaviour.Instance", e); return false; } }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool MapOpenImpl()
        {
            MapBehaviour m = MapBehaviour.Instance;
            return m != null && m.IsOpen;
        }

        /// <summary>Clic gauche en vue 3D : même effet que le bouton Utiliser (ou Caresser) du jeu.</summary>
        public static void ClickUse()
        {
            if (!HudExists) return;
            try { ClickUseImpl(); } catch (Exception e) { Report("HudManager.UseButton", e); }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ClickUseImpl()
        {
            HudManager hud = HudManager.Instance;
            // 1) le bouton Utiliser s'il vise quelque chose (tâche, ordinateur du lobby, porte, bouton d'urgence...)
            UseButton use = hud.UseButton;
            bool useShown = use != null && use.isActiveAndEnabled;
            if (useShown && use.currentTarget != null) { use.DoClick(); return; }
            // 2) la bouche d'aération visée (imposteur, ingénieur)
            VentButton vent = hud.ImpostorVentButton;
            if (vent != null && vent.isActiveAndEnabled && vent.currentTarget != null) { vent.DoClick(); return; }
            // 3) caresser son animal, sinon le bouton Utiliser tel quel
            PetButton pet = hud.PetButton;
            if (pet != null && pet.isActiveAndEnabled) { pet.DoClick(); return; }
            if (useShown) use.DoClick();
        }

        /// <summary>
        /// Vrai si une fenêtre du jeu a besoin du curseur (chat, réglages, carte, personnalisation, mini-jeu...).
        /// Le simple fait de ne pas pouvoir bouger (échelle, plateforme, animation) ne suffit pas.
        /// </summary>
        public static bool UiWantsCursor(PlayerControl p, bool canMove)
        {
            if (MapOpen) return true;
            if (canMove) return false;
            try { return UiOpenImpl(p); }
            catch (Exception e) { Report("fenêtres du jeu (chat, réglages...)", e); return true; }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool UiOpenImpl(PlayerControl p)
        {
            if (Minigame.Instance != null) return true;
            if (PlayerCustomizationMenu.Instance != null) return true;
            if (GameSettingMenu.Instance != null) return true;
            if (HudManager.InstanceExists)
            {
                HudManager hud = HudManager.Instance;
                ChatController chat = hud.Chat;
                if (chat != null && chat.IsOpenOrOpening) return true;
                OptionsMenuBehaviour menu = hud.GameMenu;
                if (menu != null && menu.IsOpen) return true;
            }
            // Personnage libre de bouger mais bloqué quand même : une fenêtre que le mod ne connaît pas est ouverte.
            return p != null && p.moveable;
        }

        /// <summary>Vrai si le jeu a sa propre caméra d'interface, dessinée après la caméra indiquée.</summary>
        public static bool HasUiCameraAbove(Camera main)
        {
            if (!HudExists) return false;
            try { return HasUiCameraAboveImpl(main); } catch (Exception e) { Report("HudManager.UICamera", e); return false; }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool HasUiCameraAboveImpl(Camera main)
        {
            Camera ui = HudManager.Instance.UICamera;
            if (ui == null || !ui.isActiveAndEnabled || ui.targetTexture != null) return false;
            int uiLayer = U.Layer("UI");
            if (uiLayer >= 0 && (ui.cullingMask & (1 << uiLayer)) == 0) return false;
            return ui.depth > main.depth + 0.5f;
        }

        /// <summary>Le jeu a-t-il le focus du clavier et de la souris ?</summary>
        public static bool AppFocused
        {
            get { try { return Application.isFocused; } catch { return true; } }
        }

        // ------------------------------------------------------------------ animations des personnages

        /// <summary>Ce qu'il faut pour savoir, à chaque image et sans rien chercher, ce que fait un personnage.</summary>
        internal struct AnimRefs
        {
            public PowerTools.SpriteAnim Animator;
            public IntPtr Run, Idle, GhostIdle, GhostAngel;
        }

        public static bool TryAnimRefs(PlayerControl p, out AnimRefs refs)
        {
            refs = default;
            try { AnimRefsImpl(p, ref refs); return refs.Animator != null; }
            catch (Exception e) { Report("PlayerPhysics.Animations", e); return false; }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AnimRefsImpl(PlayerControl p, ref AnimRefs refs)
        {
            PlayerPhysics ph = p.MyPhysics;
            if (ph == null) return;
            PlayerAnimations a = ph.Animations;
            if (a == null) return;
            PlayerAnimationGroup g = a.group;
            if (g == null) return;
            AnimationClip run = g.RunAnim, idle = g.IdleAnim;
            refs.Run = run != null ? run.Pointer : IntPtr.Zero;
            refs.Idle = idle != null ? idle.Pointer : IntPtr.Zero;
            AnimationClip ghost = g.GhostIdleAnim, angel = g.GhostGuardianAngelAnim;
            refs.GhostIdle = ghost != null ? ghost.Pointer : IntPtr.Zero;
            refs.GhostAngel = angel != null ? angel.Pointer : IntPtr.Zero;
            refs.Animator = a.Animator;
        }

        /// <summary>0 = animation spéciale (bouche d'aération, échelle, apparition, fantôme...), 1 = immobile, 2 = course.</summary>
        public static int AnimKind(in AnimRefs refs)
        {
            try { return AnimKindImpl(refs); } catch (Exception e) { Report("SpriteAnim.GetCurrentAnimation", e); return 0; }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int AnimKindImpl(in AnimRefs refs)
        {
            AnimationClip cur = refs.Animator.GetCurrentAnimation();
            if (cur == null) return 0;
            IntPtr ptr = cur.Pointer;
            if (ptr == refs.Run) return 2;
            if (ptr == refs.Idle) return 1;
            if (ptr != IntPtr.Zero && (ptr == refs.GhostIdle || ptr == refs.GhostAngel)) return 3;
            return 0;
        }

        // ------------------------------------------------------------------ animaux et caresse

        /// <summary>Main qui caresse et cœurs (ils appartiennent au joueur, mais s'affichent sur l'animal).</summary>
        public static bool TryPettingRenderers(PlayerControl p, out SpriteRenderer hand, out SpriteRenderer hearts)
        {
            hand = hearts = null;
            try { PettingRenderersImpl(p, ref hand, ref hearts); }
            catch (Exception e) { Report("CosmeticsLayer.PettingHand", e); }
            return hand != null || hearts != null;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PettingRenderersImpl(PlayerControl p, ref SpriteRenderer hand, ref SpriteRenderer hearts)
        {
            CosmeticsLayer c = p.cosmetics;
            if (c == null) return;
            PlayerPettingHand h = c.PettingHand;
            if (h == null) return;
            hand = h.HandSprite;
            hearts = h.hearts;
        }

        /// <summary>Joueur à qui appartient un animal (null si inconnu).</summary>
        public static PlayerControl PetOwner(Component pet)
        {
            try { return PetOwnerImpl(pet); } catch (Exception e) { Report("PetBehaviour.TargetPlayer", e); return null; }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static PlayerControl PetOwnerImpl(Component pet)
        {
            PetBehaviour b = pet.TryCast<PetBehaviour>();
            return b != null ? b.TargetPlayer : null;
        }

        /// <summary>L'animal est-il en train d'être caressé ?</summary>
        public static bool PetBeingPet(Component pet)
        {
            try { return PetBeingPetImpl(pet); } catch (Exception e) { Report("PetBehaviour.beingPet", e); return false; }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool PetBeingPetImpl(Component pet)
        {
            PetBehaviour b = pet.TryCast<PetBehaviour>();
            return b != null && b.beingPet;
        }

        /// <summary>Si le joueur caresse son animal : position de l'animal sur la carte.</summary>
        public static bool TryPettingTarget(PlayerControl p, out float x, out float y)
        {
            x = y = 0f;
            if (p == null) return false;
            try { return PettingTargetImpl(p, ref x, ref y); }
            catch (Exception e) { Report("PlayerControl.petting", e); return false; }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool PettingTargetImpl(PlayerControl p, ref float x, ref float y)
        {
            if (!p.petting) return false;
            CosmeticsLayer c = p.cosmetics;
            PetBehaviour pet = c != null ? c.CurrentPet : null;
            if (pet == null) return false;
            Vector3 pos = pet.transform.position;
            x = pos.x;
            y = pos.y;
            return true;
        }

        /// <summary>Identifiant du chapeau porté (IntPtr.Zero si aucun ou inconnu).</summary>
        public static IntPtr HatKey(PlayerControl p)
        {
            try { return HatKeyImpl(p); } catch (Exception e) { Report("HatParent.Hat", e); return IntPtr.Zero; }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IntPtr HatKeyImpl(PlayerControl p)
        {
            CosmeticsLayer c = p.cosmetics;
            HatParent hat = c != null ? c.hat : null;
            HatData data = hat != null ? hat.Hat : null;
            return data != null ? data.Pointer : IntPtr.Zero;
        }

        /// <summary>Le jeu affiche-t-il ce joueur (faux dans une bouche d'aération, pendant certains effets...) ?</summary>
        public static bool PlayerVisible(PlayerControl p)
        {
            try { return PlayerVisibleImpl(p); } catch (Exception e) { Report("PlayerControl.Visible", e); return true; }
        }
        [MethodImpl(MethodImplOptions.NoInlining)] private static bool PlayerVisibleImpl(PlayerControl p) => p.Visible;

        /// <summary>Petit message à gauche de l'écran (celui des changements de réglages du jeu), visible de soi seul.</summary>
        public static void Notify(string text)
        {
            AmongUs3DPlugin.Logger.LogInfo(text);
            if (!HudExists) return;
            try { NotifyImpl(text); }
            catch (Exception e) { Report("HudManager.Notifier", e); }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void NotifyImpl(string text)
        {
            NotificationPopper pop = HudManager.Instance.Notifier;
            if (pop == null) return;
            LobbyNotificationMessage msg = UnityEngine.Object.Instantiate(pop.notificationMessageOrigin, pop.transform);
            msg.transform.localPosition = U.V3(0f, 0f, -2f);
            msg.SetUp(text, pop.settingsChangeSprite, pop.settingsChangeColor, (Action)(() => pop.OnMessageDestroy(msg)));
            pop.ShiftMessages();
            pop.AddMessageToQueue(msg);
        }

        // ------------------------------------------------------------------ personnages (modèles 3D)

        public static bool TryBodyInfo(PlayerControl p, out BodyInfo info)
        {
            info = default;
            try { BodyInfoImpl(p, ref info); return info.Body != null; }
            catch (Exception e) { Report("PlayerControl.cosmetics", e); return false; }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void BodyInfoImpl(PlayerControl p, ref BodyInfo info)
        {
            CosmeticsLayer c = p.cosmetics;
            if (c == null) return;
            info.Normal = c.bodyType == PlayerBodyTypes.Normal;
            PlayerBodySprite s = c.currentBodySprite;
            if (s != null) info.Body = s.BodySprite;
            SkinLayer skin = c.skin;
            if (skin != null) info.Skin = skin.layer;
            VisorLayer visor = c.visor;
            if (visor != null) info.Visor = visor.Image;
            HatParent hat = c.hat;
            if (hat != null) { info.HatFront = hat.FrontLayer; info.HatBack = hat.BackLayer; }
            info.ColorId = c.ColorId;
        }

        /// <summary>Couleurs officielles d'un personnage : principale, ombre, visière.</summary>
        public static bool TryPlayerColors(int colorId, out Color32 main, out Color32 shadow, out Color32 visor)
        {
            main = U.C32(197, 17, 17); shadow = U.C32(122, 8, 56); visor = U.C32(149, 202, 220);
            try { return PlayerColorsImpl(colorId, ref main, ref shadow, ref visor); }
            catch (Exception e) { Report("Palette.PlayerColors", e); return false; }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool PlayerColorsImpl(int colorId, ref Color32 main, ref Color32 shadow, ref Color32 visor)
        {
            var colors = Palette.PlayerColors;
            var shadows = Palette.ShadowColors;
            if (colors == null || colorId < 0 || colorId >= colors.Length) return false;
            main = colors[colorId];
            if (shadows != null && colorId < shadows.Length) shadow = shadows[colorId];
            visor = Palette.VisorColor;
            return true;
        }

        /// <summary>Numéro de couleur du joueur à qui appartient un cadavre (-1 si inconnu).</summary>
        public static int DeadBodyColorId(Component c)
        {
            try { return DeadBodyColorIdImpl(c); } catch (Exception e) { Report("DeadBody.ParentId", e); return -1; }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int DeadBodyColorIdImpl(Component c)
        {
            DeadBody b = c.TryCast<DeadBody>();
            GameData data = GameData.Instance;
            if (b == null || data == null) return -1;
            NetworkedPlayerInfo info = data.GetPlayerById(b.ParentId);
            if (info == null) return -1;
            var outfit = info.DefaultOutfit;
            return outfit != null ? outfit.ColorId : -1;
        }
    }
}
