using System;
using UnityEngine;

namespace AmongUs3D
{
    /// <summary>
    /// Accessoires en volume, fabriqués comme les chapeaux : on photographie leur dessin (<see cref="HatCapture"/>)
    /// tel que le jeu l'affiche, puis :
    ///  - visière cosmétique et animal : le dessin est "gonflé" (<see cref="HatShape"/>) ;
    ///  - combinaison : le dessin, vu de profil comme dans le jeu, est peint sur le corps et les jambes 3D
    ///    (chaque sommet prend la couleur du dessin à sa place dans le profil).
    /// Repère des modèles : X = droite du personnage, Y = devant lui, Z = vers le haut ; unité = taille du personnage.
    /// </summary>
    internal static class Accessories
    {
        /// <summary>Demi-épaisseur d'une visière : un peu plus que la visière du modèle (0,205).</summary>
        public const float VisorHalfThickness = 0.22f;

        /// <summary>Repère du dessin du corps : centre (x), sommet de la tête (y), taille et sens du regard (+1 = droite).</summary>
        private struct Frame
        {
            public float Cx, Top, Height, Facing;
        }

        private static bool TryFrame(SpriteRenderer body, float scaleX, float heightOverride, out Frame f)
        {
            f = default;
            if (body == null) return false;
            Bounds b = body.bounds;
            Vector3 c = b.center, mx = b.max, sz = b.size;
            bool right = !body.flipX;
            if (scaleX < 0f) right = !right;
            f.Cx = c.x;
            f.Top = mx.y;
            f.Height = float.IsNaN(heightOverride) ? sz.y : heightOverride;
            f.Facing = right ? 1f : -1f;
            return f.Height > 0.05f;
        }

        private static HatMeshes Meshes(ShapeMesh s, string name)
        {
            if (s == null) return null;
            var made = new HatMeshes { Solid = CrewKit.BuildMesh(s, name, 0f, out _) };
            made.Outline = CrewKit.BuildMesh(s, name + "Contour", CrewShape.OutlineWidth * 0.8f, out var oc);
            CrewKit.Black(made.Outline, oc);
            return made;
        }

        /// <summary>
        /// Photographie un ou deux calques posés sur le corps et donne le repère du modèle : Y du bord gauche
        /// (<paramref name="y0"/>, largeur signée d'un pixel <paramref name="cellY"/>), Z du bord bas.
        /// </summary>
        public static bool TryProfile(HatCapture cap, SpriteRenderer back, SpriteRenderer front, SpriteRenderer body, float bodyScaleX,
                                      float heightOverride, out byte[] rgba, out float y0, out float cellY, out float z0, out float cellZ)
        {
            rgba = null;
            y0 = cellY = z0 = cellZ = 0f;
            if (!TryFrame(body, bodyScaleX, heightOverride, out Frame f)) return false;
            if (!cap.TryCapture(back, front, out rgba, out float minX, out float minY, out float cell)) return false;
            y0 = (minX - f.Cx) * f.Facing / f.Height;
            cellY = cell * f.Facing / f.Height;
            z0 = 1f + (minY - f.Top) / f.Height;
            cellZ = cell / f.Height;
            return true;
        }

        /// <summary>Gonfle un ou deux calques (chapeau, visière) placés sur le corps ; null si rien d'exploitable.</summary>
        public static HatMeshes Puff(HatCapture cap, SpriteRenderer back, SpriteRenderer front, SpriteRenderer body, float bodyScaleX,
                                     float heightOverride, float halfThickness, string name)
        {
            if (!TryProfile(cap, back, front, body, bodyScaleX, heightOverride, out byte[] rgba, out float y0, out float cy, out float z0, out float cz))
                return null;
            ShapeMesh s = HatShape.Build(rgba, HatCapture.Size, HatCapture.Size, y0, cy, z0, cz, halfThickness);
            return Meshes(s, name);
        }

        /// <summary>Chapeau en volume (coque autour de la tête ou objet gonflé, voir <see cref="HatShape.BuildHat"/>) ; null si rien d'exploitable.</summary>
        public static HatMeshes PuffHat(HatCapture cap, SpriteRenderer back, SpriteRenderer front, SpriteRenderer body, float bodyScaleX,
                                        float heightOverride, string productId, string name)
        {
            if (!TryProfile(cap, back, front, body, bodyScaleX, heightOverride, out byte[] rgba, out float y0, out float cy, out float z0, out float cz))
                return null;
            ShapeMesh s = HatShape.BuildHat(rgba, HatCapture.Size, HatCapture.Size, y0, cy, z0, cz, HatShape.HalfThickness, productId);
            return Meshes(s, name);
        }

        /// <summary>Peint le dessin de la combinaison sur le corps et les jambes 3D ; null si rien d'exploitable.</summary>
        public static SkinPaint PaintSkin(HatCapture cap, SpriteRenderer skin, SpriteRenderer body, float bodyScaleX)
        {
            if (!TryFrame(body, bodyScaleX, float.NaN, out Frame f)) return null;
            if (!cap.TryCapture(null, skin, out byte[] rgba, out float minX, out float minY, out float cell)) return null;
            float y0 = (minX - f.Cx) * f.Facing / f.Height, cy = cell * f.Facing / f.Height;
            float z0 = 1f + (minY - f.Top) / f.Height, cz = cell / f.Height;
            Color32[] bodyCols = Sample(CrewKit.BodyShape, 0f, 0.6f, rgba, y0, cy, z0, cz);
            Color32[] legCols = Sample(CrewKit.LegShape, CrewShape.HipHeight, CrewShape.HipHeight - 0.105f, rgba, y0, cy, z0, cz);
            if (bodyCols == null && legCols == null) return null;
            return new SkinPaint { Body = bodyCols, Leg = legCols };
        }

        private static Color32[] Sample(ShapeMesh shape, float zOffset, float centerZ, byte[] rgba, float y0, float cy, float z0, float cz)
        {
            var cols = new Color32[shape.VertexCount];
            int hits = 0;
            for (int i = 0; i < shape.VertexCount; i++)
            {
                byte zone = shape.Zone[i];
                if (zone == CrewZone.Visor) continue; // la vitre reste visible, comme dans le jeu
                if (!HatShape.SampleInside(rgba, HatCapture.Size, HatCapture.Size, y0, cy, z0, cz, shape.Y[i], shape.Z[i] + zOffset,
                                           0f, centerZ, out byte r, out byte g, out byte b)) continue;
                float k = 0.5f + 0.5f * CrewShape.Light(shape.Nx[i], shape.Ny[i], shape.Nz[i], shape.Z[i], zone);
                cols[i] = U.C32(U.ToByte(r / 255f * k), U.ToByte(g / 255f * k), U.ToByte(b / 255f * k));
                hits++;
            }
            return hits > 0 ? cols : null;
        }

        /// <summary>
        /// Animal : son dessin gonflé, en unités du monde, pieds à Z = 0, tourné vers +Y (le sens où il regarde).
        /// </summary>
        public static HatMeshes PuffPet(HatCapture cap, SpriteRenderer pet, float scaleX)
        {
            if (pet == null) return null;
            Bounds b = pet.bounds;
            Vector3 c = b.center, mn = b.min, sz = b.size;
            if (sz.y < 0.05f) return null;
            bool right = !pet.flipX;
            if (scaleX < 0f) right = !right;
            float facing = right ? 1f : -1f;
            if (!cap.TryCapture(null, pet, out byte[] rgba, out float minX, out float minY, out float cell)) return null;
            float half = M.Clamp(0.3f * sz.y, 0.05f, 0.3f);
            ShapeMesh s = HatShape.BuildPet(rgba, HatCapture.Size, HatCapture.Size, (minX - c.x) * facing, cell * facing, minY - mn.y, cell, half);
            return Meshes(s, "AU3D_Animal");
        }

        public static IntPtr SpriteKey(SpriteRenderer r)
        {
            try { return r != null && r.sprite != null ? r.sprite.Pointer : IntPtr.Zero; }
            catch { return IntPtr.Zero; }
        }
    }
}
