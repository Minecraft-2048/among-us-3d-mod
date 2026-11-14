using BepInEx.Configuration;
using UnityEngine;

namespace AmongUs3D
{
    internal enum ControlScheme { Auto, Tank, Strafe }
    internal enum ToggleMode { Auto, Always, Never }
    internal enum ScreenCorner { Auto, BottomLeft, BottomRight, TopLeft, TopRight, TopCenter }

    /// <summary>
    /// Réglages (fichier BepInEx/config/fr.mods.amongus3d.cfg, modifiable avec un éditeur de texte).
    /// </summary>
    internal static class ModConfig
    {
        // Général
        public static ConfigEntry<bool> Enable3DAtStart;
        public static ConfigEntry<bool> Show2DInVent;
        public static ConfigEntry<ToggleMode> OnScreenButtons;
        public static ConfigEntry<float> ButtonScale;

        // Touches (PC)
        public static ConfigEntry<KeyCode> KeyToggle3D;
        public static ConfigEntry<KeyCode> KeyToggleCamera;
        public static ConfigEntry<KeyCode> KeyToggleMinimap;
        public static ConfigEntry<KeyCode> KeyMouseCapture;
        public static ConfigEntry<KeyCode> KeyDiagnostics;
        public static ConfigEntry<KeyCode> KeyTurnLeft;
        public static ConfigEntry<KeyCode> KeyTurnRight;
        public static ConfigEntry<KeyCode> KeyFreeCursor;
        public static ConfigEntry<KeyCode> KeySensitivityDown;
        public static ConfigEntry<KeyCode> KeySensitivityUp;
        public static ConfigEntry<KeyCode> KeyScreenshot;

        // Contrôles
        public static ConfigEntry<ControlScheme> Scheme;
        public static ConfigEntry<float> TurnSpeed;
        public static ConfigEntry<float> TurnStepDegrees;
        public static ConfigEntry<float> MouseSensitivity;
        public static ConfigEntry<float> TouchLookSpeed;
        public static ConfigEntry<bool> InvertLookY;
        public static ConfigEntry<float> MaxPitch;
        public static ConfigEntry<bool> KeyHintsOnButtons;
        public static ConfigEntry<bool> LookAtPetWhenPetting;
        public static ConfigEntry<bool> Hats3D;
        public static ConfigEntry<bool> Accessories3D;
        public static ConfigEntry<bool> Inventory3D;
        public static ConfigEntry<bool> Ghosts3D;
        public static ConfigEntry<bool> MouseLookAtStart;
        public static ConfigEntry<bool> ClickToUse;
        public static ConfigEntry<float> MouseAxisScale;
        public static ConfigEntry<int> ConfigVersion;
        public static ConfigEntry<bool> WallTrim;

        // Caméra
        public static ConfigEntry<bool> StartInThirdPerson;
        public static ConfigEntry<float> FieldOfView;
        public static ConfigEntry<float> EyeHeight;
        public static ConfigEntry<float> ThirdPersonDistance;
        public static ConfigEntry<float> ThirdPersonHeight;

        // Monde 3D
        public static ConfigEntry<float> WallHeight;
        public static ConfigEntry<float> ObstacleHeight;
        public static ConfigEntry<bool> RaiseWallArt;
        public static ConfigEntry<bool> Decor3D;
        public static ConfigEntry<ToggleMode> OuterWalls;
        public static ConfigEntry<bool> Characters3D;
        public static ConfigEntry<bool> SabotageArrows3D;
        public static ConfigEntry<float> CharacterScale;
        public static ConfigEntry<bool> DumpGeometry;
        public static ConfigEntry<float> FloorViewDistance;
        public static ConfigEntry<int> FloorTextureSize;
        public static ConfigEntry<string> WallLayers;
        public static ConfigEntry<string> ObstacleLayers;
        public static ConfigEntry<string> FloorHiddenLayers;
        public static ConfigEntry<string> ExtraUiLayers;
        public static ConfigEntry<int> Layer3D;
        public static ConfigEntry<int> LayerMinimap;
        public static ConfigEntry<string> BackgroundColor;
        public static ConfigEntry<string> WallColorSkeld;
        public static ConfigEntry<string> WallColorMira;
        public static ConfigEntry<string> WallColorPolus;
        public static ConfigEntry<string> WallColorAirship;
        public static ConfigEntry<string> WallColorFungle;
        public static ConfigEntry<string> WallColorLobby;
        public static ConfigEntry<string> DoorColor;
        public static ConfigEntry<string> ObstacleColor;

        // Vision
        public static ConfigEntry<bool> SimulateVision;
        public static ConfigEntry<float> AmbientInDarkness;

        // Mini-carte
        public static ConfigEntry<bool> MinimapAtStart;
        public static ConfigEntry<float> MinimapSize;
        public static ConfigEntry<ScreenCorner> MinimapPosition;

        public static void Bind(ConfigFile cfg)
        {
            const string G = "1. General", K = "2. Touches (PC)", C = "3. Controles", CAM = "4. Camera",
                         W = "5. Monde 3D", V = "6. Vision", MM = "7. Mini-carte";

            Enable3DAtStart = cfg.Bind(G, "Activer3DAuDemarrage", true, "Active la vue 3D dès que l'on entre dans le lobby ou une partie.");
            Show2DInVent = cfg.Bind(G, "Vue2DDansLesBouches", true, "Repasse temporairement en 2D quand on est dans une bouche d'aération (pour cliquer sur les flèches).");
            OnScreenButtons = cfg.Bind(G, "BoutonsAEcran", ToggleMode.Auto, "Boutons tactiles [3D] [1P/3P] [MAP] en haut de l'écran. Auto = seulement sur mobile.");
            ButtonScale = cfg.Bind(G, "TailleBoutons", 1f, "Taille des boutons à l'écran (1 = normal).");

            KeyToggle3D = cfg.Bind(K, "Basculer3D", KeyCode.F5, "Active / désactive la vue 3D.");
            KeyToggleCamera = cfg.Bind(K, "BasculerCamera", KeyCode.F6, "Première personne / troisième personne.");
            KeyToggleMinimap = cfg.Bind(K, "BasculerMiniCarte", KeyCode.F7, "Affiche / masque la mini-carte 2D.");
            KeyMouseCapture = cfg.Bind(K, "CaptureSouris", KeyCode.F8, "Active / désactive la caméra à la souris (curseur caché, comme dans Minecraft).");
            KeyFreeCursor = cfg.Bind(K, "LibererLaSouris", KeyCode.LeftAlt, "Tant que cette touche est maintenue, le curseur est libéré pour cliquer sur l'interface (boutons, carte...).");
            KeySensitivityDown = cfg.Bind(K, "SensibiliteMoins", KeyCode.F10, "Diminue la sensibilité de la caméra (souris et clavier).");
            KeySensitivityUp = cfg.Bind(K, "SensibilitePlus", KeyCode.F11, "Augmente la sensibilité de la caméra (souris et clavier).");
            KeyScreenshot = cfg.Bind(K, "CaptureEcran", KeyCode.F12, "Enregistre une capture d'écran du jeu (le chemin du fichier est écrit dans le journal).");
            KeyDiagnostics = cfg.Bind(K, "Diagnostic", KeyCode.F9, "Écrit des informations techniques dans le journal BepInEx (pour signaler un problème).");
            KeyTurnLeft = cfg.Bind(K, "TournerGauche", KeyCode.None, "Touche supplémentaire pour tourner à gauche (optionnel).");
            KeyTurnRight = cfg.Bind(K, "TournerDroite", KeyCode.None, "Touche supplémentaire pour tourner à droite (optionnel).");

            KeyHintsOnButtons = cfg.Bind(G, "TouchesSurLesBoutons", true, "Écrit en petit, en bas des boutons du jeu (Tuer, Sabotage, Signaler, Utiliser...), la touche du clavier qui fait la même chose (selon tes réglages de touches d'Among Us).");
            LookAtPetWhenPetting = cfg.Bind(C, "RegarderLAnimalCaresse", true, "En 1re personne, la caméra se tourne vers l'animal pendant qu'on le caresse, pour voir l'animation.");
            MouseLookAtStart = cfg.Bind(C, "SourisCommeMinecraft", true,
                "PC : dès l'entrée en 3D, la souris dirige la caméra (curseur caché) et les touches de déplacement deviennent avancer / reculer / pas de côté, comme dans Minecraft. " +
                "Le curseur revient tout seul dans les menus, réunions, tâches, bouches d'aération et quand la carte est ouverte.");
            ClickToUse = cfg.Bind(C, "ClicGaucheUtiliser", true, "Quand la souris dirige la caméra, le clic gauche fait la même chose que le bouton Utiliser du jeu.");
            Scheme = cfg.Bind(C, "SchemaDeControle", ControlScheme.Auto,
                "Tank : haut/bas = avancer/reculer, gauche/droite = tourner la caméra. " +
                "Strafe : déplacements relatifs à la caméra (gauche/droite = pas de côté), on tourne avec la souris ou en glissant le doigt. " +
                "Auto = Strafe quand la souris dirige la caméra et sur mobile, Tank sinon.");
            TurnSpeed = cfg.Bind(C, "VitesseRotation", 240f, "Vitesse de rotation au clavier / joystick (degrés par seconde).");
            TurnStepDegrees = cfg.Bind(C, "RotationParPas", 0f, "0 = rotation continue. Par ex. 90 = chaque appui sur gauche/droite tourne d'un quart de tour.");
            MouseSensitivity = cfg.Bind(C, "SensibiliteSouris", 0.2f, "Degrés de rotation par point de déplacement de la souris (Minecraft à 100 % = 0.15). F10 / F11 la règlent en jeu.");
            TouchLookSpeed = cfg.Bind(C, "SensibiliteTactile", 260f, "Degrés de rotation pour un glissement sur toute la hauteur de l'écran.");
            InvertLookY = cfg.Bind(C, "InverserAxeVertical", false, "Inverse le regard haut/bas.");
            MaxPitch = cfg.Bind(C, "InclinaisonMax", 60f, "Angle maximal pour regarder vers le haut ou le bas (0 = désactivé).");
            MouseAxisScale = cfg.Bind(C, "EchelleAxeSouris", 20f, "Technique : nombre de points de souris pour une unité de l'axe 'Mouse X' du jeu (20 dans Among Us, mesuré en jeu). À ne changer que si la souris est beaucoup trop lente ou trop rapide après une mise à jour du jeu.");

            StartInThirdPerson = cfg.Bind(CAM, "TroisiemePersonneAuDemarrage", false, "Commencer en vue à la troisième personne.");
            FieldOfView = cfg.Bind(CAM, "ChampDeVision", 70f, "Champ de vision vertical (degrés).");
            EyeHeight = cfg.Bind(CAM, "HauteurDesYeux", 0.55f, "Hauteur de la caméra en première personne (un personnage mesure environ 0.8).");
            ThirdPersonDistance = cfg.Bind(CAM, "DistanceTroisiemePersonne", 2.2f, "Distance derrière le personnage en troisième personne.");
            ThirdPersonHeight = cfg.Bind(CAM, "HauteurTroisiemePersonne", 1.2f, "Hauteur de la caméra en troisième personne.");

            WallHeight = cfg.Bind(W, "HauteurDesMurs", 2.0f, "Hauteur des murs extrudés.");
            ObstacleHeight = cfg.Bind(W, "HauteurDesObstacles", 0.3f, "Hauteur des obstacles bas (tables, caisses...). 0 = ne pas les afficher.");
            WallTrim = cfg.Bind(W, "MursAvecPlinthes", true, "Dessine une plinthe sombre en bas des murs unis et un bandeau en haut, pour qu'ils ressemblent à de vraies cloisons.");
            RaiseWallArt = cfg.Bind(W, "MursDebout", true, "Redresse à la verticale le dessin des murs (fenêtres, consoles de tâches...) à l'endroit où le personnage bute réellement. false = ancien rendu (dessin des murs à plat au sol + muret bas).");
            Decor3D = cfg.Bind(W, "Decor3D", true, "Les meubles sont de vrais objets modelés en 3D : tables rondes à pied central entourées de bancs, bouton d'urgence en dôme, consoles inclinées, caisses à cadre, lits, chaises, et des formes aux arêtes arrondies pour le reste (dessus et faces avant peints avec le dessin du jeu). false = ancien rendu : blocs extrudés sur les collisions.");
            OuterWalls = cfg.Bind(W, "MursExterieurs", ToggleMode.Auto, "Transforme aussi en murs les limites de la carte derrière lesquelles il n'y a rien. Auto = seulement sur les cartes d'intérieur (pas Polus ni The Fungle).");
            SabotageArrows3D = cfg.Bind(W, "Fleches3D", true, "Les flèches du jeu (sabotages...) suivent la caméra 3D : posées au-dessus de l'endroit visé quand il est devant soi, sinon en boussole autour du centre de l'écran (en haut = devant, en bas = derrière). false = flèches de la vue de dessus.");
            Hats3D = cfg.Bind(W, "Chapeaux3D", true, "Les chapeaux deviennent des objets en volume posés sur la tête des personnages 3D (fabriqués à partir de leur dessin). false = images plates tournées vers la caméra.");
            Accessories3D = cfg.Bind(W, "Accessoires3D", true, "La visière, la combinaison et l'animal deviennent aussi des objets 3D, comme les chapeaux : la visière et l'animal sont fabriqués à partir de leur dessin, la combinaison est peinte sur le corps 3D. false = visière et combinaison masquées sur les modèles 3D, animal en image plate.");
            Inventory3D = cfg.Bind(G, "Inventaire3D", true, "Dans l'inventaire (menu de personnalisation), l'aperçu du personnage est en 3D et tourne doucement. F5 (touche Basculer3D) passe de la 3D à la 2D pendant que le menu est ouvert.");
            Ghosts3D = cfg.Bind(W, "Fantomes3D", true, "Les fantômes (visibles quand on est mort soi-même) sont des modèles 3D transparents qui flottent. false = images plates.");
            Characters3D = cfg.Bind(W, "Personnages3D", true, "Les joueurs et les cadavres sont de vrais modèles 3D (corps, visière, sac à dos, jambes animées). false = panneaux plats tournés vers la caméra.");
            CharacterScale = cfg.Bind(W, "TaillePersonnages3D", 1f, "Taille des personnages 3D (1 = taille du jeu).");
            DumpGeometry = cfg.Bind(W, "ExporterLaGeometrie", false, "Diagnostic : écrit dans le journal toutes les collisions de la carte (lignes GEO|...) à chaque construction des murs.");
            FloorViewDistance = cfg.Bind(W, "DistanceDeVueDuSol", 14f, "Demi-taille de la zone de sol affichée autour du joueur.");
            FloorTextureSize = cfg.Bind(W, "ResolutionDuSol", 0, "Résolution de la texture du sol (0 = auto : 2048 sur PC, 1024 sur mobile).");
            WallLayers = cfg.Bind(W, "CalquesMurs", "auto", "Calques dont les collisions deviennent des murs pleine hauteur. auto = ce qui bloque la vision dans le jeu (Constants.ShadowMask), sinon des noms séparés par des virgules (ex. Shadow).");
            ObstacleLayers = cfg.Bind(W, "CalquesObstacles", "auto", "Calques des collisions physiques (murs où l'on bute, murets, meubles). auto = tout ce qui bloque les déplacements (Constants.ShipAndAllObjectsMask), sinon des noms (ex. Ship,ShortObjects).");
            FloorHiddenLayers = cfg.Bind(W, "CalquesCachesDuSol", "UI,Shadow,DrawShadows", "Calques à ne pas dessiner dans la texture du sol.");
            ExtraUiLayers = cfg.Bind(W, "CalquesInterfaceSupplementaires", "", "Calques supplémentaires à dessiner par-dessus la vue 3D (interface).");
            Layer3D = cfg.Bind(W, "NumeroCalque3D", -1, "Calque Unity libre utilisé pour la scène 3D (-1 = automatique).");
            LayerMinimap = cfg.Bind(W, "NumeroCalqueMiniCarte", -1, "Calque Unity libre utilisé pour la mini-carte (-1 = automatique).");
            BackgroundColor = cfg.Bind(W, "CouleurDuFond", "#05070C", "Couleur du ciel / de l'obscurité.");
            WallColorSkeld = cfg.Bind(W, "CouleurMursSkeld", "#6E7E94", "Couleur des murs sur The Skeld / Dleks.");
            WallColorMira = cfg.Bind(W, "CouleurMursMira", "#9CA8B6", "Couleur des murs sur MIRA HQ.");
            WallColorPolus = cfg.Bind(W, "CouleurMursPolus", "#8C7C6C", "Couleur des murs sur Polus.");
            WallColorAirship = cfg.Bind(W, "CouleurMursAirship", "#857362", "Couleur des murs sur The Airship.");
            WallColorFungle = cfg.Bind(W, "CouleurMursFungle", "#62785A", "Couleur des murs sur The Fungle.");
            WallColorLobby = cfg.Bind(W, "CouleurMursLobby", "#727E8E", "Couleur des murs dans le lobby.");
            DoorColor = cfg.Bind(W, "CouleurPortes", "#B4B4BE", "Couleur des portes fermées.");
            ObstacleColor = cfg.Bind(W, "CouleurObstacles", "#4C5260", "Couleur des obstacles bas.");

            SimulateVision = cfg.Bind(V, "SimulerLaVision", true, "Reproduit le rayon de vision du jeu (obscurité, sabotage des lumières, joueurs invisibles hors de vue).");
            AmbientInDarkness = cfg.Bind(V, "LumiereDansLObscurite", 0.16f, "Luminosité du décor hors du rayon de vision (0 = noir complet).");

            MinimapAtStart = cfg.Bind(MM, "MiniCarteAuDemarrage", true, "Affiche une petite vue 2D (exactement la vue normale du jeu) avec une flèche indiquant où l'on regarde.");
            MinimapSize = cfg.Bind(MM, "TailleMiniCarte", 0.28f, "Hauteur de la mini-carte en fraction de la hauteur de l'écran.");
            MinimapPosition = cfg.Bind(MM, "PositionMiniCarte", ScreenCorner.Auto, "Auto = en bas à gauche sur PC, en haut au centre sur mobile.");

            ConfigVersion = cfg.Bind(G, "VersionDesReglages", 0, "Ne pas modifier : sert à adopter les nouvelles valeurs par défaut quand le mod est mis à jour.");
            Migrate();
        }

        private const int CurrentConfigVersion = 2;

        /// <summary>
        /// Un fichier de réglages existant garde ses anciennes valeurs : quand une valeur par défaut change dans une
        /// nouvelle version du mod, on l'adopte à condition que le joueur n'y ait pas touché.
        /// </summary>
        private static void Migrate()
        {
            int from = ConfigVersion.Value;
            if (from >= CurrentConfigVersion) return;
            if (from < 2)
            {
                // 1.1 : caméra plus vive, regard vertical plus ample.
                Adopt(TurnSpeed, 150f, 240f);
                Adopt(MouseSensitivity, 0.15f, 0.25f);
                Adopt(TouchLookSpeed, 180f, 260f);
                Adopt(MaxPitch, 35f, 60f);
                if (DumpGeometry.Value) DumpGeometry.Value = false; // n'a servi qu'à mettre au point la 1.1
            }
            ConfigVersion.Value = CurrentConfigVersion;
        }

        private static void Adopt(ConfigEntry<float> entry, float oldDefault, float newDefault)
        {
            if (System.MathF.Abs(entry.Value - oldDefault) < 1e-4f) entry.Value = newDefault;
        }

        /// <summary>Lit une couleur "#RRGGBB" (ou "#RRGGBBAA").</summary>
        public static bool TryParseHex(string s, out float r, out float g, out float b)
        {
            r = g = b = 1f;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim().TrimStart('#');
            if (s.Length != 6 && s.Length != 8) return false;
            try
            {
                r = System.Convert.ToInt32(s.Substring(0, 2), 16) / 255f;
                g = System.Convert.ToInt32(s.Substring(2, 2), 16) / 255f;
                b = System.Convert.ToInt32(s.Substring(4, 2), 16) / 255f;
                return true;
            }
            catch { return false; }
        }
    }
}
