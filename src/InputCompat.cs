using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace AmongUs3D
{
    /// <summary>Un contact tactile, copié une fois par image.</summary>
    internal struct TouchInfo
    {
        public int fingerId;
        public float x, y;     // pixels, origine en bas à gauche
        public float dx, dy;   // déplacement depuis l'image précédente
        public int phase;      // 0 Began, 1 Moved, 2 Stationary, 3 Ended, 4 Canceled
    }

    /// <summary>
    /// Accès à UnityEngine.Input (module "InputLegacyModule") par réflexion :
    /// le mod n'a ainsi besoin d'aucune référence de compilation vers ce module, et si une méthode
    /// manque dans une version du jeu, seule la fonctionnalité correspondante est désactivée.
    /// </summary>
    internal static class InputCompat
    {
        private static Func<KeyCode, bool> _getKeyDown, _getKey;
        private static Func<int, bool> _getMouseButton, _getMouseButtonDown, _getMouseButtonUp;
        private static Func<Vector3> _mousePosition;
        private static Func<string, float> _getAxisRaw;
        private static Func<int> _touchCount;
        private static MethodInfo _getTouch;
        private static MemberInfo _tFinger, _tPos, _tDelta, _tPhase;

        public static bool MouseAxisAvailable { get; private set; }
        public static bool TouchAvailable => _touchCount != null && _getTouch != null && _tPos != null && _tPhase != null;

        private static readonly List<TouchInfo> _touches = new List<TouchInfo>(10);
        private static int _touchFrame = -1;

        public static void Init()
        {
            Type input = FindType("UnityEngine.Input", "UnityEngine.InputLegacyModule");
            if (input == null)
            {
                AmongUs3DPlugin.Logger.LogWarning("UnityEngine.Input introuvable : raccourcis clavier, souris et tactile indisponibles.");
                return;
            }

            _getKeyDown = Static<Func<KeyCode, bool>>(input, "GetKeyDown", typeof(KeyCode));
            _getKey = Static<Func<KeyCode, bool>>(input, "GetKey", typeof(KeyCode));
            _getMouseButton = Static<Func<int, bool>>(input, "GetMouseButton", typeof(int));
            _getMouseButtonDown = Static<Func<int, bool>>(input, "GetMouseButtonDown", typeof(int));
            _getMouseButtonUp = Static<Func<int, bool>>(input, "GetMouseButtonUp", typeof(int));
            _getAxisRaw = Static<Func<string, float>>(input, "GetAxisRaw", typeof(string));
            _mousePosition = Static<Func<Vector3>>(input, "get_mousePosition");
            _touchCount = Static<Func<int>>(input, "get_touchCount");
            _getTouch = input.GetMethod("GetTouch", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int) }, null);

            if (_getTouch != null)
            {
                Type touch = _getTouch.ReturnType;
                _tFinger = Member(touch, "fingerId", "m_FingerId");
                _tPos = Member(touch, "position", "m_Position");
                _tDelta = Member(touch, "deltaPosition", "m_PositionDelta");
                _tPhase = Member(touch, "phase", "m_Phase");
            }

            // L'axe "Mouse X" n'existe que s'il est déclaré dans l'Input Manager du jeu.
            try
            {
                if (_getAxisRaw != null)
                {
                    _getAxisRaw("Mouse X");
                    MouseAxisAvailable = true;
                }
            }
            catch
            {
                MouseAxisAvailable = false;
            }

            AmongUs3DPlugin.Logger.LogInfo($"Entrées : clavier={(_getKeyDown != null)}, souris={(_mousePosition != null)}, " +
                                           $"axe souris={MouseAxisAvailable}, tactile={TouchAvailable}");
        }

        public static bool KeyDown(KeyCode k)
        {
            if (k == KeyCode.None || _getKeyDown == null) return false;
            try { return _getKeyDown(k); } catch { return false; }
        }

        public static bool Key(KeyCode k)
        {
            if (k == KeyCode.None || _getKey == null) return false;
            try { return _getKey(k); } catch { return false; }
        }

        public static bool MouseButton(int b)
        {
            if (_getMouseButton == null) return false;
            try { return _getMouseButton(b); } catch { return false; }
        }

        public static bool MouseButtonDown(int b)
        {
            if (_getMouseButtonDown == null) return false;
            try { return _getMouseButtonDown(b); } catch { return false; }
        }

        public static bool MouseButtonUp(int b)
        {
            if (_getMouseButtonUp == null) return false;
            try { return _getMouseButtonUp(b); } catch { return false; }
        }

        public static bool TryMousePosition(out float x, out float y)
        {
            x = y = 0f;
            if (_mousePosition == null) return false;
            try
            {
                Vector3 p = _mousePosition();
                x = p.x; y = p.y;
                return true;
            }
            catch { return false; }
        }

        public static float AxisRaw(string axis)
        {
            if (!MouseAxisAvailable) return 0f;
            try { return _getAxisRaw(axis); } catch { return 0f; }
        }

        /// <summary>Contacts tactiles de l'image en cours (calculés une seule fois par image).</summary>
        public static List<TouchInfo> Touches
        {
            get
            {
                int frame = Time.frameCount;
                if (frame == _touchFrame) return _touches;
                _touchFrame = frame;
                _touches.Clear();
                if (!TouchAvailable) return _touches;
                try
                {
                    int n = _touchCount();
                    for (int i = 0; i < n && i < 10; i++)
                    {
                        object t = _getTouch.Invoke(null, new object[] { i });
                        Vector2 pos = (Vector2)Read(_tPos, t);
                        Vector2 delta = _tDelta != null ? (Vector2)Read(_tDelta, t) : default;
                        _touches.Add(new TouchInfo
                        {
                            fingerId = _tFinger != null ? Convert.ToInt32(Read(_tFinger, t)) : i,
                            x = pos.x, y = pos.y,
                            dx = delta.x, dy = delta.y,
                            phase = Convert.ToInt32(Read(_tPhase, t)),
                        });
                    }
                }
                catch (Exception e)
                {
                    AmongUs3DPlugin.Logger.LogWarning("Lecture tactile impossible, désactivée : " + e.Message);
                    _touchCount = null;
                    _touches.Clear();
                }
                return _touches;
            }
        }

        // ---------------------------------------------------------------- utilitaires de réflexion

        private static Type FindType(string fullName, string assemblyName)
        {
            try
            {
                Type t = Type.GetType(fullName + ", " + assemblyName, false);
                if (t != null) return t;
            }
            catch { /* ignoré */ }

            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = a.GetType(fullName, false);
                    if (t != null) return t;
                }
                catch { /* ignoré */ }
            }
            return null;
        }

        private static T Static<T>(Type type, string name, params Type[] args) where T : Delegate
        {
            try
            {
                MethodInfo m = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, args, null);
                if (m == null) return null;
                return (T)Delegate.CreateDelegate(typeof(T), m, false);
            }
            catch
            {
                return null;
            }
        }

        private static MemberInfo Member(Type t, string property, string field)
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            MemberInfo m = t.GetProperty(property, F);
            if (m != null) return m;
            return t.GetField(field, F) ?? (MemberInfo)t.GetField(property, F);
        }

        private static object Read(MemberInfo m, object target)
        {
            return m is PropertyInfo p ? p.GetValue(target) : ((FieldInfo)m).GetValue(target);
        }
    }
}
