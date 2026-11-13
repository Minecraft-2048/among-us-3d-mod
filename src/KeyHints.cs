using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace AmongUs3D
{
    /// <summary>
    /// Écrit en petit, en bas des boutons du jeu (Utiliser, Signaler, Tuer, Sabotage, Bouche, Capacité, Caresser,
    /// Carte...), la touche du clavier qui fait la même chose. La touche est lue dans les réglages de touches du
    /// jeu (Rewired) : si le joueur les a changées, l'affichage suit.
    /// Les textes sont des enfants des boutons : ils apparaissent et disparaissent avec eux.
    /// </summary>
    internal sealed class KeyHints
    {
        private const int ToggleMapAction = 4; // RewiredConstsEnum.Action.ToggleMap

        private sealed class Hint
        {
            public Component Button;       // ActionButton, ou le bouton de la carte
            public ActionButton Action;    // null pour le bouton de la carte
            public IntPtr Ptr;
            public GameObject Go;
            public TextMeshPro Text;
            public TextMeshPro Label;      // texte du bouton ("TUER"...), pour se placer juste au-dessus
            public Renderer Graphic;
            public string Shown;
        }

        private readonly List<Hint> _hints = new List<Hint>();
        private readonly Dictionary<int, string> _names = new Dictionary<int, string>();
        private readonly HashSet<string> _reported = new HashSet<string>();
        private float _nextScan, _nextNames;
        private bool _broken, _visible = true;
        private IntPtr _hudPtr;

        public int Count => _hints.Count;

        public void Describe(System.Text.StringBuilder sb)
        {
            sb.Append("Touches sur les boutons : ").Append(_broken ? "désactivées (erreur)" : _hints.Count.ToString());
            foreach (Hint h in _hints)
            {
                string name = "?";
                try { name = h.Button.gameObject.name; } catch { /* ignoré */ }
                sb.Append(" | ").Append(name).Append('=').Append(h.Shown ?? "-");
            }
            sb.AppendLine();
        }

        public void Update(bool show)
        {
            if (_broken) return;
            try { UpdateImpl(show); }
            catch (Exception e)
            {
                _broken = true;
                AmongUs3DPlugin.Logger.LogWarning("Touches sur les boutons désactivées : " + e);
                try { Clear(); } catch { /* ignoré */ }
            }
        }

        private void UpdateImpl(bool show)
        {
            if (!show || !Game.HudExists)
            {
                SetVisible(false);
                return;
            }
            HudManager hud = HudManager.Instance;
            if (hud == null) { SetVisible(false); return; }
            if (hud.Pointer != _hudPtr)
            {
                Clear();
                _hudPtr = hud.Pointer;
                _nextScan = 0f;
            }
            SetVisible(true);

            float now = Time.unscaledTime;
            if (now >= _nextNames) { _names.Clear(); _nextNames = now + 2f; } // touches changées dans les réglages
            if (now >= _nextScan) { _nextScan = now + 1f; Scan(hud); }

            for (int i = _hints.Count - 1; i >= 0; i--)
            {
                Hint h = _hints[i];
                if (!U.Alive(h.Button) || !U.Alive(h.Go))
                {
                    U.SafeDestroy(h.Go);
                    _hints.RemoveAt(i);
                    continue;
                }
                Refresh(h);
            }
        }

        private void SetVisible(bool on)
        {
            if (on == _visible) return;
            _visible = on;
            foreach (Hint h in _hints)
                if (U.Alive(h.Go)) h.Go.SetActive(on);
        }

        public void Clear()
        {
            foreach (Hint h in _hints) U.SafeDestroy(h.Go);
            _hints.Clear();
            _hudPtr = IntPtr.Zero;
        }

        // ------------------------------------------------------------------ boutons

        private void Scan(HudManager hud)
        {
            TryAdd("UseButton", () => hud.UseButton);
            TryAdd("PetButton", () => hud.PetButton);
            TryAdd("ReportButton", () => hud.ReportButton);
            TryAdd("KillButton", () => hud.KillButton);
            TryAdd("ImpostorVentButton", () => hud.ImpostorVentButton);
            TryAdd("SabotageButton", () => hud.SabotageButton);
            TryAdd("AbilityButton", () => hud.AbilityButton);
            TryAdd("AdminButton", () => hud.AdminButton);
            try
            {
                PassiveButton map = hud.MapButton;
                if (map != null && !Has(map.Pointer)) Create(map, null);
            }
            catch (Exception e) { ReportOnce("HudManager.MapButton", e); }
        }

        private void TryAdd(string what, Func<ActionButton> get)
        {
            try
            {
                ActionButton b = get();
                if (b != null && !Has(b.Pointer)) Create(b, b);
            }
            catch (Exception e) { ReportOnce("HudManager." + what, e); }
        }

        private bool Has(IntPtr ptr)
        {
            foreach (Hint h in _hints)
                if (h.Ptr == ptr) return true;
            return false;
        }

        private void Create(Component button, ActionButton action)
        {
            TextMeshPro label = null;
            Renderer graphic = null;
            if (action != null)
            {
                try { label = action.buttonLabelText; } catch { label = null; }
                try { graphic = action.graphic; } catch { graphic = null; }
            }
            if (graphic == null)
            {
                try { graphic = button.GetComponentInChildren<SpriteRenderer>(true); } catch { graphic = null; }
            }

            var go = new GameObject("AU3D_Touche");
            go.layer = label != null ? label.gameObject.layer : button.gameObject.layer;
            go.transform.SetParent(button.transform, false);
            TextMeshPro t = go.AddComponent<TextMeshPro>();
            if (label != null)
            {
                t.font = label.font;
                t.fontSharedMaterial = label.fontSharedMaterial;
                t.fontSize = MathF.Max(0.5f, label.fontSize * 0.85f);
                t.sortingOrder = label.sortingOrder + 1;
            }
            else
            {
                TextMeshPro any = FindAnyLabel();
                if (any != null)
                {
                    t.font = any.font;
                    t.fontSharedMaterial = any.fontSharedMaterial;
                }
                t.fontSize = 2.2f;
                t.sortingOrder = graphic != null ? graphic.sortingOrder + 1 : 50;
            }
            t.alignment = TextAlignmentOptions.Center;
            t.enableWordWrapping = false;
            t.enableAutoSizing = false;
            t.color = U.Col(1f, 1f, 1f, 1f);
            t.rectTransform.sizeDelta = U.V2(3f, 1f);
            t.text = "";

            _hints.Add(new Hint
            {
                Button = button, Action = action, Ptr = button.Pointer, Go = go, Text = t, Label = label, Graphic = graphic,
            });
            if (!_visible) go.SetActive(false);
        }

        private TextMeshPro FindAnyLabel()
        {
            foreach (Hint h in _hints)
                if (h.Label != null && U.Alive(h.Label)) return h.Label;
            return null;
        }

        private void Refresh(Hint h)
        {
            // touche
            int action = ToggleMapAction;
            if (h.Action != null)
            {
                action = -1;
                try
                {
                    ActionMapGlyphDisplay glyph = h.Action.glyph;
                    if (glyph != null) action = (int)glyph.actionToDisplayMappedGlyphFor;
                }
                catch (Exception e) { ReportOnce("ActionButton.glyph", e); }
            }
            string key = KeyName(action);
            string text = string.IsNullOrEmpty(key) ? "" : "[" + key + "]";
            if (!string.Equals(text, h.Shown, StringComparison.Ordinal))
            {
                h.Shown = text;
                h.Text.text = text;
            }
            if (text.Length == 0) return;

            // place : juste au-dessus du texte du bouton ("TUER"), en bas de l'icône ;
            // sans texte, juste sous l'icône (bouton de la carte, en haut de l'écran)
            float alpha = 1f;
            Vector3 pos;
            try
            {
                if (h.Label != null && U.Alive(h.Label) && h.Label.isActiveAndEnabled && !string.IsNullOrEmpty(h.Label.text))
                {
                    Bounds lb = h.Label.renderer.bounds;
                    Vector3 c = lb.center, mx = lb.max, sz = lb.size;
                    float lh = MathF.Max(0.05f, sz.y);
                    pos = U.V3(c.x, mx.y + lh * 0.48f, c.z - 0.01f);
                }
                else if (h.Graphic != null && U.Alive(h.Graphic))
                {
                    Bounds gb = h.Graphic.bounds;
                    Vector3 c = gb.center, mn = gb.min, sz = gb.size;
                    pos = U.V3(c.x, mn.y - MathF.Max(0.08f, sz.y * 0.16f), c.z - 0.01f);
                }
                else return;

                if (h.Graphic is SpriteRenderer sr && U.Alive(sr)) alpha = M.Clamp01(sr.color.a);
            }
            catch (Exception e)
            {
                ReportOnce("position des touches", e);
                return;
            }
            h.Go.transform.position = pos;
            Color tc = h.Text.color;
            if (MathF.Abs(tc.a - alpha) > 0.01f) h.Text.color = U.Col(1f, 1f, 1f, alpha);
        }

        // ------------------------------------------------------------------ noms des touches

        private string KeyName(int action)
        {
            if (action <= 0) return null;
            if (_names.TryGetValue(action, out string s)) return s;
            s = Lookup(action);
            _names[action] = s;
            return s;
        }

        private string Lookup(int action)
        {
            try
            {
                Rewired.Player p = KeyboardJoystick.player;
                if (p == null && Rewired.ReInput.isReady) p = Rewired.ReInput.players.GetPlayer(0);
                if (p == null) return null;
                Rewired.ActionElementMap m = p.controllers.maps.GetFirstButtonMapWithAction(Rewired.ControllerType.Keyboard, action, true);
                if (m == null) return null;
                string n = m.elementIdentifierName;
                if (string.IsNullOrEmpty(n)) n = m.keyCode.ToString();
                return Short(n);
            }
            catch (Exception e)
            {
                ReportOnce("Rewired (touches du clavier)", e);
                return null;
            }
        }

        /// <summary>Nom court et en français d'une touche ("Left Shift" -> "Maj").</summary>
        public static string Short(string n)
        {
            if (string.IsNullOrWhiteSpace(n)) return null;
            n = n.Trim();
            switch (n.ToLowerInvariant())
            {
                case "space": return "Espace";
                case "left shift": case "right shift": case "shift": case "leftshift": case "rightshift": return "Maj";
                case "left control": case "right control": case "control": case "leftcontrol": case "rightcontrol": case "ctrl": return "Ctrl";
                case "left alt": case "right alt": case "alt": case "leftalt": case "rightalt": return "Alt";
                case "tab": return "Tab";
                case "return": case "enter": case "keypad enter": case "keypadenter": return "Entrée";
                case "escape": case "esc": return "Échap";
                case "backspace": return "Retour";
                case "up arrow": case "uparrow": return "Haut";
                case "down arrow": case "downarrow": return "Bas";
                case "left arrow": case "leftarrow": return "Gauche";
                case "right arrow": case "rightarrow": return "Droite";
            }
            if (n.StartsWith("Keypad", StringComparison.OrdinalIgnoreCase)) return "Pavé " + n.Substring(6).Trim();
            if (n.StartsWith("Alpha", StringComparison.Ordinal) && n.Length == 6) return n.Substring(5);
            return n;
        }

        private void ReportOnce(string what, Exception e)
        {
            if (_reported.Add(what))
                AmongUs3DPlugin.Logger.LogWarning($"[Touches] {what} indisponible : {e.GetType().Name}: {e.Message}");
        }
    }
}
