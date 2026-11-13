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
        private readonly OnScreenButtons _buttons = new OnScreenButtons();
        private readonly ArrowSystem _arrows = new ArrowSystem();
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
            HandleHotkeys();

            _local = Game.LocalPlayer;
            try { _main = Camera.main; } catch { _main = null; }
            MonoBehaviour map = null;
            bool lobby = false;
            if (_local != null && _main != null) map = Game.MapRoot(out lobby);
            _inWorld = map != null;

            bool fullscreenUi = _inWorld && Game.FullscreenUiOpen;
            bool inVent = _inWorld && ModConfig.Show2DInVent.Value && Game.InVent(_local);
            bool render = _want3D && _inWorld && !fullscreenUi && !inVent && !_failSafe;

            if (render && !EnsureRig()) render = false;
            if (render && map.Pointer != _mapPtr) BuildMap(map, lobby);
            if (render && !_active) Activate();
            else if (!render && _active) Deactivate();

            _canMove = _inWorld && Game.CanMove(_local);
            if (_active)
            {
                // La souris dirige la caméra (curseur verrouillé) sauf si une fenêtre du jeu a besoin du curseur,
                // si la touche "libérer la souris" est maintenue, ou si le jeu n'est plus au premier plan.
                bool capture = _controls.MouseLook
                               && Game.AppFocused
                               && !InputCompat.Key(ModConfig.KeyFreeCursor.Value)
                               && !Game.UiWantsCursor(_local, _canMove);
                _controls.SetCapture(capture);
                _controls.Update(Time.deltaTime, _canMove, _isOverButton);

                // Clic gauche = Utiliser (comme la touche du jeu), tant que la souris dirige la caméra.
                if (_controls.Captured && _controls.CaptureAge > 3 && _canMove
                    && ModConfig.ClickToUse.Value && InputCompat.MouseButtonDown(0))
                    Game.ClickUse();
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
            if (InputCompat.KeyDown(ModConfig.KeyToggle3D.Value)) Toggle3D();
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
            if (InputCompat.KeyDown(ModConfig.KeyDiagnostics.Value)) DumpDiagnostics();
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
            Diagnostics.Dump(_rig, _walls, _proxies, _controls, _layer3D, _layerMini, _active, _want3D, _local, _main);
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
            Vector2 truePos = Game.TruePosition(_local);
            SmoothedPosition(truePos, out float px, out float py);

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

            // ---- position et orientation de la caméra
            float yaw = _controls.Yaw, pitch = _controls.Pitch;
            M.YawBasis(yaw, out float fx, out float fy, out _, out _);
            float camX = px, camY = py, camZ, lookPitch = pitch;
            if (!_third)
            {
                camZ = -M.Clamp(ModConfig.EyeHeight.Value, 0.1f, 3f);
            }
            else
            {
                float dist = M.Clamp(ModConfig.ThirdPersonDistance.Value, 0.5f, 8f);
                float h = M.Clamp(ModConfig.ThirdPersonHeight.Value, 0.2f, 6f);
                // La caméra tourne autour de la tête du personnage : regarder vers le bas la fait monter
                // (et inversement), comme la vue arrière de Minecraft.
                const float pivot = 0.55f;
                float rest = MathF.Atan2(h - pivot, dist) / M.Deg2Rad;
                float reach = MathF.Sqrt(dist * dist + (h - pivot) * (h - pivot));
                float er = M.Clamp(rest - pitch, -8f, 84f) * M.Deg2Rad;
                float back = reach * MathF.Cos(er), rise = reach * MathF.Sin(er);
                float k = 1f;
                if (pivot + rise < M.Clamp(ModConfig.WallHeight.Value, 0.3f, 10f) && back > 0.05f)
                {
                    float t = _walls.ClipFraction(px, py, px - fx * back, py - fy * back);
                    if (t < 1f) k = MathF.Max(0.12f, t - 0.2f / back);
                }
                camX = px - fx * back * k;
                camY = py - fy * back * k;
                camZ = -MathF.Max(0.12f, pivot + rise * k);
                lookPitch = pitch - rest; // regarde toujours la tête du personnage
            }
            float cp = MathF.Cos(lookPitch * M.Deg2Rad), sp = MathF.Sin(lookPitch * M.Deg2Rad);
            _rig.UpdateCameras(camX, camY, camZ, fx * cp, fy * cp, -sp,
                               _background.Get(ModConfig.BackgroundColor.Value, U.Col(0.02f, 0.03f, 0.05f)));

            // ---- flèches du jeu (sabotages...) : réorientées par rapport à la caméra 3D
            _arrows.Update(_rig, px, py, yaw);

            // ---- murs
            _walls.UpdateVisibility();
            _walls.UpdateLighting(vision);

            // ---- personnages
            int gameMask = _main.cullingMask;
            _proxies.Update(new ProxyFrame
            {
                Local = _local, CamX = camX, CamY = camY, Yaw = yaw, Vision = vision, ThirdPerson = _third,
                SimulateVision = simulate, GameMask = gameMask, MaxDistance = half * 1.2f, DeltaTime = dt, Walls = _walls,
            });

            // ---- sol (vue de dessus de la carte, sans les personnages ni l'interface)
            int floorMask = gameMask & ~_floorHidden.Get(ModConfig.FloorHiddenLayers.Value) & ~_rig.UiMask();
            _rig.UpdateFloor(px, py, vision, floorMask);
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
