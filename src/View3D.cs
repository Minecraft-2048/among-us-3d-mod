using System;
using UnityEngine;

namespace AmongUs3D
{
    /// <summary>
    /// Chef d'orchestre : décide quand la vue 3D est active, construit la carte 3D,
    /// et met à jour caméras, murs, personnages, sol et mini-carte à chaque image.
    /// </summary>
    internal sealed class View3D
    {
        public static View3D Instance { get; private set; }

        // Lus par MovementPatch (FixedUpdate, 50 fois par seconde).
        public static bool MovementOverride { get; private set; }
        public static float CurrentYaw { get; private set; }
        public static bool TankMode { get; private set; }

        private static float _fixX, _fixY, _fixTime = -1f;

        /// <summary>Position du corps physique avant le pas de simulation (pour une caméra fluide à haut FPS).</summary>
        public static void RecordFixedStep(Vector2 bodyPosition)
        {
            _fixX = bodyPosition.x;
            _fixY = bodyPosition.y;
            _fixTime = Time.fixedTime;
        }

        private readonly Controls _controls = new Controls();
        private readonly WallSystem _walls = new WallSystem();
        private readonly ProxySystem _proxies = new ProxySystem();
        private readonly InventoryPreview _inventory = new InventoryPreview();
        private readonly OnScreenButtons _buttons = new OnScreenButtons();
        private readonly ArrowSystem _arrows = new ArrowSystem();
        private readonly KeyHints _keyHints = new KeyHints();
        private bool _petLook;
        private float _petLookEnd, _prePetPitch, _petPull;
        private readonly CachedMask _floorHidden = new CachedMask();
        private readonly CachedColor _background = new CachedColor();

        private Rig _rig;
        private bool _want3D, _third, _minimap, _active, _failSafe;
        private IntPtr _mapPtr;
        private bool _isLobby;
        private int _layer3D = -1, _layerMini = -1;
        private float _rigRetryAt;
        private int _errors;
        private float _errorWindowStart;
        private float _activatedAt;
        private bool _autoDiagDone, _announced;

        private PlayerControl _local;
        private Camera _main;
        private bool _inWorld, _canMove;
        // Vues particulières : au-dessus du conduit où l'on se trouve, derrière le joueur qu'un fantôme suit.
        private bool _ventView, _ventArrowsOff, _windowWasOpen;
        private PlayerControl _haunted;
        private int _chatOpenedFrame = -100;
        private float _groundZ; // hauteur du sol sous la caméra (relief), lissée
        // Zoom de la 3e personne (molette) : 1 = distance des réglages ; enregistré quand la molette s'arrête.
        private const float ZoomMin = 0.4f, ZoomMax = 2.6f;
        private float _zoom = -1f, _zoomSaveAt = -1f, _ownAlpha = 1f;
        private readonly System.Collections.Generic.List<Vector2> _ventArrowsAt = new System.Collections.Generic.List<Vector2>();

        // Délégués créés une seule fois (évite des allocations à chaque image).
        private readonly Func<float, float, bool> _isOverButton;
        private readonly Action _toggle3D, _toggleCamera, _toggleMinimap;

        public static void Create() => Instance = new View3D();

        private View3D()
        {
            _want3D = ModConfig.Enable3DAtStart.Value;
            _third = ModConfig.StartInThirdPerson.Value;
            _minimap = ModConfig.MinimapAtStart.Value;
            _isOverButton = _buttons.Contains;
            _toggle3D = Toggle3D;
            _toggleCamera = ToggleCamera;
            _toggleMinimap = ToggleMinimap;
        }

        // ------------------------------------------------------------------ protections

        public void SafeUpdate()
        {
            try { Update(); }
            catch (Exception e) { OnError(e); }
        }

        public void SafeLateUpdate()
        {
            try { LateUpdate(); }
            catch (Exception e) { OnError(e); }
            finally { _proxies.RestoreHidden(); }
        }

        private void OnError(Exception e)
        {
            float now = Time.realtimeSinceStartup;
            if (now - _errorWindowStart > 10f) { _errorWindowStart = now; _errors = 0; }
            _errors++;
            if (_errors <= 3) AmongUs3DPlugin.Logger.LogError("Erreur AmongUs3D : " + e);
            if (_errors < 30 || _failSafe) return;
            // Trop d'erreurs : on rend la main au jeu normal plutôt que de le gêner.
            _failSafe = true;
            AmongUs3DPlugin.Logger.LogError("Trop d'erreurs : vue 3D désactivée pour cette session (le jeu repasse en 2D normale). " +
                                            "Envoyez le fichier BepInEx/LogOutput.log pour corriger le problème.");
            try { TearDown(); } catch { /* ignoré */ }
        }

        // ------------------------------------------------------------------ Update

        private void Update()
        {
            _inventory.Update(Time.unscaledDeltaTime, _layer3D, _layerMini);
            HandleHotkeys();

            _local = Game.LocalPlayer;
            try { _main = Camera.main; } catch { _main = null; }
            MonoBehaviour map = null;
            bool lobby = false;
            if (_local != null && _main != null) map = Game.MapRoot(out lobby);
            _inWorld = map != null;

            // Réunion, éjection, intro : écrans pleins en 2D. Une tâche s'affiche en 2D par-dessus la vue 3D
            // (réglage TachesSurFond3D). Un fantôme qui suit un joueur reste en 3D.
            bool minigame = _inWorld && Game.MinigameOpen;
            PlayerControl haunted = null;
            bool haunting = minigame && Game.Haunting(out haunted);
            bool fullscreenUi = _inWorld && (Game.CutsceneOpen || (minigame && !haunting && !ModConfig.TasksOver3D.Value));
            bool inVentNow = _inWorld && Game.InVent(_local);
            bool render = _want3D && _inWorld && !fullscreenUi && !(inVentNow && ModConfig.Show2DInVent.Value) && !_failSafe;

            if (render && !EnsureRig()) render = false;
            if (render && map.Pointer != _mapPtr) BuildMap(map, lobby);
            if (render && !_active) Activate();
            else if (!render && _active) Deactivate();

            _canMove = _inWorld && Game.CanMove(_local);
            _ventView = _active && inVentNow;
            _haunted = _active && haunting ? haunted : null;
            if (_ventView != _ventArrowsOff)
            {
                // Vue 3D d'un conduit : c'est le mod qui clique les flèches (voir plus bas).
                _ventArrowsOff = _ventView;
                if (!_ventView) Game.SetVentArrowsClickable(true);
            }
            if (_ventView) Game.SetVentArrowsClickable(false); // les flèches changent à chaque conduit
            _controls.AllowDragLook = _ventView;
            HandleWindowKeys();
            HandleZoom();
            if (_active)
            {
                // La souris dirige la caméra (curseur verrouillé) sauf si une fenêtre du jeu a besoin du curseur,
                // si la touche "libérer la souris" est maintenue, ou si le jeu n'est plus au premier plan.
                bool capture = _controls.MouseLook
                               && Game.AppFocused
                               && !InputCompat.Key(ModConfig.KeyFreeCursor.Value)
                               && !_ventView
                               && !Game.UiWantsCursor(_local, _canMove);
                _controls.SetCapture(capture);
                _controls.Update(Time.deltaTime, _canMove, _isOverButton);

                // Clic gauche = Utiliser (comme la touche du jeu), tant que la souris dirige la caméra.
                if (_controls.Captured && _controls.CaptureAge > 3 && _canMove
                    && ModConfig.ClickToUse.Value && InputCompat.MouseButtonDown(0))
                    Game.ClickUse();

                // Dans un conduit : un clic sur une flèche dessinée au sol emmène au conduit voisin.
                if (_ventView && _rig != null && InputCompat.MouseButtonDown(0)
                    && InputCompat.TryMousePosition(out float mx, out float my))
                {
                    bool onFloor = _rig.ScreenToFloor(mx, my, _groundZ, out float ax, out float ay);
                    bool moved = onFloor && Game.ClickVentArrow(ax, ay, 0.8f);
                    if (ModConfig.TestTools.Value)
                        AmongUs3DPlugin.Logger.LogInfo(FormattableString.Invariant(
                            $"Conduit : clic écran ({mx:0}, {my:0}) -> sol ({ax:0.00}, {ay:0.00}) touché={onFloor} flèche={moved} | {Game.DescribeVentArrows()}"));
                }
            }
            else
            {
                _controls.SetCapture(false);
            }

            MovementOverride = _active;
            CurrentYaw = _controls.Yaw;
            TankMode = _controls.TankActive;

            bool showButtons = ButtonsEnabled() && _inWorld && !fullscreenUi && !_failSafe;
            _buttons.Update(_main, showButtons, _want3D, _third, _minimap, _toggle3D, _toggleCamera, _toggleMinimap);

            // Touches du clavier écrites sur les boutons du jeu (Tuer, Sabotage...)
            _keyHints.Update(ModConfig.KeyHintsOnButtons.Value && !Platform.IsMobile && _inWorld && !_failSafe);
        }

        /// <summary>
        /// En 1re personne, pendant qu'on caresse son animal, la caméra se tourne vers lui et baisse les yeux
        /// pour voir l'animation ; elle remonte ensuite à sa hauteur d'avant.
        /// </summary>
        private void UpdatePettingLook(float px, float py, float dt)
        {
            float tx = 0f, ty = 0f;
            bool petting = !_third && !_ventView && _haunted == null && ModConfig.LookAtPetWhenPetting.Value
                           && Game.TryPettingTarget(_local, out tx, out ty);
            if (petting)
            {
                if (!_petLook)
                {
                    _petLook = true;
                    _prePetPitch = _controls.Pitch;
                }
                float dx = tx - px, dy = ty - py, d = MathF.Sqrt(dx * dx + dy * dy);
                if (d > 0.05f)
                {
                    float eye = M.Clamp(ModConfig.EyeHeight.Value, 0.1f, 3f);
                    float yaw = MathF.Atan2(dx, dy) / M.Deg2Rad;
                    float pitch = -MathF.Atan2(eye - 0.15f, d + _petPull) / M.Deg2Rad;
                    _controls.Steer(yaw, pitch, dt, 6f);
                }
                return;
            }
            if (_petLook)
            {
                _petLook = false;
                _petLookEnd = Time.unscaledTime + 0.8f;
            }
            if (Time.unscaledTime < _petLookEnd) _controls.Steer(float.NaN, _prePetPitch, dt, 5f);
        }

        /// <summary>
        /// Molette : rapproche ou éloigne la caméra de la 3e personne. Tout près, on passe en 1re personne ;
        /// depuis la 1re personne, reculer la molette repasse derrière le personnage.
        /// </summary>
        private void HandleZoom()
        {
            if (_zoom < 0f) _zoom = M.Clamp(ModConfig.ThirdPersonZoom.Value, ZoomMin, ZoomMax);
            if (_zoomSaveAt > 0f && Time.unscaledTime >= _zoomSaveAt)
            {
                _zoomSaveAt = -1f;
                if (MathF.Abs(ModConfig.ThirdPersonZoom.Value - _zoom) > 0.005f) ModConfig.ThirdPersonZoom.Value = _zoom;
            }
            if (!_active || !ModConfig.WheelZoom.Value || !Game.AppFocused || (_inWorld && Game.AnyWindowOpen)) return;
            float wheel = InputCompat.MouseScroll;
            if (MathF.Abs(wheel) < 0.01f) return;
            wheel = M.Clamp(wheel, -3f, 3f);
            bool forced = _ventView || _haunted != null; // vues toujours "de derrière"
            if (!_third && !forced)
            {
                if (wheel < 0f) { _third = true; _zoom = ZoomMin; } // on recule : la caméra sort du personnage
                else return;
            }
            else
            {
                float wanted = _zoom * MathF.Exp(-wheel * 0.16f);
                if (wanted < ZoomMin * 0.97f && _third && !forced && wheel > 0f) _third = false; // tout près : 1re personne
                _zoom = M.Clamp(wanted, ZoomMin, ZoomMax);
            }
            _zoomSaveAt = Time.unscaledTime + 1.5f;
        }

        /// <summary>En vue 3D : une touche ouvre le chat (T), une autre les paramètres (Échap) quand rien n'est ouvert.</summary>
        private void HandleWindowKeys()
        {
            bool open = _inWorld && Game.AnyWindowOpen;
            if (_active && !open && !_windowWasOpen)
            {
                KeyCode chat = ModConfig.KeyChat.Value, settings = ModConfig.KeySettings.Value;
                if (chat != KeyCode.None && InputCompat.KeyDown(chat) && Game.TryOpenChat()) _chatOpenedFrame = Time.frameCount;
                else if (settings != KeyCode.None && InputCompat.KeyDown(settings)) Game.TryOpenSettings();
            }
            // La lettre de la touche ne doit pas se retrouver tapée dans le chat qu'elle vient d'ouvrir.
            if (Time.frameCount - _chatOpenedFrame <= 3) Game.DropChatKey(ModConfig.KeyChat.Value.ToString());
            _windowWasOpen = open;
        }

        private static bool ButtonsEnabled()
        {
            switch (ModConfig.OnScreenButtons.Value)
            {
                case ToggleMode.Always: return true;
                case ToggleMode.Never: return false;
                default: return Platform.IsMobile;
            }
        }

        private void HandleHotkeys()
        {
            if (InputCompat.KeyDown(ModConfig.KeyToggle3D.Value))
            {
                if (_inventory.MenuOpen) _inventory.Toggle(); // inventaire ouvert : F5 bascule l'aperçu du personnage
                else Toggle3D();
            }
            if (InputCompat.KeyDown(ModConfig.KeyToggleCamera.Value)) ToggleCamera();
            if (InputCompat.KeyDown(ModConfig.KeyToggleMinimap.Value)) ToggleMinimap();
            if (InputCompat.KeyDown(ModConfig.KeyMouseCapture.Value))
            {
                bool on = _controls.SetMouseLook(!_controls.MouseLook);
                Game.Notify(on ? "Souris : dirige la caméra (Alt maintenu = curseur)" : "Souris : curseur libre (flèches = tourner)");
            }
            if (InputCompat.KeyDown(ModConfig.KeySensitivityUp.Value)) ChangeSensitivity(1.2f);
            if (InputCompat.KeyDown(ModConfig.KeySensitivityDown.Value)) ChangeSensitivity(1f / 1.2f);
            if (InputCompat.KeyDown(ModConfig.KeyScreenshot.Value)) TakeScreenshot();
            if (InputCompat.KeyDown(ModConfig.KeyDiagnostics.Value))
            {
                if (ModConfig.TestTools.Value && InputCompat.Key(KeyCode.LeftControl)) RunTestCommand();
                else DumpDiagnostics();
            }
        }

        /// <summary>Outil de mise au point (réglage OutilsDeTest, Entraînement seulement) : exécute BepInEx/AmongUs3D-test.txt.</summary>
        private static void RunTestCommand()
        {
            try
            {
                string file = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "AmongUs3D-test.txt");
                if (!System.IO.File.Exists(file)) return;
                string[] w = System.IO.File.ReadAllText(file).Trim().Split(' ');
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                if (w.Length == 3 && w[0] == "tp"
                    && float.TryParse(w[1], System.Globalization.NumberStyles.Float, inv, out float x)
                    && float.TryParse(w[2], System.Globalization.NumberStyles.Float, inv, out float y))
                {
                    bool ok = Game.TeleportInFreeplay(x, y);
                    AmongUs3DPlugin.Logger.LogInfo(ok ? $"Outil de test : personnage placé en ({w[1]}, {w[2]})." : "Outil de test : refusé hors du mode Entraînement.");
                }
                else if (w.Length >= 2 && w[0] == "catalogue")
                {
                    // "catalogue chapeaux 0 100" : inventaire ouvert, images 3D des objets du jeu
                    int start = w.Length >= 3 && int.TryParse(w[2], out int s0) ? s0 : 0;
                    int count = w.Length >= 4 && int.TryParse(w[3], out int n0) ? n0 : 100000;
                    Instance?._inventory.StartCatalog(w[1], start, count);
                }
            }
            catch (Exception e)
            {
                AmongUs3DPlugin.Logger.LogWarning("Outil de test : " + e.Message);
            }
        }

        /// <summary>Rend la caméra plus ou moins vive (souris, clavier et tactile ensemble) et l'enregistre.</summary>
        private static void ChangeSensitivity(float factor)
        {
            ModConfig.MouseSensitivity.Value = M.Clamp(ModConfig.MouseSensitivity.Value * factor, 0.02f, 3f);
            ModConfig.TurnSpeed.Value = M.Clamp(ModConfig.TurnSpeed.Value * factor, 30f, 1200f);
            ModConfig.TouchLookSpeed.Value = M.Clamp(ModConfig.TouchLookSpeed.Value * factor, 30f, 1500f);
            int percent = (int)MathF.Round(ModConfig.MouseSensitivity.Value / 0.25f * 100f);
            Game.Notify($"Sensibilité de la caméra : {percent} %");
        }

        private static void TakeScreenshot()
        {
            try
            {
                string dir = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "AmongUs3D-captures");
                System.IO.Directory.CreateDirectory(dir);
                string file = System.IO.Path.Combine(dir, "capture-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png");
                ScreenCapture.CaptureScreenshot(file);
                AmongUs3DPlugin.Logger.LogInfo("Capture d'écran : " + file);
            }
            catch (Exception e)
            {
                AmongUs3DPlugin.Logger.LogWarning("Capture d'écran impossible : " + e.Message);
            }
        }

        private void Toggle3D()
        {
            _want3D = !_want3D;
            if (_failSafe && _want3D)
            {
                AmongUs3DPlugin.Logger.LogWarning("La vue 3D a été désactivée suite à des erreurs ; redémarrez le jeu pour réessayer.");
                _want3D = false;
            }
            AmongUs3DPlugin.Logger.LogInfo("Vue 3D : " + (_want3D ? "activée" : "désactivée"));
        }

        private void ToggleCamera()
        {
            _third = !_third;
            AmongUs3DPlugin.Logger.LogInfo(_third ? "Caméra : troisième personne" : "Caméra : première personne");
        }

        private void ToggleMinimap() => _minimap = !_minimap;

        private void DumpDiagnostics()
        {
            Diagnostics.Dump(_rig, _walls, _proxies, _controls, _keyHints, _layer3D, _layerMini, _active, _want3D, _local, _main);
        }

        // ------------------------------------------------------------------ cycle de vie

        private bool EnsureRig()
        {
            if (_rig != null && (!_rig.IsAlive || _rig.Main.Pointer != _main.Pointer))
                TearDown(); // changement de scène / nouvelle caméra principale
            if (_rig != null) return true;
            if (Time.unscaledTime < _rigRetryAt) return false;
            try
            {
                if (_layer3D < 0)
                {
                    var avoid = new System.Collections.Generic.HashSet<int>();
                    _layer3D = U.PickFreeLayer(ModConfig.Layer3D.Value, avoid);
                    avoid.Add(_layer3D);
                    _layerMini = U.PickFreeLayer(ModConfig.LayerMinimap.Value, avoid);
                    AmongUs3DPlugin.Logger.LogInfo($"Calques utilisés : 3D = {_layer3D}, mini-carte = {_layerMini}");
                }
                _rig = Rig.Create(_main, _layer3D, _layerMini);
                _mapPtr = IntPtr.Zero;
                return true;
            }
            catch (Exception e)
            {
                AmongUs3DPlugin.Logger.LogError("Impossible de créer les caméras 3D (nouvel essai dans 10 s) : " + e);
                _rig = null;
                _rigRetryAt = Time.unscaledTime + 10f;
                return false;
            }
        }

        private void BuildMap(MonoBehaviour map, bool lobby)
        {
            _mapPtr = map.Pointer;
            _isLobby = lobby;
            try
            {
                _walls.Build(map, lobby, _rig.Root.transform, _layer3D, _rig.FloorMaterial);
            }
            catch (Exception e)
            {
                AmongUs3DPlugin.Logger.LogError("Construction des murs impossible (la vue 3D continue sans murs) : " + e);
            }
            _proxies.Reset(_rig.Root.transform, _layer3D, 1 << _layerMini);
            _controls.ResetOrientation();
        }

        private void Activate()
        {
            _rig.SetActive(true);
            _active = true;
            _activatedAt = Time.unscaledTime;
            if (!_announced)
            {
                _announced = true;
                if (Platform.IsMobile)
                    AmongUs3DPlugin.Logger.LogInfo("Vue 3D active : joystick = se déplacer, glisser à droite de l'écran = tourner, boutons en haut = options.");
                else if (_controls.MouseLook)
                    Game.Notify("Vue 3D : souris = regarder, clic gauche = utiliser, Alt maintenu = curseur, F8 = souris libre");
                else
                    Game.Notify("Vue 3D : flèches gauche/droite = tourner, F8 = caméra à la souris");
            }
        }

        private void Deactivate()
        {
            _active = false;
            _ventView = false;
            _haunted = null;
            if (_ventArrowsOff) { _ventArrowsOff = false; Game.SetVentArrowsClickable(true); }
            MovementOverride = false;
            _proxies.RestoreHidden();
            if (_rig != null) _rig.SetActive(false);
            _controls.SetCapture(false);
        }

        private void TearDown()
        {
            Deactivate();
            _walls.Clear();
            _proxies.Clear();
            _rig?.Destroy();
            _rig = null;
            _mapPtr = IntPtr.Zero;
        }

        // ------------------------------------------------------------------ LateUpdate

        private void LateUpdate()
        {
            if (!_active || _rig == null || _local == null || _main == null) return;
            if (!_rig.IsAlive)
            {
                TearDown();
                return;
            }

            float dt = Time.deltaTime;
            // La caméra suit le joueur ; un fantôme qui "hante" quelqu'un le regarde, lui.
            float px, py;
            bool following = _haunted != null && U.Alive(_haunted);
            if (following)
            {
                Vector2 hp = Game.TruePosition(_haunted);
                px = hp.x; py = hp.y;
            }
            else
            {
                SmoothedPosition(Game.TruePosition(_local), out px, out py);
            }
            // Vue de derrière imposée : au-dessus du conduit où l'on se trouve, et derrière le joueur suivi.
            bool third = _third || _ventView || following;

            // Relief (The Fungle) : la caméra suit la hauteur du sol, en douceur (une échelle se grimpe, un saut de zone se coupe).
            Terrain terrain = _walls.Ground;
            float groundNow = terrain.Height(px, py);
            if (MathF.Abs(groundNow - _groundZ) > 3f) _groundZ = groundNow;
            else _groundZ += (groundNow - _groundZ) * (1f - MathF.Exp(-dt * 14f));

            // ---- vision (rayon du jeu : rôle, réglages, sabotage des lumières)
            bool simulate = ModConfig.SimulateVision.Value;
            bool full = !simulate || _isLobby || Game.IsDead(_local);
            float radius = full ? 1000f : Game.LightRadius(_local, 4f);
            float half = _rig.FloorHalf;
            var vision = new VisionParams
            {
                Cx = px, Cy = py, Radius = radius, Full = full,
                Ambient = M.Clamp01(ModConfig.AmbientInDarkness.Value),
                FadeStart = half * 0.6f, FadeEnd = half * 0.97f,
            };

            // ---- caresse : regarder l'animal
            UpdatePettingLook(px, py, dt);

            // ---- position et orientation de la caméra
            float yaw = _controls.Yaw, pitch = _controls.Pitch;
            M.YawBasis(yaw, out float fx, out float fy, out _, out _);
            float camX = px, camY = py, camZ, lookPitch = pitch, ownDistance = 10f;
            if (!third)
            {
                camZ = -M.Clamp(ModConfig.EyeHeight.Value, 0.1f, 3f);
                // Pendant une caresse, la caméra recule un peu : l'animal est juste à nos pieds.
                float wantPull = _petLook ? 0.8f : 0f;
                _petPull += (wantPull - _petPull) * (1f - MathF.Exp(-dt * 5f));
                if (_petPull > 0.01f)
                {
                    float t = _walls.ClipFraction(px, py, px - fx * _petPull, py - fy * _petPull);
                    float pull = t < 1f ? MathF.Max(0f, _petPull * t - 0.1f) : _petPull;
                    camX = px - fx * pull;
                    camY = py - fy * pull;
                }
            }
            else
            {
                // La caméra tourne autour de la tête du personnage : regarder vers le bas la fait monter
                // (et inversement), comme la vue arrière de Minecraft. La molette règle son éloignement.
                const float pivot = 0.55f;
                float zoom = _zoom > 0f ? _zoom : 1f;
                float dist = M.Clamp(ModConfig.ThirdPersonDistance.Value, 0.5f, 8f) * zoom;
                float h = pivot + (M.Clamp(ModConfig.ThirdPersonHeight.Value, 0.2f, 6f) - pivot) * zoom;
                float rest = MathF.Atan2(h - pivot, dist) / M.Deg2Rad;
                float reach = MathF.Sqrt(dist * dist + (h - pivot) * (h - pivot));
                float er = M.Clamp(rest - pitch, -8f, 84f) * M.Deg2Rad;
                float back = reach * MathF.Cos(er), rise = reach * MathF.Sin(er);
                // La caméra ne traverse jamais un mur : elle se rapproche du personnage. Sous un plafond, elle
                // ne le dépasse pas non plus ; sans plafond, elle peut monter au-dessus des murs.
                float wallsTop = M.Clamp(ModConfig.WallHeight.Value, 0.3f, 10f);
                bool roofed = CeilingWanted() && !terrain.Active;
                float k = 1f;
                if (back > 0.05f && (roofed || pivot + rise < wallsTop))
                {
                    float t = _walls.ClipFractionWide(px, py, px - fx * back, py - fy * back, 0.14f);
                    if (t < 1f) k = MathF.Max(0.03f, t - 0.22f / back);
                }
                if (roofed && rise > 0.01f) k = MathF.Min(k, MathF.Max(0.03f, (wallsTop - 0.16f - pivot) / rise));
                camX = px - fx * back * k;
                camY = py - fy * back * k;
                camZ = -MathF.Max(0.12f, pivot + rise * k);
                lookPitch = pitch - rest; // regarde toujours la tête du personnage
                ownDistance = reach * k;
            }
            // Caméra collée au personnage (coin de mur, zoom au plus près) : il s'efface pour ne pas boucher la vue.
            float ownWanted = third ? M.Clamp01((ownDistance - 0.42f) / 0.3f) : 0f;
            _ownAlpha += (ownWanted - _ownAlpha) * (1f - MathF.Exp(-dt * 14f));
            float camAbove = -camZ; // hauteur de la caméra au-dessus du sol où se tient le joueur
            camZ -= _groundZ;
            float cp = MathF.Cos(lookPitch * M.Deg2Rad), sp = MathF.Sin(lookPitch * M.Deg2Rad);
            _rig.UpdateCameras(camX, camY, camZ, fx * cp, fy * cp, -sp,
                               _background.Get(ModConfig.BackgroundColor.Value, U.Col(0.02f, 0.03f, 0.05f)));

            // ---- plafond (cartes d'intérieur) : il s'efface quand la caméra monte au-dessus des murs
            float wallTop = M.Clamp(ModConfig.WallHeight.Value, 0.3f, 10f);
            _rig.UpdateCeiling(CeilingWanted() && !terrain.Active && camAbove < wallTop - 0.06f, px, py, wallTop, vision);

            // ---- conduit : flèches 3D sur le sol vers les conduits voisins
            if (_ventView && Game.VentArrows(_ventArrowsAt, out Vector2 ventAt))
                _rig.UpdateVentArrows(_ventArrowsAt, ventAt.x, ventAt.y, MathF.Sin(Time.unscaledTime * 5f), terrain.Height(ventAt.x, ventAt.y));
            else
                _rig.UpdateVentArrows(null, 0f, 0f, 0f);

            // ---- flèches du jeu (sabotages...) : réorientées par rapport à la caméra 3D
            _arrows.Update(_rig, px, py, yaw);

            // ---- murs
            _walls.UpdateVisibility();
            _walls.UpdateLighting(vision);

            // ---- personnages
            int gameMask = _main.cullingMask;
            _proxies.Update(new ProxyFrame
            {
                Local = _local, CamX = camX, CamY = camY, Yaw = yaw, Vision = vision, ThirdPerson = third, OwnAlpha = _ownAlpha,
                SimulateVision = simulate, GameMask = gameMask, MaxDistance = half * 1.2f, DeltaTime = dt, Walls = _walls,
            });

            // ---- sol (vue de dessus de la carte, sans les personnages ni l'interface)
            int floorMask = gameMask & ~_floorHidden.Get(ModConfig.FloorHiddenLayers.Value) & ~_rig.UiMask();
            _rig.UpdateFloor(px, py, vision, floorMask, terrain);
            _walls.UpdateFaceUvs(_rig.FloorCenterX, _rig.FloorCenterY, _rig.FloorHalf);
            _rig.RenderFloor(_proxies);

            // ---- mini-carte
            Vector3 sprite = _local.transform.position;
            _rig.UpdateMinimap(_minimap, sprite.x, sprite.y, yaw, ModConfig.MinimapSize.Value,
                               ModConfig.MinimapPosition.Value, _buttons.ReservedTopFraction);

            if (!_autoDiagDone && Time.unscaledTime - _activatedAt > 3f)
            {
                _autoDiagDone = true;
                DumpDiagnostics(); // une fois par session : aide à diagnostiquer sans clavier (Android)
            }
        }

        private bool CeilingWanted()
        {
            switch (ModConfig.Ceiling.Value)
            {
                case ToggleMode.Always: return true;
                case ToggleMode.Never: return false;
                default: return _walls.Kind != MapKind.Polus && _walls.Kind != MapKind.Fungle;
            }
        }

        private void SmoothedPosition(Vector2 truePos, out float x, out float y)
        {
            x = truePos.x;
            y = truePos.y;
            if (_fixTime < 0f || MathF.Abs(_fixTime - Time.fixedTime) > 1e-4f) return;
            Rigidbody2D body = Game.Body(_local);
            if (body == null) return;
            try
            {
                if (body.interpolation != RigidbodyInterpolation2D.None) return; // déjà interpolé par Unity
                Vector2 cur = body.position;
                float dx = cur.x - _fixX, dy = cur.y - _fixY;
                if (dx * dx + dy * dy > 1f) return; // téléportation
                float a = M.Clamp01((Time.time - Time.fixedTime) / MathF.Max(1e-4f, Time.fixedDeltaTime));
                x = _fixX + dx * a + (truePos.x - cur.x);
                y = _fixY + dy * a + (truePos.y - cur.y);
            }
            catch { /* garde la position brute */ }
        }
    }

    internal sealed class CachedMask
    {
        private string _last;
        private int _mask;

        public int Get(string names)
        {
            if (!ReferenceEquals(names, _last)) { _last = names; _mask = U.MaskFromNames(names); }
            return _mask;
        }
    }

    internal sealed class CachedColor
    {
        private string _last;
        private Color _color;

        public Color Get(string hex, Color fallback)
        {
            if (!ReferenceEquals(hex, _last)) { _last = hex; _color = U.ParseColor(hex, fallback); }
            return _color;
        }
    }
}
