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
        GhostShape();
        HatShapes();
        HatWrap();
        Props();
        SkinSampling();
        WallRulesTests();
        Animations();
        KeyLayouts();
        TerrainLevels();

        Console.WriteLine($"{_passed} tests réussis, {_failed} échecs");
        return _failed == 0 ? 0 : 1;
    }

    private static bool ValidMesh(ShapeMesh m)
    {
        foreach (int t in m.Tris)
            if (t < 0 || t >= m.VertexCount) return false;
        for (int i = 0; i < m.VertexCount; i++)
        {
            float l = MathF.Sqrt(m.Nx[i] * m.Nx[i] + m.Ny[i] * m.Ny[i] + m.Nz[i] * m.Nz[i]);
            if (MathF.Abs(l - 1f) > 1e-3f) return false;
        }
        return m.Tris.Count % 3 == 0;
    }

    /// <summary>Chaque triangle tourne sa face vers l'extérieur (dans le sens des normales de ses sommets).</summary>
    private static bool FacesOutward(ShapeMesh m)
    {
        for (int t = 0; t < m.Tris.Count; t += 3)
        {
            int a = m.Tris[t], b = m.Tris[t + 1], c = m.Tris[t + 2];
            float ux = m.X[b] - m.X[a], uy = m.Y[b] - m.Y[a], uz = m.Z[b] - m.Z[a];
            float vx = m.X[c] - m.X[a], vy = m.Y[c] - m.Y[a], vz = m.Z[c] - m.Z[a];
            float cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
            float nx = m.Nx[a] + m.Nx[b] + m.Nx[c], ny = m.Ny[a] + m.Ny[b] + m.Ny[c], nz = m.Nz[a] + m.Nz[b] + m.Nz[c];
            if (cx * nx + cy * ny + cz * nz < 0f) return false;
        }
        return true;
    }

    private static void GhostShape()
    {
        ShapeMesh g = CrewShape.Ghost();
        Check(g.VertexCount > 500 && g.Tris.Count > 900, "fantôme : maillage non vide");
        Check(ValidMesh(g), "fantôme : indices valides et normales unitaires");
        Check(FacesOutward(g), "fantôme : triangles tournés vers l'extérieur");
        float minY = float.MaxValue, minZ = float.MaxValue, maxZ = float.MinValue, maxR = 0f;
        for (int i = 0; i < g.VertexCount; i++)
        {
            minY = MathF.Min(minY, g.Y[i]);
            minZ = MathF.Min(minZ, g.Z[i]);
            maxZ = MathF.Max(maxZ, g.Z[i]);
            maxR = MathF.Max(maxR, MathF.Abs(g.X[i]));
        }
        Check(minY < -0.2f && minZ < 0.1f, "fantôme : la queue part vers l'arrière et descend près du sol");
        Check(maxZ > 0.95f && maxZ < 1.05f && maxR < 0.32f, "fantôme : même tête que le personnage vivant");
    }

    private static void Polygon(List<float> xs, List<float> ys, float cx, float cy, float rx, float ry, int n, float angle = 0f)
    {
        xs.Clear(); ys.Clear();
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        for (int i = 0; i < n; i++)
        {
            float a = MathF.PI * (2f * i / n + 1f / n), u = rx * MathF.Cos(a), v = ry * MathF.Sin(a);
            xs.Add(cx + u * c - v * s); ys.Add(cy + u * s + v * c);
        }
    }

    private static void Props()
    {
        var xs = new List<float>();
        var ys = new List<float>();
        var indoor = new PropHints { Indoor = true, Height = 0.3f, BaseColor = 0x4C5260, WallX = 0f, WallY = 1f, LookX = 0f, LookY = 1f };

        Polygon(xs, ys, 5f, -2f, 1.3f, 1.3f, 16);
        Footprint round = PropShape.Analyze(xs, ys);
        Check(round.Roundness > 0.95f && round.IsRound && Near(round.Cx, 5f, 0.05f) && Near(round.Cy, -2f, 0.05f), "meuble : disque reconnu et centré");
        Check(PropShape.Classify("Collider", round, indoor) == PropKind.RoundTable, "meuble : grande forme ronde = table ronde");
        PropHints outdoor = indoor;
        outdoor.Indoor = false;
        Check(PropShape.Classify("Rock", round, outdoor) == PropKind.Generic, "meuble : forme ronde en extérieur = forme générique");

        // distributeur : meuble haut, à la hauteur lue sur son dessin
        Polygon(xs, ys, 3f, 1f, 0.5f, 0.5f, 4);
        Footprint square = PropShape.Analyze(xs, ys);
        Check(PropShape.Classify("VendingMachineConsole", square, indoor) == PropKind.Cabinet, "meuble : un distributeur est un meuble haut");
        PropHints tall = indoor;
        tall.ArtHeight = 1.1f;
        var cabSolid = new ShapeMesh();
        var cabArt = new ShapeMesh();
        float cabTop = PropShape.Build(PropKind.Cabinet, square, xs, ys, tall, cabSolid, cabArt);
        float cabMax = 0f;
        for (int i = 0; i < cabSolid.VertexCount; i++) cabMax = MathF.Max(cabMax, cabSolid.Z[i]);
        Check(Near(cabTop, 1.1f) && Near(cabMax, 1.1f, 0.01f) && ValidMesh(cabSolid) && cabArt.VertexCount > 0, "meuble haut : à la hauteur de son dessin, façade peinte");
        var genSolid = new ShapeMesh();
        var genArt = new ShapeMesh();
        Check(Near(PropShape.Build(PropKind.Generic, square, xs, ys, tall, genSolid, genArt), 1.1f)
              && Near(PropShape.Build(PropKind.Generic, square, xs, ys, indoor, new ShapeMesh(), new ShapeMesh()), 0.3f),
              "meuble non reconnu : vraie hauteur si le dessin la donne, basse sinon");

        // rectangle de 2 x 0,8 tourné de 30° : centre, axes et demi-tailles retrouvés
        float ang = MathF.PI / 6f;
        Polygon(xs, ys, 2f, 3f, 1f * MathF.Sqrt(2f), 0.4f * MathF.Sqrt(2f), 4, ang);
        Footprint box = PropShape.Analyze(xs, ys);
        Check(Near(box.HalfLen, 1f, 0.01f) && Near(box.HalfWid, 0.4f, 0.01f) && box.Fill > 0.99f && box.IsBox, "meuble : rectangle orienté retrouvé");
        Check(Near(MathF.Abs(box.Ux * MathF.Cos(ang) + box.Uy * MathF.Sin(ang)), 1f, 0.01f) && Near(box.Cx, 2f) && Near(box.Cy, 3f), "meuble : grand axe et centre du rectangle");
        Check(PropShape.Classify("nav_chairmid", box, indoor) == PropKind.Chair, "meuble : chaise reconnue à son nom");
        Check(PropShape.Classify("Leftbox", box, indoor) == PropKind.Crate, "meuble : caisse reconnue à son nom");
        Check(PropShape.Classify("admin_bridge", box, indoor) == PropKind.Console, "meuble : console reconnue à son nom");
        Check(PropShape.Classify("EmergencyConsole", round, indoor) == PropKind.Button && PropShape.Classify("WeaponConsole", box, indoor) == PropKind.Chair,
              "meuble : bouton d'urgence et poste d'armes reconnus");
        PropHints emergency = indoor;
        emergency.Emergency = true;
        Check(PropShape.Classify("Collider", round, emergency) == PropKind.RoundTable && PropShape.Classify("x", box, emergency) == PropKind.Table,
              "meuble : le support du bouton d'urgence est une table");
        Check(PropShape.Contains(xs, ys, 2f, 3f) && !PropShape.Contains(xs, ys, 4f, 3f), "meuble : point dans le polygone");

        var cleanX = new List<float> { 0f, 0f, 1f, 1f, 0f };
        var cleanY = new List<float> { 0f, 1f, 1f, 0f, 0f };
        PropShape.Clean(cleanX, cleanY);
        Check(cleanX.Count == 4 && Geo.SignedArea(cleanX, cleanY) > 0f, "meuble : point de fermeture retiré, sens direct");

        // chaque sorte de meuble : maillage valide, coloré, posé au sol et contenu dans son empreinte
        foreach (PropKind kind in Enum.GetValues(typeof(PropKind)))
        {
            bool roundKind = kind == PropKind.RoundTable || kind == PropKind.Drum;
            if (roundKind) Polygon(xs, ys, 5f, -2f, kind == PropKind.Drum ? 0.4f : 1.3f, kind == PropKind.Drum ? 0.4f : 1.1f, 20);
            else Polygon(xs, ys, 2f, 3f, 0.9f * MathF.Sqrt(2f), 0.45f * MathF.Sqrt(2f), 4, ang);
            PropShape.Clean(xs, ys);
            Footprint fp = PropShape.Analyze(xs, ys);
            var solid = new ShapeMesh();
            var art = new ShapeMesh();
            float top = PropShape.Build(kind, fp, xs, ys, emergency, solid, art);
            float maxTop = kind == PropKind.Cabinet ? 2f : 0.7f; // un meuble haut (distributeur) dépasse les autres
            bool inside = true, grounded = false;
            for (int i = 0; i < solid.VertexCount; i++)
            {
                inside &= PropShape.Near(fp, solid.X[i], solid.Y[i], 0.03f) && solid.Z[i] > -1e-4f && solid.Z[i] < maxTop + 0.1f;
                grounded |= solid.Z[i] < 1e-3f;
            }
            bool painted = kind == PropKind.Chair || kind == PropKind.Bed || kind == PropKind.Button || art.Tris.Count > 0;
            Check(ValidMesh(solid) && ValidMesh(art) && solid.HasColors && solid.Tris.Count > 30 && top > 0.1f && top < maxTop,
                  "meuble 3D " + kind + " : maillage valide");
            Check(inside && grounded && painted, "meuble 3D " + kind + " : posé au sol, dans son empreinte, dessus peint");
            if (kind == PropKind.RoundTable || kind == PropKind.Button)
            {
                bool red = false;
                for (int i = 0; i < solid.VertexCount; i++) red |= solid.Rgba[i] >> 8 == 0xE3262B && solid.Z[i] > top;
                Check(red, "meuble 3D : dôme rouge du bouton d'urgence sur la table");
            }
        }

        Check(PropShape.Shade(0f, 0f, 1f, 0.4f) > 0.9f && PropShape.Shade(0f, 0f, -1f, 0.4f) < 0.6f, "meuble : dessus éclairé, dessous sombre");
        Check(PropShape.Shade(0f, -1f, 0f, 0f) < PropShape.Shade(0f, -1f, 0f, 0.3f), "meuble : bas assombri au contact du sol");
    }

    private static void SkinSampling()
    {
        // image 8 x 8 : carré rouge bordé de noir, transparent autour ; un pixel = 0,125
        const int W = 8, H = 8;
        var px = new byte[W * H * 4];
        for (int j = 1; j < 7; j++)
        for (int i = 1; i < 7; i++)
        {
            int k = (j * W + i) * 4;
            bool border = i == 1 || i == 6 || j == 1 || j == 6;
            px[k] = border ? (byte)0 : (byte)220; px[k + 1] = border ? (byte)0 : (byte)30; px[k + 2] = border ? (byte)0 : (byte)40; px[k + 3] = 255;
        }
        Check(HatShape.SampleProfile(px, W, H, 0f, 0.125f, 0f, 0.125f, 0.5f, 0.5f, out byte r, out _, out _) && r == 220, "combinaison : couleur du dessin lue");
        Check(!HatShape.SampleProfile(px, W, H, 0f, 0.125f, 0f, 0.125f, 0.05f, 0.5f, out _, out _, out _), "combinaison : transparent ignoré");
        Check(HatShape.SampleInside(px, W, H, 0f, 0.125f, 0f, 0.125f, 0.8f, 0.5f, 0.5f, 0.5f, out r, out _, out _) && r == 220, "combinaison : contour noir évité");
        Check(HatShape.SampleProfile(px, W, H, 1f, -0.125f, 0f, 0.125f, 0.5f, 0.5f, out r, out _, out _) && r == 220, "combinaison : dessin tourné vers la gauche");
        Check(ValidMesh(CrewShape.Body()) && FacesOutward(CrewShape.Body()), "corps 3D plus fin : maillage valide");
    }

    private static void HatShapes()
    {
        const int W = 32, H = 32;
        const float cell = 1f / 32f, y0 = -0.5f, z0 = 0.8f;
        var px = new byte[W * H * 4];
        for (int j = 4; j < 14; j++)
        for (int i = 6; i < 26; i++)
        {
            int k = (j * W + i) * 4;
            px[k] = 200; px[k + 1] = 40; px[k + 2] = 40; px[k + 3] = 255;
        }
        ShapeMesh hat = HatShape.Build(px, W, H, y0, cell, z0, cell, HatShape.HalfThickness);
        Check(hat != null && hat.HasColors && hat.Tris.Count > 0, "chapeau : maillage coloré produit");
        if (hat == null) return;
        Check(ValidMesh(hat), "chapeau : indices valides et normales unitaires");
        Check(FacesOutward(hat), "chapeau : triangles tournés vers l'extérieur");

        float maxX = 0f, minX = 0f;
        for (int i = 0; i < hat.VertexCount; i++) { maxX = MathF.Max(maxX, hat.X[i]); minX = MathF.Min(minX, hat.X[i]); }
        Check(maxX <= HatShape.HalfThickness + 1e-4f && MathF.Abs(maxX + minX) < 1e-4f, "chapeau : épaisseur bornée et symétrique");

        // le bas (posé sur la tête) reste épais, le haut s'amincit vers son bord
        float bottom = ThicknessAt(hat, y0 + 16 * cell, z0 + 4 * cell);
        float top = ThicknessAt(hat, y0 + 16 * cell, z0 + 14 * cell);
        float middle = ThicknessAt(hat, y0 + 16 * cell, z0 + 9 * cell);
        Check(bottom > 0.6f * HatShape.HalfThickness, "chapeau : le bord du bas garde son épaisseur");
        Check(top < 0.5f * bottom && middle > top, "chapeau : le haut s'arrondit");

        // un dessin tourné vers la gauche (largeur de pixel négative) donne la même forme, en miroir
        ShapeMesh left = HatShape.Build(px, W, H, -y0, -cell, z0, cell, HatShape.HalfThickness);
        Check(left != null && left.VertexCount == hat.VertexCount && FacesOutward(left), "chapeau : version tournée vers la gauche");

        Check(HatShape.Build(new byte[W * H * 4], W, H, 0f, 0.1f, 0f, 0.1f, 0.3f) == null, "chapeau : image vide = pas de chapeau");
    }

    /// <summary>Chapeaux : coque autour de la tête (bonnet, couronne) ou objet posé dessus (gonflé), exceptions.</summary>
    private static void HatWrap()
    {
        const int W = 64, H = 64;
        const float cell = 1f / 64f, y0 = -0.5f, z0 = 0.6f; // image : y de -0,5 à 0,5 ; z de 0,6 à 1,6
        byte[] Img(Func<float, float, bool> inside)
        {
            var px = new byte[W * H * 4];
            for (int j = 0; j < H; j++)
            for (int i = 0; i < W; i++)
            {
                float y = y0 + (i + 0.5f) * cell, z = z0 + (j + 0.5f) * cell;
                if (!inside(y, z)) continue;
                int k = (j * W + i) * 4;
                px[k] = 60; px[k + 1] = 60; px[k + 2] = 200; px[k + 3] = 255;
            }
            return px;
        }
        float kx = CrewShape.BodyRadiusX / CrewShape.BodyRadiusY;

        // bonnet : couvre le haut de la tête -> coque qui l'entoure, fermée en haut
        ShapeMesh b = HatShape.BuildHat(Img((y, z) => z >= 0.78f && z <= 1.08f && MathF.Abs(y) <= 0.27f), W, H, y0, cell, z0, cell, HatShape.HalfThickness);
        Check(b != null && ValidMesh(b) && FacesOutward(b), "coque : maillage valide, triangles tournés vers l'extérieur");
        if (b != null)
        {
            bool outside = true, lid = false;
            float maxX = 0f;
            for (int i = 0; i < b.VertexCount; i++)
            {
                maxX = MathF.Max(maxX, MathF.Abs(b.X[i]));
                if (b.Nz[i] > 0.99f && b.Z[i] > 1.05f) lid = true;
                float hd = HatShape.HeadHalfDepth(b.Z[i]);
                if (hd <= 0f) continue;
                float u = b.X[i] / (kx * hd), v = b.Y[i] / hd;
                if (u * u + v * v < 0.8f) outside = false;
            }
            Check(outside, "coque : rien ne rentre dans la tête");
            Check(maxX > 0.28f && maxX < 0.6f, "coque : aussi large que la tête, sans plus");
            Check(lid, "coque : fermée en haut");
        }

        // couronne : bandeau + trois petites pointes -> les pointes se posent sur l'anneau, des deux côtés de la tête
        ShapeMesh c = HatShape.BuildHat(Img((y, z) => MathF.Abs(y) <= 0.26f && ((z >= 0.86f && z <= 1.02f)
            || (z > 1.02f && z <= 1.12f && MathF.Abs(MathF.IEEERemainder(y, 0.16f)) < 0.04f * (1.12f - z) / 0.10f))),
            W, H, y0, cell, z0, cell, HatShape.HalfThickness);
        Check(c != null && ValidMesh(c) && FacesOutward(c), "couronne : maillage valide");
        if (c != null)
        {
            bool left = false, right = false;
            for (int i = 0; i < c.VertexCount; i++)
            {
                if (c.Z[i] < 1.05f) continue;
                if (c.X[i] > 0.15f) right = true;
                if (c.X[i] < -0.15f) left = true;
            }
            Check(left && right, "couronne : pointes sur l'anneau, de chaque côté de la tête");
        }

        // boule posée sur la tête -> objet gonflé, à peu près rond
        ShapeMesh o = HatShape.BuildHat(Img((y, z) => (y * y + (z - 1.15f) * (z - 1.15f)) <= 0.12f * 0.12f), W, H, y0, cell, z0, cell, HatShape.HalfThickness);
        Check(o != null && ValidMesh(o) && FacesOutward(o), "objet posé : maillage valide");
        if (o != null)
        {
            float maxX = 0f;
            for (int i = 0; i < o.VertexCount; i++) maxX = MathF.Max(maxX, MathF.Abs(o.X[i]));
            Check(maxX > 0.03f && maxX < 0.16f, "objet posé : épaisseur proche de sa largeur (pas une plaque)");
        }

        Check(HatShape.KeepPuffed("hat_EarmuffGreen") && HatShape.KeepPuffed("hat_mira_headset_pink") && HatShape.KeepPuffed("hat_pk03_Headphones"),
              "exceptions : cache-oreilles et casques gonflés");
        Check(!HatShape.KeepPuffed("hat_crownBean") && !HatShape.KeepPuffed(null), "exceptions : les autres chapeaux suivent la règle");
    }

    private static float ThicknessAt(ShapeMesh m, float y, float z)
    {
        float best = -1f;
        for (int i = 0; i < m.VertexCount; i++)
            if (MathF.Abs(m.Y[i] - y) < 1e-4f && MathF.Abs(m.Z[i] - z) < 1e-4f) best = MathF.Max(best, m.X[i]);
        return best;
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

    /// <summary>Découpe un polygone en morceaux de 0,5 au plus, comme le fait le mod avec les collisions d'une carte.</summary>
    private static WallRules.Piece[] Chain(float[] xs, float[] ys, bool closed, Func<float, float, int> kindAt, List<WallSeg> physical)
    {
        var list = new List<WallRules.Piece>();
        int n = xs.Length, segs = closed ? n : n - 1;
        for (int s = 0; s < segs; s++)
        {
            float ax = xs[s], ay = ys[s], bx = xs[(s + 1) % n], by = ys[(s + 1) % n];
            physical.Add(new WallSeg { ax = ax, ay = ay, bx = bx, by = by });
            float len = MathF.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            int pieces = Math.Max(1, (int)MathF.Ceiling(len / 0.5f));
            for (int k = 0; k < pieces; k++)
            {
                float t0 = (float)k / pieces, t1 = (float)(k + 1) / pieces;
                float x0 = ax + (bx - ax) * t0, y0 = ay + (by - ay) * t0, x1 = ax + (bx - ax) * t1, y1 = ay + (by - ay) * t1;
                list.Add(new WallRules.Piece { Ax = x0, Ay = y0, Bx = x1, By = y1, Kind = kindAt((x0 + x1) * 0.5f, (y0 + y1) * 0.5f) });
            }
        }
        return list.ToArray();
    }

    private static List<WallSeg> Rectangle(float x0, float y0, float x1, float y1)
    {
        return new List<WallSeg>
        {
            new WallSeg { ax = x0, ay = y0, bx = x1, by = y0 }, new WallSeg { ax = x1, ay = y0, bx = x1, by = y1 },
            new WallSeg { ax = x1, ay = y1, bx = x0, by = y1 }, new WallSeg { ax = x0, ay = y1, bx = x0, by = y0 },
        };
    }

    private static int CountKind(WallRules.Piece[] c, int kind)
    {
        int n = 0;
        foreach (WallRules.Piece p in c) if (p.Kind == kind) n++;
        return n;
    }

    private static void WallRulesTests()
    {
        const int Low = WallRules.Low, Tall = WallRules.Tall;

        // Salle de 10 x 6. Le mur "vision" est collé aux murs, sauf au sud où il est 1 en retrait :
        // tout le côté sud est resté bas, ainsi que deux morceaux au milieu du mur nord.
        var physical = new List<WallSeg>();
        List<WallSeg> vision = Rectangle(-0.3f, -1f, 10.3f, 6.3f);
        WallRules.Piece[] room = Chain(new[] { 0f, 10f, 10f, 0f }, new[] { 0f, 0f, 6f, 6f }, true,
            (x, y) => y < 0.01f || (y > 5.99f && x > 4.5f && x < 5.5f) ? Low : Tall, physical);
        Check(WallRules.Walkable(5f, 3f, physical) && !WallRules.Walkable(5f, -0.5f, physical) && !WallRules.Walkable(-3f, 3f, physical),
              "murs : l'intérieur de la salle est praticable, pas l'extérieur");
        Check(WallRules.VisionFirst(5f, 0f, 0f, -1f, 3f, vision, physical) && !WallRules.VisionFirst(5f, 0f, 0f, -1f, 0.5f, vision, physical),
              "murs : mur vision trouvé derrière, dans la limite de distance");
        int lowBefore = CountKind(room, Low);
        int done = WallRules.CompleteWalls(room, true, vision, physical);
        Check(lowBefore == 22 && done == 22 && CountKind(room, Low) == 0, "murs : côté de salle et trou dans un mur complétés");

        // Barrière isolée au milieu de la salle (praticable des deux côtés) : elle reste basse.
        WallRules.Piece[] rail = Chain(new[] { 3f, 5f }, new[] { 3f, 3f }, false, (x, y) => Low, physical);
        Check(WallRules.CompleteWalls(rail, false, vision, physical) == 0, "murs : une barrière isolée reste basse");

        // Pupitre de 2 x 1 adossé au mur sud : il ne devient pas un mur, son contour refermé devient un meuble.
        physical = new List<WallSeg>();
        vision = Rectangle(-0.3f, -0.3f, 10.3f, 6.3f);
        WallRules.Piece[] desk = Chain(new[] { 0f, 4f, 4f, 6f, 6f, 10f, 10f, 0f }, new[] { 0f, 0f, 1f, 1f, 0f, 0f, 6f, 6f }, true,
            (x, y) => x > 3.9f && x < 6.1f && y < 1.1f && !(y < 0.01f) ? Low : Tall, physical);
        Check(WallRules.CompleteWalls(desk, true, vision, physical) == 0, "meuble adossé : son contour ne devient pas un mur");
        var bx = new List<List<float>>();
        var by = new List<List<float>>();
        WallRules.FindBumps(desk, true, physical, bx, by);
        bool one = bx.Count == 1;
        Footprint f = one ? PropShape.Analyze(bx[0], by[0]) : default;
        Check(one && Near(f.Area, 2f, 0.05f) && Near(f.Cx, 5f, 0.05f) && Near(f.Cy, 0.5f, 0.05f), "meuble adossé : contour refermé, bonne place et bonne taille");
        Check(CountKind(desk, WallRules.Bump) == 8 && CountKind(desk, Low) == 0, "meuble adossé : ses morceaux ne sont plus des murets");

        // Alcôve de 2 x 1 creusée dans le mur sud (on y marche) : ce n'est pas un meuble, et son fond est un mur.
        physical = new List<WallSeg>();
        vision = Rectangle(-0.3f, -1.3f, 10.3f, 6.3f);
        WallRules.Piece[] alcove = Chain(new[] { 0f, 4f, 4f, 6f, 6f, 10f, 10f, 0f }, new[] { 0f, 0f, -1f, -1f, 0f, 0f, 6f, 6f }, true,
            (x, y) => x > 3.9f && x < 6.1f && y < -0.01f ? Low : Tall, physical);
        bx.Clear(); by.Clear();
        WallRules.FindBumps((WallRules.Piece[])alcove.Clone(), true, physical, bx, by);
        Check(bx.Count == 0, "alcôve : un creux praticable n'est pas un meuble");
        Check(WallRules.CompleteWalls(alcove, true, vision, physical) == 4, "alcôve : le fond devient un mur");
    }

    private static void TerrainLevels()
    {
        // Carte de 10 x 10 : une zone basse (y de 0 à 4), une falaise (y de 4 à 6), une zone haute (y de 6 à 10),
        // et une échelle de 3 de long qui monte de l'une à l'autre.
        var walls = Rectangle(0f, 0f, 10f, 10f);
        walls.Add(new WallSeg { ax = 0f, ay = 4f, bx = 10f, by = 4f });
        walls.Add(new WallSeg { ax = 0f, ay = 6f, bx = 10f, by = 6f });
        var ladders = new List<WallSeg> { new WallSeg { ax = 5f, ay = 3.5f, bx = 5f, by = 6.5f } };
        Terrain t = Terrain.Build(walls, ladders);
        Check(t.Active && t.ZoneCount == 2 && Near(t.MaxHeight, 3f), "relief : deux niveaux séparés par le dénivelé de l'échelle");
        float low = t.Height(5f, 2f), high = t.Height(5f, 8f), mid = t.Height(5f, 5f);
        Check(Near(high - low, 3f, 0.02f) && mid > low + 0.8f && mid < high - 0.8f, "relief : zones plates, falaise en pente entre les deux");
        Check(Near(t.Height(2f, 1f), low, 0.02f) && Near(t.Height(8f, 9f), high, 0.02f), "relief : toute une zone est au même niveau");
        Check(Near(MathF.Min(low, high), 0f, 0.02f) || Near(MathF.Max(low, high), 0f, 0.02f), "relief : le niveau 0 est celui d'une des zones");
        Check(!Terrain.Build(walls, new List<WallSeg>()).Active && Terrain.Flat.Height(3f, 3f) == 0f, "relief : sans échelle la carte reste plate");
        // une échelle qui ne change pas de zone (pas de falaise entre ses bouts) ne crée pas de relief
        var open = Rectangle(0f, 0f, 10f, 10f);
        Check(!Terrain.Build(open, ladders).Active, "relief : une échelle dans une seule zone ne crée pas de niveau");
    }

    private static void KeyLayouts()
    {
        Check(KeyLayout.UsScanCode("Q") == 0x10 && KeyLayout.UsScanCode("a") == 0x1E && KeyLayout.UsScanCode("M") == 0x32 && KeyLayout.UsScanCode("Z") == 0x2C,
              "touches : place des lettres sur le clavier américain");
        Check(KeyLayout.UsScanCode("Semicolon") == 0x27 && KeyLayout.UsScanCode(";") == 0x27 && KeyLayout.UsScanCode(",") == 0x33, "touches : signes de ponctuation");
        Check(KeyLayout.UsScanCode("Tab") == 0 && KeyLayout.UsScanCode("1") == 0 && KeyLayout.UsScanCode("Espace") == 0 && KeyLayout.UsScanCode(null) == 0,
              "touches : chiffres et touches spéciales laissés tels quels");
    }

    private static void Animations()
    {
        const float flat = MathF.PI * 0.5f;
        bool rising = true, bounded = true;
        for (float t = 0f; t < 1.2f; t += 0.01f)
        {
            float a = CrewShape.FallAngle(t);
            if (t < 0.41f && CrewShape.FallAngle(t + 0.01f) < a) rising = false;
            if (a < 0f || a > flat + 1e-4f) bounded = false;
        }
        Check(CrewShape.FallAngle(0f) == 0f && rising && bounded, "chute : part de debout, bascule sans dépasser l'horizontale");
        Check(CrewShape.FallAngle(0.1f) < 0.1f && Near(CrewShape.FallAngle(0.42f), flat, 0.01f), "chute : lente au début, au sol en 0,42 s");
        Check(CrewShape.FallAngle(0.53f) < flat - 0.1f && Near(CrewShape.FallAngle(CrewShape.FallDuration), flat) && Near(CrewShape.FallAngle(60f), flat),
              "chute : un rebond, puis le corps reste couché");

        ShapeMesh corpse = CrewShape.Corpse();
        float lift = CrewShape.RestLift(corpse, flat);
        Check(Near(CrewShape.RestLift(corpse, 0f), 0f, 0.01f) && lift > 0.15f && lift < 0.35f, "chute : debout les pieds touchent le sol, couché le corps est remonté");
        float lowest = float.MaxValue, a1 = 1f, l1 = CrewShape.RestLift(corpse, a1);
        for (int i = 0; i < corpse.VertexCount; i++)
            lowest = MathF.Min(lowest, -corpse.Y[i] * MathF.Sin(a1) + corpse.Z[i] * MathF.Cos(a1) + l1);
        Check(Near(lowest, 0f, 1e-4f), "chute : pendant la bascule, le point le plus bas reste au sol");

        CrewShape.VentPop(0f, out float s0, out float z0);
        CrewShape.VentPop(0.2f, out float s1, out float z1);
        CrewShape.VentPop(1f, out float s2, out float z2);
        Check(Near(s0, 1f) && Near(z0, 0f) && Near(s1, 1f) && z1 > 0.1f && s2 < 0.1f && z2 < 0f, "bouche d'aération : petit bond, puis le personnage rétrécit et s'enfonce");

        ShapeMesh puddle = CrewShape.Puddle();
        bool level = true;
        float rMin = float.MaxValue, rMax = 0f;
        for (int i = 1; i < puddle.VertexCount; i++)
        {
            if (puddle.Z[i] != 0f) level = false;
            float r = MathF.Sqrt(puddle.X[i] * puddle.X[i] + puddle.Y[i] * puddle.Y[i]);
            rMin = MathF.Min(rMin, r); rMax = MathF.Max(rMax, r);
        }
        Check(ValidMesh(puddle) && FacesOutward(puddle) && level && rMin > 0.3f && rMax < 0.6f, "flaque : disque plat au contour irrégulier");
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
