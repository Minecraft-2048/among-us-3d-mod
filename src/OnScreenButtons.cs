using System;
using System.Collections.Generic;
using UnityEngine;

namespace AmongUs3D
{
    /// <summary>
    /// Boutons [3D] [1P/3P] [MAP] en haut de l'écran (indispensables sur Android, optionnels sur PC).
    /// Ce sont de simples sprites sur le calque UI, enfants de la caméra du jeu ; les appuis sont détectés
    /// par le mod (tactile ou clic) sans passer par le système de boutons du jeu.
    /// </summary>
    internal sealed class OnScreenButtons
    {
        private sealed class Button
        {
            public string LabelOn, LabelOff;
            public Action Click;
            public GameObject Go;
            public SpriteRenderer Renderer;
            public Sprite On, Off;
            public float X0, Y0, X1, Y1; // rectangle écran en pixels
        }

        private const int TexW = 96, TexH = 56;
        private readonly List<Button> _buttons = new List<Button>();
        private readonly List<UnityEngine.Object> _assets = new List<UnityEngine.Object>();
        private readonly Dictionary<int, int> _pressFinger = new Dictionary<int, int>();
        private Camera _cam;
        private int _mousePressed = -1;
        private bool _shown;
        private bool _broken;

        /// <summary>Hauteur (fraction de l'écran) occupée en haut, pour placer la mini-carte en dessous.</summary>
        public float ReservedTopFraction { get; private set; }

        public bool Contains(float x, float y)
        {
            if (!_shown) return false;
            foreach (Button b in _buttons)
                if (x >= b.X0 && x <= b.X1 && y >= b.Y0 && y <= b.Y1) return true;
            return false;
        }

        public void Update(Camera main, bool show, bool is3D, bool thirdPerson, bool minimap,
                           Action toggle3D, Action toggleCamera, Action toggleMinimap)
        {
            if (_broken) return;
            try
            {
                if (!show || main == null)
                {
                    Hide();
                    return;
                }
                if (_cam == null || !U.Alive(_cam) || _cam.Pointer != main.Pointer || _buttons.Count == 0 || !U.Alive(_buttons[0].Go))
                    Build(main, toggle3D, toggleCamera, toggleMinimap);

                Layout(main);
                SetState(0, is3D);
                SetState(1, thirdPerson);
                SetState(2, minimap);
                // Les boutons caméra et mini-carte n'ont d'effet qu'en 3D.
                SetVisible(0, true);
                SetVisible(1, is3D);
                SetVisible(2, is3D);
                _shown = true;
                DetectPresses();
            }
            catch (Exception e)
            {
                _broken = true;
                AmongUs3DPlugin.Logger.LogWarning("Boutons à l'écran désactivés : " + e);
                Hide();
            }
        }

        private void Hide()
        {
            _shown = false;
            ReservedTopFraction = 0f;
            _pressFinger.Clear();
            _mousePressed = -1;
            foreach (Button b in _buttons)
                if (U.Alive(b.Go)) b.Go.SetActive(false);
        }

        private void SetState(int i, bool on)
        {
            Button b = _buttons[i];
            b.Renderer.sprite = on ? b.On : b.Off;
        }

        private void SetVisible(int i, bool visible)
        {
            Button b = _buttons[i];
            if (b.Go.activeSelf != visible) b.Go.SetActive(visible);
            if (!visible) { b.X0 = b.Y0 = 1e9f; b.X1 = b.Y1 = -1e9f; }
        }

        // ------------------------------------------------------------------ construction

        private void Build(Camera main, Action toggle3D, Action toggleCamera, Action toggleMinimap)
        {
            Destroy();
            _cam = main;
            int uiLayer = U.Layer("UI");
            if (uiLayer < 0) uiLayer = 5;
            _buttons.Add(Make("3D", "2D", toggle3D, main, uiLayer));
            _buttons.Add(Make("3P", "1P", toggleCamera, main, uiLayer));
            _buttons.Add(Make("MAP", "MAP", toggleMinimap, main, uiLayer));
        }

        private Button Make(string labelOn, string labelOff, Action click, Camera main, int layer)
        {
            var b = new Button { LabelOn = labelOn, LabelOff = labelOff, Click = click };
            b.On = MakeSprite(labelOn, true);
            b.Off = MakeSprite(labelOff, false);
            b.Go = new GameObject("AU3D_Button_" + labelOn);
            b.Go.layer = layer;
            b.Go.transform.SetParent(main.transform, false);
            b.Renderer = b.Go.AddComponent<SpriteRenderer>();
            b.Renderer.sprite = b.Off;
            b.Renderer.sortingOrder = 30000;
            return b;
        }

        private Sprite MakeSprite(string label, bool active)
        {
            var pixels = new Color[TexW * TexH];
            Color fill = active ? U.Col(0.05f, 0.22f, 0.32f, 0.85f) : U.Col(0.06f, 0.07f, 0.1f, 0.7f);
            Color border = active ? U.Col(0.35f, 0.88f, 1f, 1f) : U.Col(0.55f, 0.57f, 0.62f, 0.9f);
            Color text = active ? U.Col(1f, 1f, 1f, 1f) : U.Col(0.78f, 0.8f, 0.85f, 1f);
            const int radius = 12, borderW = 3;
            for (int y = 0; y < TexH; y++)
            for (int x = 0; x < TexW; x++)
            {
                float d = RoundRectDistance(x + 0.5f, y + 0.5f, TexW, TexH, radius);
                Color c = default;
                if (d <= 0f) c = d > -borderW ? border : fill;
                pixels[y * TexW + x] = c;
            }
            PixelFont.Draw(pixels, TexW, TexH, label, 4, text);

            var tex = new Texture2D(TexW, TexH, TextureFormat.RGBA32, false) { name = "AU3D_Btn_" + label };
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.SetPixels(pixels);
            tex.Apply();
            var sprite = Sprite.Create(tex, new Rect(0f, 0f, TexW, TexH), U.V2(0.5f, 0.5f), 100f, 0u,
                                       SpriteMeshType.FullRect, default(Vector4), false);
            sprite.name = "AU3D_Btn_" + label;
            _assets.Add(tex);
            _assets.Add(sprite);
            return sprite;
        }

        /// <summary>Distance signée au bord d'un rectangle arrondi (négative à l'intérieur).</summary>
        private static float RoundRectDistance(float x, float y, float w, float h, float r)
        {
            float qx = MathF.Abs(x - w * 0.5f) - (w * 0.5f - r);
            float qy = MathF.Abs(y - h * 0.5f) - (h * 0.5f - r);
            float ox = MathF.Max(qx, 0f), oy = MathF.Max(qy, 0f);
            return MathF.Sqrt(ox * ox + oy * oy) + MathF.Min(MathF.Max(qx, qy), 0f) - r;
        }

        public void Destroy()
        {
            foreach (Button b in _buttons) U.SafeDestroy(b.Go);
            _buttons.Clear();
            foreach (var a in _assets) U.SafeDestroy(a);
            _assets.Clear();
            _cam = null;
            _shown = false;
        }

        // ------------------------------------------------------------------ disposition et appuis

        private void Layout(Camera main)
        {
            float sw = Math.Max(1, Screen.width), sh = Math.Max(1, Screen.height);
            float scale = M.Clamp(ModConfig.ButtonScale.Value, 0.4f, 3f);
            float bh = M.Clamp(sh * 0.075f * scale, 36f, 220f);
            float bw = bh * TexW / TexH;
            float gap = bh * 0.25f;
            float top = sh - sh * 0.02f;
            float total = bw * _buttons.Count + gap * (_buttons.Count - 1);
            float x = sw * 0.5f - total * 0.5f;

            float ortho = main.orthographicSize;
            float aspect = sw / sh;
            float worldPerPixel = 2f * ortho / sh;
            float scaleWorld = bh * worldPerPixel / (TexH / 100f);
            float z = Math.Max(0.5f, main.nearClipPlane + 0.5f);

            foreach (Button b in _buttons)
            {
                b.X0 = x; b.X1 = x + bw; b.Y0 = top - bh; b.Y1 = top;
                float cx = x + bw * 0.5f, cy = top - bh * 0.5f;
                float lx = (cx / sw - 0.5f) * 2f * ortho * aspect;
                float ly = (cy / sh - 0.5f) * 2f * ortho;
                b.Go.transform.localPosition = U.V3(lx, ly, z);
                b.Go.transform.localScale = U.V3(scaleWorld, scaleWorld, 1f);
                x += bw + gap;
            }
            ReservedTopFraction = (bh + sh * 0.03f) / sh;
        }

        private int HitTest(float x, float y)
        {
            for (int i = 0; i < _buttons.Count; i++)
            {
                Button b = _buttons[i];
                if (x >= b.X0 && x <= b.X1 && y >= b.Y0 && y <= b.Y1) return i;
            }
            return -1;
        }

        private void DetectPresses()
        {
            // Tactile : un appui compte s'il commence et se termine sur le même bouton.
            foreach (TouchInfo t in InputCompat.Touches)
            {
                if (t.phase == 0)
                {
                    int hit = HitTest(t.x, t.y);
                    if (hit >= 0) _pressFinger[t.fingerId] = hit;
                }
                else if (t.phase == 3 || t.phase == 4)
                {
                    if (_pressFinger.TryGetValue(t.fingerId, out int started))
                    {
                        _pressFinger.Remove(t.fingerId);
                        if (t.phase == 3 && HitTest(t.x, t.y) == started) Fire(started);
                    }
                }
            }

            // Souris (PC, si les boutons sont affichés)
            if (Platform.IsMobile) return;
            if (InputCompat.MouseButtonDown(0) && InputCompat.TryMousePosition(out float mx, out float my))
                _mousePressed = HitTest(mx, my);
            if (InputCompat.MouseButtonUp(0))
            {
                if (_mousePressed >= 0 && InputCompat.TryMousePosition(out float ux, out float uy) && HitTest(ux, uy) == _mousePressed)
                    Fire(_mousePressed);
                _mousePressed = -1;
            }
        }

        private void Fire(int index)
        {
            try { _buttons[index].Click?.Invoke(); }
            catch (Exception e) { AmongUs3DPlugin.Logger.LogWarning("Bouton : " + e.Message); }
        }
    }

    /// <summary>Police pixel 5x7 minimaliste (seulement les caractères utilisés).</summary>
    internal static class PixelFont
    {
        private static readonly Dictionary<char, string[]> Glyphs = new Dictionary<char, string[]>
        {
            ['1'] = new[] { "..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###." },
            ['2'] = new[] { ".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####" },
            ['3'] = new[] { "####.", "....#", "....#", ".###.", "....#", "....#", "####." },
            ['A'] = new[] { ".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" },
            ['D'] = new[] { "####.", "#...#", "#...#", "#...#", "#...#", "#...#", "####." },
            ['M'] = new[] { "#...#", "##.##", "#.#.#", "#.#.#", "#...#", "#...#", "#...#" },
            ['P'] = new[] { "####.", "#...#", "#...#", "####.", "#....", "#....", "#...." },
        };

        public static void Draw(Color[] pixels, int w, int h, string text, int scale, Color color)
        {
            int glyphW = 5 * scale, glyphH = 7 * scale, spacing = scale;
            int total = text.Length * glyphW + (text.Length - 1) * spacing;
            int x0 = (w - total) / 2, y0 = (h - glyphH) / 2;
            for (int c = 0; c < text.Length; c++)
            {
                if (!Glyphs.TryGetValue(text[c], out string[] rows)) continue;
                int gx = x0 + c * (glyphW + spacing);
                for (int row = 0; row < 7; row++)
                for (int col = 0; col < 5; col++)
                {
                    if (rows[row][col] != '#') continue;
                    for (int sy = 0; sy < scale; sy++)
                    for (int sx = 0; sx < scale; sx++)
                    {
                        int px = gx + col * scale + sx;
                        int py = y0 + (6 - row) * scale + sy; // la texture a son origine en bas
                        if (px < 0 || py < 0 || px >= w || py >= h) continue;
                        pixels[py * w + px] = color;
                    }
                }
            }
        }
    }
}
