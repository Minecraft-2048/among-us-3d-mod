using System;
using System.Collections.Generic;
using AmongUs3D;

/// <summary>Petit banc de tests sans framework : dotnet run --project tests</summary>
internal static class MathTests
{
    private static int _passed, _failed;

    private static void Check(bool ok, string name)
    {
        if (ok) _passed++;
        else { _failed++; Console.WriteLine("ÉCHEC : " + name); }
    }

    private static bool Near(float a, float b, float eps = 1e-3f) => MathF.Abs(a - b) <= eps;

    private static int Main()
    {
        YawBasis();
        UserExampleRightThenDown();
        StrafeAndTank();
        SquareAxis();
        LookRotationBasis();
        ViewIsNotMirrored();
        BillboardFacesCamera();
        Angles();
        Vision();
        Segments();
        WallStrips();

        Console.WriteLine($"{_passed} tests réussis, {_failed} échecs");
        return _failed == 0 ? 0 : 1;
    }

    private static void YawBasis()
    {
        M.YawBasis(0f, out var fx, out var fy, out var rx, out var ry);
        Check(Near(fx, 0) && Near(fy, 1) && Near(rx, 1) && Near(ry, 0), "lacet 0 : avant = nord, droite = est");
        M.YawBasis(90f, out fx, out fy, out rx, out ry);
        Check(Near(fx, 1) && Near(fy, 0) && Near(rx, 0) && Near(ry, -1), "lacet 90 : avant = est, droite = sud");
        M.YawBasis(180f, out fx, out fy, out rx, out ry);
        Check(Near(fx, 0) && Near(fy, -1) && Near(rx, -1) && Near(ry, 0), "lacet 180 : avant = sud, droite = ouest");
    }

    /// <summary>
    /// Exemple de la demande : en 2D on va à droite puis en bas ;
    /// en 3D on tourne à droite et on avance, puis on retourne à droite et on avance.
    /// </summary>
    private static void UserExampleRightThenDown()
    {
        const float speed = 2.5f;
        foreach (bool tank in new[] { true, false })
        {
            // touche "haut" (avancer) avec la caméra tournée d'un quart de tour à droite
            M.TransformVelocity(0f, speed, 90f, tank, out float x1, out float y1);
            Check(Near(x1, speed) && Near(y1, 0f), $"avancer après un quart de tour à droite = aller à droite en 2D (tank={tank})");
            // nouveau quart de tour à droite, puis avancer
            M.TransformVelocity(0f, speed, 180f, tank, out float x2, out float y2);
            Check(Near(x2, 0f) && Near(y2, -speed), $"avancer après deux quarts de tour = aller en bas en 2D (tank={tank})");
        }
    }

    private static void StrafeAndTank()
    {
        const float s = 3f;
        // Strafe : gauche/droite = pas de côté, la norme est conservée
        M.TransformVelocity(s, 0f, 0f, false, out float x, out float y);
        Check(Near(x, s) && Near(y, 0f), "strafe droite face au nord = est");
        M.TransformVelocity(s, 0f, 90f, false, out x, out y);
        Check(Near(x, 0f) && Near(y, -s), "strafe droite face à l'est = sud");
        M.TransformVelocity(s * 0.7071f, s * 0.7071f, 37f, false, out x, out y);
        Check(Near(MathF.Sqrt(x * x + y * y), s, 2e-3f), "strafe : vitesse conservée");

        // Tank : gauche/droite ne déplacent pas (ils font tourner)
        M.TransformVelocity(s, 0f, 30f, true, out x, out y);
        Check(Near(x, 0f) && Near(y, 0f), "tank : droite seule = pas de déplacement");
        M.TransformVelocity(0f, -s, 0f, true, out x, out y);
        Check(Near(x, 0f) && Near(y, -s), "tank : bas = reculer");
        M.TransformVelocity(s * 0.70710678f, s * 0.70710678f, 0f, true, out x, out y);
        Check(Near(y, s, 2e-3f) && Near(x, 0f), "tank : haut+droite au clavier = avancer à pleine vitesse");
        M.TransformVelocity(s * 0.3f, s * 0.3f, 0f, true, out x, out y);
        Check(Near(y, s * 0.42426f, 2e-3f), "tank : joystick analogique à moitié = vitesse partielle");
        Check(MathF.Sqrt(x * x + y * y) <= s * 0.4243f + 1e-3f, "tank : jamais plus vite que le jeu");
    }

    private static void SquareAxis()
    {
        M.SquareAxis(0.70710678f, 0.70710678f, out float x, out float y);
        Check(Near(x, 1f) && Near(y, 1f), "diagonale clavier -> (1,1)");
        M.SquareAxis(0.5f, 0f, out x, out y);
        Check(Near(x, 0.5f) && Near(y, 0f), "axe analogique conservé");
        M.SquareAxis(0f, 0f, out x, out y);
        Check(x == 0f && y == 0f, "zéro reste zéro");
    }

    private static void LookRotationBasis()
    {
        var cases = new (float fx, float fy, float fz)[]
        {
            (0, 1, 0), (1, 0, 0), (0, -1, 0), (-1, 0, 0), (0.6f, 0.8f, 0), (0.5f, 0.5f, -0.70710678f), (0.3f, -0.9f, 0.2f),
        };
        foreach (var c in cases)
        {
            M.LookRotation(c.fx, c.fy, c.fz, 0, 0, -1, out float qx, out float qy, out float qz, out float qw);
            float len = MathF.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            Check(Near(len, 1f), $"quaternion unitaire {c}");
            float fl = MathF.Sqrt(c.fx * c.fx + c.fy * c.fy + c.fz * c.fz);
            M.Rotate(qx, qy, qz, qw, 0, 0, 1, out float ox, out float oy, out float oz);
            Check(Near(ox, c.fx / fl) && Near(oy, c.fy / fl) && Near(oz, c.fz / fl), $"Z local -> avant {c}");
            M.Rotate(qx, qy, qz, qw, 0, 1, 0, out ox, out oy, out oz);
            Check(oz < 0f || Near(c.fz / fl, -1f), $"Y local vers le haut du monde (-Z) {c}");
        }
    }

    /// <summary>Face au nord, un objet à l'est doit apparaître à DROITE de l'écran (image non inversée).</summary>
    private static void ViewIsNotMirrored()
    {
        foreach (float yaw in new[] { 0f, 90f, 180f, 270f, 45f })
        {
            M.YawBasis(yaw, out float fx, out float fy, out float rx, out float ry);
            M.LookRotation(fx, fy, 0, 0, 0, -1, out float qx, out float qy, out float qz, out float qw);
            // point situé à droite (sur le sol, en direction de "droite")
            float px = rx * 3f + fx * 5f, py = ry * 3f + fy * 5f, pz = 0f;
            // vers le repère caméra : rotation inverse (conjugué)
            M.Rotate(-qx, -qy, -qz, qw, px, py, pz - (-0.55f), out float lx, out float ly, out float lz);
            Check(lx > 0f, $"objet à droite visible à droite de l'écran (lacet {yaw})");
            Check(lz > 0f, $"objet devant la caméra (lacet {yaw})");
            Check(ly > 0f == false, $"le sol est sous l'horizon (lacet {yaw})");
        }
    }

    /// <summary>Un panneau (personnage) doit montrer son côté droit à droite de l'écran, le haut vers le haut.</summary>
    private static void BillboardFacesCamera()
    {
        M.LookRotation(0, 1, 0, 0, 0, -1, out float qx, out float qy, out float qz, out float qw);
        M.Rotate(qx, qy, qz, qw, 1, 0, 0, out float ox, out float oy, out float oz);
        Check(Near(ox, 1) && Near(oy, 0) && Near(oz, 0), "X du sprite = droite de la caméra (est)");
        M.Rotate(qx, qy, qz, qw, 0, 1, 0, out ox, out oy, out oz);
        Check(Near(ox, 0) && Near(oy, 0) && Near(oz, -1), "Y du sprite = vers le haut (-Z)");

        // Règle du miroir : un joueur qui va vers l'est, vu par une caméra face au sud, regarde vers la gauche de l'écran.
        M.YawBasis(180f, out _, out _, out float crx, out float cry);
        bool wantRight = 1f * crx + 0f * cry >= 0f;
        Check(!wantRight, "joueur vers l'est vu depuis le nord -> tourné vers la gauche de l'écran");
    }

    private static void Angles()
    {
        Check(Near(M.WrapDegrees(-90f), 270f), "WrapDegrees négatif");
        Check(Near(M.WrapDegrees(725f), 5f), "WrapDegrees > 360");
        Check(Near(M.DeltaAngle(350f, 10f), 20f), "DeltaAngle passe par 0");
        Check(Near(M.DeltaAngle(10f, 350f), -20f), "DeltaAngle négatif");
        M.RotationZ(90f, out float qx, out float qy, out float qz, out float qw);
        M.Rotate(qx, qy, qz, qw, 0, 1, 0, out float ox, out float oy, out _);
        Check(Near(ox, -1f) && Near(oy, 0f), "RotationZ(90) tourne +Y vers -X (sens trigonométrique)");
        // flèche de la mini-carte : RotationZ(-lacet) doit pointer vers l'est pour un lacet de 90
        M.RotationZ(-90f, out qx, out qy, out qz, out qw);
        M.Rotate(qx, qy, qz, qw, 0, 1, 0, out ox, out oy, out _);
        Check(Near(ox, 1f) && Near(oy, 0f), "flèche de la mini-carte vers l'est pour un lacet de 90");
    }

    private static void Vision()
    {
        float r = 3f, amb = 0.16f, fs = 8f, fe = 13f;
        Check(Near(M.VisionBrightness(0f, r, false, amb, fs, fe), 1f), "pleine lumière au centre");
        Check(Near(M.VisionBrightness(5f, r, false, amb, fs, fe), amb), "pénombre hors du rayon");
        Check(Near(M.VisionBrightness(20f, r, false, amb, fs, fe), 0f), "noir au-delà de la zone affichée");
        Check(Near(M.VisionBrightness(5f, r, true, amb, fs, fe), 1f), "vision totale (fantôme / lobby)");
        float prev = 2f;
        bool monotonic = true;
        for (float d = 0f; d < 15f; d += 0.1f)
        {
            float b = M.VisionBrightness(d, r, false, amb, fs, fe);
            if (b > prev + 1e-5f) monotonic = false;
            prev = b;
        }
        Check(monotonic, "la luminosité ne fait que décroître avec la distance");
        Check(Near(M.VisionAlpha(1f, r, false), 1f) && Near(M.VisionAlpha(4f, r, false), 0f), "joueurs visibles seulement dans le rayon");
    }

    private static void Segments()
    {
        Check(M.SegmentIntersect(0, 0, 2, 0, 1, -1, 1, 1, out float t) && Near(t, 0.5f), "segments qui se croisent");
        Check(!M.SegmentIntersect(0, 0, 2, 0, 0, 1, 2, 1, out _), "segments parallèles");
        Check(!M.SegmentIntersect(0, 0, 1, 0, 2, -1, 2, 1, out _), "segments disjoints");
        Check(Near(WallGeometry.DistanceToBox(5, 0, 0, 0, 2, 2), 3f), "distance à une boîte");
        Check(WallGeometry.DistanceToBox(1, 1, 0, 0, 2, 2) == 0f, "point dans la boîte");
    }

    private static void WallStrips()
    {
        var px = new List<float> { 0, 2, 2, 0 };
        var py = new List<float> { 0, 0, 2, 2 };
        var vx = new List<float>(); var vy = new List<float>(); var vz = new List<float>();
        var shade = new List<float>(); var tris = new List<int>(); var segs = new List<WallSeg>();
        WallGeometry.AddStrips(px, py, true, 2f, 0.5f, 0.6f, 7, vx, vy, vz, shade, tris, segs);
        Check(segs.Count == 4, "4 segments pour un carré fermé");
        Check(vx.Count == 40, "4 côtés x 5 colonnes x 2 sommets");
        Check(tris.Count == 4 * 4 * 6, "2 triangles par morceau");
        bool heights = true, indices = true, owner = true;
        for (int i = 0; i < vz.Count; i++) if (!(vz[i] == 0f || vz[i] == -2f)) heights = false;
        foreach (int idx in tris) if (idx < 0 || idx >= vx.Count) indices = false;
        foreach (var s in segs) if (s.owner != 7) owner = false;
        Check(heights, "sommets au sol (z=0) et en haut (z=-hauteur)");
        Check(indices, "indices de triangles valides");
        Check(owner, "segments rattachés à leur maillage");

        // Polyligne ouverte : 2 points -> 1 segment
        vx.Clear(); vy.Clear(); vz.Clear(); shade.Clear(); tris.Clear(); segs.Clear();
        WallGeometry.AddStrips(new List<float> { 0, 1 }, new List<float> { 0, 0 }, false, 1f, 0.5f, 0.6f, 0, vx, vy, vz, shade, tris, segs);
        Check(segs.Count == 1 && vx.Count == 6, "polyligne ouverte");
    }
}
