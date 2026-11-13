using System;
using UnityEngine;

namespace AmongUs3D
{
    /// <summary>
    /// Orientation de la caméra (lacet / tangage) et lecture des commandes de rotation.
    /// Le déplacement lui-même reste calculé par le jeu (vitesse, collisions, réseau) ;
    /// <see cref="MovementPatch"/> ne fait que réorienter la vitesse selon <see cref="Yaw"/>.
    ///
    /// Sur PC, par défaut, la souris dirige la caméra comme dans Minecraft : le curseur est verrouillé au
    /// centre de l'écran et les touches de déplacement deviennent avancer / reculer / pas de côté.
    /// Le curseur est rendu dès qu'une fenêtre du jeu en a besoin (voir <see cref="View3D"/>).
    /// </summary>
    internal sealed class Controls
    {
        public float Yaw;     // degrés, 0 = nord ("haut" en 2D), sens horaire
        public float Pitch;   // degrés, positif = regarder vers le haut

        /// <summary>Préférence du joueur : la souris dirige la caméra (F8 pour changer).</summary>
        public bool MouseLook { get; private set; }

        /// <summary>Le curseur est verrouillé en ce moment (la souris tourne réellement la caméra).</summary>
        public bool Captured { get; private set; }

        /// <summary>Nombre d'images écoulées depuis le dernier verrouillage (évite les clics "fantômes").</summary>
        public int CaptureAge { get; private set; }

        public bool TankActive { get; private set; }

        // Totaux bruts depuis le dernier diagnostic (pour étalonner la souris sur une nouvelle version du jeu).
        public float DebugAxisX, DebugAxisY;

        private float _targetYaw;
        private int _lastTurnSign;
        private bool _hadMouse;
        private float _lastMouseX, _lastMouseY;
        private int _lookFinger = -1;
        private int _skipFrames;
        private float _nextAssert;
        private bool _cursorBroken;

        public Controls()
        {
            MouseLook = !Platform.IsMobile && ModConfig.MouseLookAtStart.Value;
        }

        public void ResetOrientation()
        {
            Yaw = 0f; _targetYaw = 0f; Pitch = 0f; _lastTurnSign = 0; _lookFinger = -1;
        }

        /// <summary>Active ou désactive la caméra à la souris. Renvoie le nouvel état.</summary>
        public bool SetMouseLook(bool on)
        {
            if (Platform.IsMobile) on = false;
            if (on && !InputCompat.MouseAxisAvailable)
            {
                AmongUs3DPlugin.Logger.LogWarning("Caméra à la souris impossible (axe 'Mouse X' absent du jeu) : " +
                                                  "maintenez le clic droit pour regarder autour de vous.");
                on = false;
            }
            MouseLook = on;
            if (!on) SetCapture(false);
            return on;
        }

        /// <summary>Verrouille (ou rend) le curseur. À appeler à chaque image avec l'état voulu.</summary>
        public void SetCapture(bool wanted)
        {
            if (Platform.IsMobile || _cursorBroken) { Captured = false; return; }
            if (wanted && !MouseLook) wanted = false;
            if (wanted != Captured)
            {
                Captured = wanted;
                CaptureAge = 0;
                _skipFrames = 2; // le premier déplacement après un verrouillage contient un saut du curseur
                ApplyCursor(wanted);
                _nextAssert = Time.unscaledTime + 0.5f;
                return;
            }
            if (!Captured) return;
            CaptureAge++;
            // Le jeu, ou un passage par une autre fenêtre, peut avoir rendu le curseur : on le reprend.
            if (Time.unscaledTime >= _nextAssert)
            {
                _nextAssert = Time.unscaledTime + 0.5f;
                try
                {
                    if (Cursor.lockState != CursorLockMode.Locked || Cursor.visible) ApplyCursor(true);
                }
                catch { /* ignoré */ }
            }
        }

        private void ApplyCursor(bool locked)
        {
            try
            {
                Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
                Cursor.visible = !locked;
            }
            catch (Exception e)
            {
                _cursorBroken = true;
                Captured = false;
                AmongUs3DPlugin.Logger.LogWarning("Verrouillage du curseur indisponible : " + e.Message);
            }
        }

        /// <summary>
        /// Tourne doucement la caméra vers une direction (NaN = ne pas toucher à cet axe), par exemple vers l'animal
        /// qu'on caresse. La souris garde la main : ses mouvements s'ajoutent.
        /// </summary>
        public void Steer(float yaw, float pitch, float dt, float rate)
        {
            float k = 1f - MathF.Exp(-dt * rate);
            if (!float.IsNaN(yaw))
            {
                float d = M.DeltaAngle(Yaw, yaw) * k;
                Yaw += d;
                _targetYaw += d;
            }
            if (!float.IsNaN(pitch))
            {
                float maxPitch = M.Clamp(ModConfig.MaxPitch.Value, 0f, 85f);
                Pitch = M.Clamp(Pitch + (pitch - Pitch) * k, -maxPitch, maxPitch);
            }
        }

        /// <summary>Schéma effectif (lu aussi par le patch de déplacement, à 50 Hz).</summary>
        private bool ComputeTank()
        {
            switch (ModConfig.Scheme.Value)
            {
                case ControlScheme.Tank: return true;
                case ControlScheme.Strafe: return false;
                default: return !Platform.IsMobile && !MouseLook;
            }
        }

        /// <param name="canMove">faux si un menu, le chat, etc. empêche de bouger</param>
        /// <param name="isOverButton">teste si un point écran est sur un de nos boutons tactiles</param>
        public void Update(float dt, bool canMove, Func<float, float, bool> isOverButton)
        {
            TankActive = ComputeTank();
            float turnSpeed = Math.Max(10f, ModConfig.TurnSpeed.Value);

            // ---- rotation clavier / joystick (mode tank) et touches dédiées
            float turn = 0f;
            if (canMove)
            {
                if (TankActive)
                {
                    Vector2 raw = Game.JoystickDelta;
                    M.SquareAxis(raw.x, raw.y, out float sx, out _);
                    turn += M.Clamp(sx, -1f, 1f);
                }
                if (InputCompat.Key(ModConfig.KeyTurnLeft.Value)) turn -= 1f;
                if (InputCompat.Key(ModConfig.KeyTurnRight.Value)) turn += 1f;
            }
            turn = M.Clamp(turn, -1f, 1f);

            float step = ModConfig.TurnStepDegrees.Value;
            if (step >= 1f)
            {
                // Rotation par pas (ex. 90° par appui) avec une petite animation.
                int sign = turn > 0.5f ? 1 : (turn < -0.5f ? -1 : 0);
                if (sign != 0 && sign != _lastTurnSign) _targetYaw += sign * step;
                _lastTurnSign = sign;
                float d = M.DeltaAngle(Yaw, _targetYaw);
                float maxStep = Math.Max(turnSpeed, 180f) * 2.5f * dt;
                Yaw += M.Clamp(d, -maxStep, maxStep);
            }
            else
            {
                Yaw += turn * turnSpeed * dt;
                _targetYaw = Yaw;
            }

            // ---- regard à la souris (PC) ou au doigt (mobile)
            float lookYaw = 0f, lookPitch = 0f;
            if (Platform.IsMobile) ReadTouchLook(canMove, isOverButton, ref lookYaw, ref lookPitch);
            else ReadMouseLook(canMove, ref lookYaw, ref lookPitch);

            Yaw += lookYaw;
            _targetYaw += lookYaw;
            Pitch += ModConfig.InvertLookY.Value ? -lookPitch : lookPitch;

            float maxPitch = M.Clamp(ModConfig.MaxPitch.Value, 0f, 85f);
            Pitch = M.Clamp(Pitch, -maxPitch, maxPitch);

            // Garde les angles bornés sans casser la rotation par pas.
            if (Yaw > 720f || Yaw < -720f)
            {
                float w = M.WrapDegrees(Yaw);
                _targetYaw += w - Yaw;
                Yaw = w;
            }
        }

        private void ReadMouseLook(bool canMove, ref float lookYaw, ref float lookPitch)
        {
            float sens = M.Clamp(ModConfig.MouseSensitivity.Value, 0.01f, 5f);

            // Mode Minecraft : curseur verrouillé, la souris tourne toujours la caméra.
            // Sinon (ancien mode) : il faut maintenir le clic droit.
            bool legacyDrag = !MouseLook && canMove && InputCompat.MouseButton(1);
            if (!Captured && !legacyDrag)
            {
                _hadMouse = false;
                return;
            }

            float dx = 0f, dy = 0f;
            if (InputCompat.MouseAxisAvailable)
            {
                // GetAxisRaw("Mouse X") = déplacement brut de la souris x sensibilité de l'axe dans le jeu
                // (0,1 dans Among Us, mesuré en jeu) : on revient à des "points" de souris.
                float ax = InputCompat.AxisRaw("Mouse X"), ay = InputCompat.AxisRaw("Mouse Y");
                DebugAxisX += ax; DebugAxisY += ay;
                float scale = M.Clamp(ModConfig.MouseAxisScale.Value, 0.01f, 1000f);
                dx = ax * scale;
                dy = ay * scale;
            }
            else if (InputCompat.TryMousePosition(out float mx, out float my))
            {
                if (_hadMouse) { dx = mx - _lastMouseX; dy = my - _lastMouseY; }
                _lastMouseX = mx; _lastMouseY = my;
                _hadMouse = true;
            }

            if (_skipFrames > 0) { _skipFrames--; return; }
            // Un à-coup énorme (retour d'une autre fenêtre, changement d'écran...) n'est pas un geste du joueur.
            if (MathF.Abs(dx) > 4000f || MathF.Abs(dy) > 4000f) return;

            lookYaw += dx * sens;
            lookPitch += dy * sens;
        }

        private void ReadTouchLook(bool canMove, Func<float, float, bool> isOverButton, ref float lookYaw, ref float lookPitch)
        {
            var touches = InputCompat.Touches;
            if (!canMove)
            {
                _lookFinger = -1;
                return;
            }
            float h = Math.Max(1, Screen.height), w = Math.Max(1, Screen.width);
            float degPerPixel = ModConfig.TouchLookSpeed.Value / h;
            bool found = false;
            for (int i = 0; i < touches.Count; i++)
            {
                TouchInfo t = touches[i];
                if (t.phase == 0) // Began
                {
                    if (_lookFinger < 0 && IsLookZone(t.x / w, t.y / h) && (isOverButton == null || !isOverButton(t.x, t.y)))
                        _lookFinger = t.fingerId;
                    if (t.fingerId == _lookFinger) found = true;
                    continue;
                }
                if (t.fingerId != _lookFinger) continue;
                found = true;
                if (t.phase == 1) // Moved
                {
                    lookYaw += t.dx * degPerPixel;
                    lookPitch += t.dy * degPerPixel;
                }
                else if (t.phase == 3 || t.phase == 4) // Ended / Canceled
                {
                    _lookFinger = -1;
                }
            }
            if (!found) _lookFinger = -1;
        }

        /// <summary>
        /// Zone de l'écran où glisser le doigt fait tourner la caméra : moitié droite, sans les boutons
        /// d'action du jeu (en bas à droite) ni les icônes du coin supérieur droit.
        /// </summary>
        private static bool IsLookZone(float nx, float ny)
        {
            if (nx < 0.38f) return false;                    // joystick à gauche
            if (nx > 0.60f && ny < 0.45f) return false;      // boutons Utiliser / Signaler / Tuer...
            if (nx > 0.80f && ny > 0.80f) return false;      // réglages, carte, chat
            return true;
        }
    }
}
