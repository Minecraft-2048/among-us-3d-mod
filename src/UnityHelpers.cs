using System;
using System.Collections.Generic;
using UnityEngine;

namespace AmongUs3D
{
    /// <summary>
    /// Exécution protégée d'appels Unity qui pourraient avoir été retirés de la build du jeu
    /// (Unity supprime les fonctions que le jeu n'utilise pas). Un avertissement unique par fonction.
    /// </summary>
    internal static class Safe
    {
        private static readonly HashSet<string> _reported = new HashSet<string>();

        public static void Report(string what, Exception e)
        {
            if (_reported.Add(what))
                AmongUs3DPlugin.Logger.LogWarning($"[Unity] {what} indisponible ({e.GetType().Name}: {e.Message}) — fonction ignorée.");
        }

        public static bool Run(string what, Action action)
        {
            try { action(); return true; }
            catch (Exception e) { Report(what, e); return false; }
        }

        public static T Get<T>(string what, Func<T> getter, T fallback)
        {
            try { return getter(); }
            catch (Exception e) { Report(what, e); return fallback; }
        }
    }

    /// <summary>
    /// Petits utilitaires Unity. Les structures (Vector3, Color...) sont remplies champ par champ :
    /// sous Il2CppInterop, un constructeur de structure est un appel natif (lent et potentiellement
    /// supprimé de la build du jeu), alors qu'une affectation de champ est gratuite.
    /// </summary>
    internal static class U
    {
        public static Vector2 V2(float x, float y) { Vector2 v = default; v.x = x; v.y = y; return v; }
        public static Vector3 V3(float x, float y, float z) { Vector3 v = default; v.x = x; v.y = y; v.z = z; return v; }
        public static Color Col(float r, float g, float b, float a = 1f) { Color c = default; c.r = r; c.g = g; c.b = b; c.a = a; return c; }

        public static Color32 C32(byte r, byte g, byte b, byte a = 255)
        {
            Color32 c = default; c.r = r; c.g = g; c.b = b; c.a = a; return c;
        }

        public static Quaternion Quat(float x, float y, float z, float w)
        {
            Quaternion q = default; q.x = x; q.y = y; q.z = z; q.w = w; return q;
        }

        public static Quaternion LookRotation(float fx, float fy, float fz, float ux, float uy, float uz)
        {
            M.LookRotation(fx, fy, fz, ux, uy, uz, out float x, out float y, out float z, out float w);
            return Quat(x, y, z, w);
        }

        public static Quaternion RotationZ(float degrees)
        {
            M.RotationZ(degrees, out float x, out float y, out float z, out float w);
            return Quat(x, y, z, w);
        }

        public static byte ToByte(float v) => (byte)(v <= 0f ? 0 : (v >= 1f ? 255 : (int)(v * 255f + 0.5f)));

        /// <summary>Vrai si l'objet Unity existe encore (pas détruit, wrapper encore valide).</summary>
        public static bool Alive(UnityEngine.Object o)
        {
            if (ReferenceEquals(o, null)) return false;
            try { return !o.WasCollected && o != null; } catch { return false; }
        }

        public static void SafeDestroy(UnityEngine.Object o)
        {
            if (!Alive(o)) return;
            try { UnityEngine.Object.Destroy(o); } catch { /* ignoré */ }
        }

        public static Color ParseColor(string hex, Color fallback)
        {
            return ModConfig.TryParseHex(hex, out float r, out float g, out float b) ? Col(r, g, b) : fallback;
        }

        /// <summary>Masque de calques à partir d'une liste de noms "UI,Shadow".</summary>
        public static int MaskFromNames(string names)
        {
            int mask = 0;
            if (string.IsNullOrWhiteSpace(names)) return 0;
            foreach (string raw in names.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string n = raw.Trim();
                if (n.Length == 0) continue;
                int layer = -1;
                if (int.TryParse(n, out int numeric)) layer = numeric;
                else
                {
                    try { layer = LayerMask.NameToLayer(n); } catch { layer = -1; }
                }
                if (layer >= 0 && layer < 32) mask |= 1 << layer;
            }
            return mask;
        }

        public static int Layer(string name)
        {
            try { return LayerMask.NameToLayer(name); } catch { return -1; }
        }

        public static string LayerName(int i)
        {
            try { return LayerMask.LayerToName(i) ?? ""; } catch { return ""; }
        }

        /// <summary>Choisit un calque Unity sans nom (donc inutilisé par le jeu).</summary>
        public static int PickFreeLayer(int configured, HashSet<int> avoid)
        {
            if (configured >= 8 && configured < 32) return configured;
            // 29 et 30 sont utilisés par d'autres mods connus (Nebula, SuperNewRoles) : on les évite.
            for (int i = 31; i >= 8; i--)
            {
                if (avoid.Contains(i) || i == 29 || i == 30) continue;
                if (string.IsNullOrEmpty(LayerName(i))) return i;
            }
            for (int i = 31; i >= 8; i--)
                if (!avoid.Contains(i)) return i;
            return 31;
        }

        /// <summary>Mesh partagé d'un MeshFilter (sans en créer de copie).</summary>
        public static Mesh SharedMesh(MeshFilter f)
        {
            try { return f.sharedMesh; } catch { return null; }
        }

        /// <summary>Assigne un mesh à un MeshFilter (sharedMesh, ou mesh en secours).</summary>
        public static void SetMesh(MeshFilter f, Mesh m)
        {
            if (_meshSetter == 0)
            {
                try { f.sharedMesh = m; _meshSetter = 1; return; }
                catch (Exception e) { Safe.Report("MeshFilter.sharedMesh", e); _meshSetter = 2; }
            }
            if (_meshSetter == 1) f.sharedMesh = m;
            else f.mesh = m;
        }
        private static int _meshSetter;

        public static string MaskToNames(int mask)
        {
            var parts = new List<string>();
            for (int i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                string n = LayerName(i);
                parts.Add(string.IsNullOrEmpty(n) ? i.ToString() : n);
            }
            return parts.Count == 0 ? "(aucun)" : string.Join(",", parts);
        }
    }
}
