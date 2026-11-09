using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;
using UnityEngine.Audio;

namespace MontreTactique
{
    /// Montre Mousqueton 1.12.0 (base : 1.5.4)
    /// Correction : le titre de section F12 "7. Lueur de l'ecran" contenait une apostrophe,
    /// interdite par BepInEx, ce qui faisait planter le plugin au demarrage. Titre corrige.
    [DefaultExecutionOrder(32000)]
    [BepInPlugin("com.yann.montretactique", "Montre Mousqueton", "1.12.0")]
    public class MontreTactiquePlugin : BaseUnityPlugin
    {
        private const string MontreTpl = "6907a1f0c2b5a9d4e3f10001";
        private const string NomBundle = "montre_kingwatch";
        private const string NomPrefab = "KingWatch";
        private const string NomBoussole = "item_compass";
        private const string NomPaume = "LPalm";

        private const bool EcranPlugin = false;
        private const float PosX0 = -0.0817f, PosY0 = -0.0507f, PosZ0 = -0.0254f;
        private const float Taille0 = 0.085f;
        private const float TexteHeureTaille0 = 0.05f;
        private const float TexteRaidTaille0 = 0.03f;

        private const int NbPips = 4;
        private const float HauteurBip = 7276f;
        private const float DureePip = 0.060f;

        private const BindingFlags Tous = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static ManualLogSource Log;

        private ConfigEntry<bool> _secondes, _clignote, _indiglo;
        private ConfigEntry<Color> _couleurIndiglo;
        private ConfigEntry<bool> _bipActif;
        private ConfigEntry<int> _bipMinutes;
        private ConfigEntry<float> _bipVolume;

        // Montre
        private ConfigEntry<float> _taille;
        private ConfigEntry<float> _x, _y, _z;
        private ConfigEntry<float> _baseX, _baseY, _baseZ;
        private ConfigEntry<float> _retourner, _pivoter, _pencher;

        // Lueur
        private ConfigEntry<float> _lueur;
        private ConfigEntry<Color> _couleurFond, _couleurChiffres;

        // Textes
        private ConfigEntry<float> _hX, _hY, _hZ;
        private ConfigEntry<float> _rX, _rY, _rZ;

        private ConfigEntry<KeyboardShortcut> _toucheNoter;

        private bool _pret;
        private float _prochaineVerif, _prochaineMaj, _prochaineVerifBip;

        private Player _joueurConnu;
        private Transform _paume;
        private Transform _boussoleJeu;
        private GameObject _prefab;
        private GameObject _montre;
        private bool _bundleEssaye;

        // Afficheurs sept segments
        private Afficheur _heure, _raid;

        // Materiaux de lueur
        private Material _matFond, _matChiffres;
        private readonly List<Renderer> _rendusFond = new List<Renderer>();
        private readonly List<Renderer> _rendusContour = new List<Renderer>();

        private AudioSource _son;
        private AudioClip _clipBip;
        private bool _mixeurBranche;
        private bool _bipJoue;
        private double _dernierRestant = -1;

        private static ConfigDescription Plage(string texte, float min, float max)
        {
            return new ConfigDescription(texte, new AcceptableValueRange<float>(min, max));
        }

        private void Awake()
        {
            Log = Logger;
            try
            {
                _secondes = Config.Bind("1. General", "Afficher les secondes", false, "");
                _bipActif = Config.Bind("3. Alerte", "Bip de fin de raid", true, "La montre fait pip-pip-pip-pip une fois quand il reste peu de temps.");
                _bipMinutes = Config.Bind("3. Alerte", "Minutes avant la fin", 10, new ConfigDescription("Moment du bip.", new AcceptableValueRange<int>(1, 120)));
                _bipVolume = Config.Bind("3. Alerte", "Volume", 1f, Plage("", 0f, 1f));

                _taille = Config.Bind("4. Montre - Position", "Taille", Taille0, Plage("Taille de la montre (1 = taille du modele).", 0.01f, 3f));
                _x = Config.Bind("4. Montre - Position", "Position X", PosX0, Plage("Metres, par rapport a la paume gauche.", -0.3f, 0.3f));
                _y = Config.Bind("4. Montre - Position", "Position Y", PosY0, Plage("Metres, par rapport a la paume gauche.", -0.3f, 0.3f));
                _z = Config.Bind("4. Montre - Position", "Position Z", PosZ0, Plage("Metres, par rapport a la paume gauche.", -0.3f, 0.3f));

                _baseX = Config.Bind("4b. Montre - Rotation de depart", "Depart X", -103.1f, Plage("Sert a remettre le cadran face au joueur. Essaie 90, -90 ou 180.", -180f, 180f));
                _baseY = Config.Bind("4b. Montre - Rotation de depart", "Depart Y", 5.1f, Plage("Sert a remettre le cadran face au joueur. Essaie 90, -90 ou 180.", -180f, 180f));
                _baseZ = Config.Bind("4b. Montre - Rotation de depart", "Depart Z", -15.2f, Plage("Sert a remettre le cadran face au joueur. Essaie 90, -90 ou 180.", -180f, 180f));

                _retourner = Config.Bind("4c. Montre - Rotation fine", "Retourner (comme une page)", -160.6f, Plage("Tourne autour de l axe gauche-droite de la montre.", -180f, 180f));
                _pivoter = Config.Bind("4c. Montre - Rotation fine", "Pivoter (comme un cadran)", 13.5f, Plage("Tourne autour de l axe qui sort du cadran.", -180f, 180f));
                _pencher = Config.Bind("4c. Montre - Rotation fine", "Pencher (avant-arriere)", 46.5f, Plage("Tourne autour de l axe haut-bas de la montre.", -180f, 180f));

                // ATTENTION : pas d'apostrophe dans les noms de section ni de cle (interdit par BepInEx).
                _clignote = Config.Bind("1. General", "Deux-points clignotants", true, "Les deux-points de l heure et du raid clignotent une fois par seconde.");
                _lueur = Config.Bind("7. Lueur de lecran", "Intensite lueur (1.7)", 0.32f, Plage("0 = eteint, 1 = normal, 2 = tres lumineux. Multiplie la couleur du fond et des chiffres.", 0f, 3f));
                _couleurFond = Config.Bind("7. Lueur de lecran", "Couleur du fond (vert)", new Color(0.05f, 0.12f, 0.08f), "Fond des deux zones, vert sombre. Le jeu eclaircit les couleurs : pars bas.");
                _indiglo = Config.Bind("7. Lueur de lecran", "Mode Indiglo (fond eclaire)", false, "true = le fond prend la couleur Indiglo, comme le retroeclairage d une montre.");
                _couleurIndiglo = Config.Bind("7. Lueur de lecran", "Couleur Indiglo", new Color(0.20f, 0.65f, 0.50f), "Couleur du fond quand le mode Indiglo est actif.");
                _couleurChiffres = Config.Bind("7. Lueur de lecran", "Couleur des chiffres", new Color(0.35f, 1f, 0.55f), "Couleur des chiffres de l heure et du raid.");

                _hX = Config.Bind("5. Texte - Heure du jeu", "Decalage X", 0.0376f, Plage("Deplace les chiffres dans le modele. Essaie de petits pas (0.01).", -1f, 1f));
                _hY = Config.Bind("5. Texte - Heure du jeu", "Decalage Y", -0.0188f, Plage("Deplace les chiffres dans le modele. Essaie de petits pas (0.01).", -1f, 1f));
                _hZ = Config.Bind("5. Texte - Heure du jeu", "Decalage Z", -0.0282f, Plage("Deplace les chiffres dans le modele. Essaie de petits pas (0.01).", -1f, 1f));

                _rX = Config.Bind("6. Texte - Temps de raid", "Decalage X", -0.0094f, Plage("Deplace les chiffres dans le modele. Essaie de petits pas (0.01).", -1f, 1f));
                _rY = Config.Bind("6. Texte - Temps de raid", "Decalage Y", 0.0376f, Plage("Deplace les chiffres dans le modele. Essaie de petits pas (0.01).", -1f, 1f));
                _rZ = Config.Bind("6. Texte - Temps de raid", "Decalage Z", -0.1033f, Plage("Deplace les chiffres dans le modele. Essaie de petits pas (0.01).", -1f, 1f));


                _toucheNoter = Config.Bind("9. Test", "Touche pour noter les valeurs", new KeyboardShortcut(KeyCode.F10), "Ecrit dans le journal les valeurs de placement.");

                Camera.onPreCull += AvantRendu;
                Application.onBeforeRender += AvantRenduGeneral;
                _dossierPlugin = Path.GetDirectoryName(Info.Location);
                _pret = true;
                PreparerIcone();
                PreparerVue3D();
                Log.LogInfo("Montre Mousqueton 1.12.0 chargee (lueur + chiffres sept segments, bundle '" + NomBundle + "').");
            }
            catch (Exception e) { Log.LogError("Montre : erreur au demarrage : " + e); }
        }

        private void OnDestroy()
        {
            Camera.onPreCull -= AvantRendu;
            Application.onBeforeRender -= AvantRenduGeneral;
        }

        private static Player TrouverJoueur()
        {
            if (!Singleton<GameWorld>.Instantiated) return null;
            return Singleton<GameWorld>.Instance.MainPlayer;
        }

        // ---------- CHARGEMENT DU MODELE ----------

        private string CheminBundle()
        {
            string dossier = Path.GetDirectoryName(Info.Location);
            return Path.Combine(dossier, NomBundle);
        }

        private void ChargerBundle()
        {
            if (_bundleEssaye) return;
            _bundleEssaye = true;
            try
            {
                string chemin = CheminBundle();
                if (!File.Exists(chemin)) { Log.LogError("Montre : bundle introuvable : " + chemin); return; }
                AssetBundle ab = AssetBundle.LoadFromFile(chemin);
                if (ab == null) { Log.LogError("Montre : impossible de charger le bundle."); return; }
                _prefab = ab.LoadAsset<GameObject>(NomPrefab);
                if (_prefab == null)
                {
                    foreach (string n in ab.GetAllAssetNames()) Log.LogInfo("Montre : contenu du bundle : " + n);
                    Log.LogError("Montre : prefab '" + NomPrefab + "' introuvable dans le bundle.");
                    return;
                }
                Log.LogInfo("Montre : bundle charge, prefab '" + NomPrefab + "' pret.");
            }
            catch (Exception e) { Log.LogError("Montre : erreur chargement bundle : " + e); }
        }

        /// Materiau qui s eclaire tout seul ET se dessine par-dessus le corps de la montre
        /// (test de profondeur desactive), pour que le disque du corps ne cache plus ecran et chiffres.
        private static Material MatiereLueur(Color couleur, int file = 4000, bool toujoursDessus = true)
        {
            Shader s = Shader.Find("UI/Default");
            bool dessus = s != null;
            if (s == null) s = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Standard");
            if (s == null) return null;
            Material m = new Material(s);
            m.color = couleur;
            if (dessus && toujoursDessus)
            {
                m.SetInt("unity_GUIZTestMode", 8); // Always
                m.renderQueue = file;
            }
            return m;
        }

        /// Le corps de la montre garde son rendu normal (shader "Standard" si besoin).
        /// L'ecran (WatchScreen et les pieces Screen_*) recoit un materiau qui s'eclaire tout seul.
        private void AdapterMateriaux(GameObject go)
        {
            Shader standard = Shader.Find("Standard");
            _matFond = MatiereLueur(CouleurFond(), 4001, false);
            _rendusFond.Clear();
            _rendusContour.Clear();

            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                string nom = r.name;
                bool ecran = nom.StartsWith("Screen_") || nom == "WatchScreen" || nom == "Zone_Clock" || nom == "Zone_RaidTime";
                if (ecran && EcranPlugin)
                {
                    bool fond = nom.Contains("ZoneClock") || nom.Contains("ZoneRaid") || nom == "Zone_Clock" || nom == "Zone_RaidTime";
                    bool groove = nom.ToLowerInvariant().Contains("groove");
                    Material voulu = fond && !groove ? _matFond : null;
                    if (voulu != null)
                    {
                        Material[] mats = new Material[Math.Max(1, r.sharedMaterials.Length)];
                        for (int i = 0; i < mats.Length; i++) mats[i] = voulu;
                        r.sharedMaterials = mats;
                    }
                    if (fond && !groove) _rendusFond.Add(r); else _rendusContour.Add(r);
                }
                else
                {
                    Material[] ms = r.sharedMaterials;
                    bool change = false;
                    for (int i = 0; i < ms.Length; i++)
                    {
                        Material m = ms[i];
                        if (m == null) continue;
                        if (_matFond != null && m.name.Contains("Screen_Off_Zone"))
                        {
                            ms[i] = _matFond; change = true; _rendusFond.Add(r);
                            continue;
                        }
                        if (m.shader == null || !m.shader.isSupported)
                        {
                            if (standard != null) m.shader = standard;
                        }
                    }
                    if (change) r.sharedMaterials = ms;
                }
            }
            Log.LogInfo("Montre : lueur appliquee (" + _rendusFond.Count + " rendus de fond, " + _rendusContour.Count + " de contour/rainure).");
        }

        private void MettreAJourLueur()
        {
            float k = _lueur.Value;
            if (_matFond != null) _matFond.color = Intensite(CouleurFond(), k);
            if (_matChiffres != null) _matChiffres.color = Intensite(_couleurChiffres.Value, k);
        }

        private Color CouleurFond()
        {
            return _indiglo.Value ? _couleurIndiglo.Value : _couleurFond.Value;
        }

        private static Color Intensite(Color c, float k)
        {
            return new Color(Mathf.Clamp01(c.r * k), Mathf.Clamp01(c.g * k), Mathf.Clamp01(c.b * k), 1f);
        }

        private static void CouchePartout(GameObject go, int couche)
        {
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = couche;
        }

        /// Diagnostic : liste toutes les pieces de la montre dans le journal.
        private void DiagnostiquerMontre(GameObject go)
        {
            try
            {
                Log.LogInfo("===== DIAGNOSTIC MONTRE (1.12.0) =====");
                foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
                {
                    string mats = "";
                    foreach (Material m in r.sharedMaterials)
                        mats += (m == null ? "null" : m.name + "[" + (m.shader != null ? m.shader.name : "?") + "|" + ColorUtility.ToHtmlStringRGB(m.HasProperty("_Color") ? m.color : Color.magenta) + "]") + "; ";
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    string maillage = mf != null && mf.sharedMesh != null ? mf.sharedMesh.vertexCount + " sommets" : "-";
                    Log.LogInfo("  " + Chemin(r.transform) + " | actif " + r.gameObject.activeSelf + " | " + maillage + " | centre " + r.bounds.center.ToString("F3") + " | taille " + r.bounds.size.ToString("F3") + " | " + mats);
                }
                Log.LogInfo("===== FIN DIAGNOSTIC =====");
            }
            catch (Exception e) { Log.LogError("Montre : erreur diagnostic : " + e); }
        }

        private void ConstruireMontre()
        {
            if (_prefab == null || _paume == null) return;
            if (_montre != null) Destroy(_montre);

            _montre = Instantiate(_prefab);
            _montre.name = "MontreMousqueton_Main";
            _montre.transform.SetParent(_paume, false);
            AdapterMateriaux(_montre);
            CouchePartout(_montre, _paume.gameObject.layer);

            foreach (Collider c in _montre.GetComponentsInChildren<Collider>(true)) Destroy(c);

            _matChiffres = MatiereLueur(_couleurChiffres.Value, 4002);
            int couche = _paume.gameObject.layer;
            _heure = new Afficheur("Heure", _montre.transform, _matChiffres, 5, new[] { 0, 1, 3, 4 });
            _raid = new Afficheur("Raid", _montre.transform, _matChiffres, 8, new[] { 0, 1, 3, 4, 6, 7 });
            Log.LogInfo("Montre : segments trouves dans le modele : heure " + _heure.Trouves + "/35, raid " + _raid.Trouves + "/56.");

            DiagnostiquerMontre(_montre);
            _montre.SetActive(false);
            _prochaineMaj = 0f;
            Log.LogInfo("Montre : modele place sur la paume gauche, segments du modele.");
        }

        // ---------- BOUCLE PRINCIPALE ----------

        private void Update()
        {
            if (!_pret) return;

            Player joueur = TrouverJoueur();
            if (joueur == null) { Nettoyer(); return; }
            if (joueur != _joueurConnu) { Nettoyer(); _joueurConnu = joueur; }

            if (Time.time >= _prochaineVerif)
            {
                _prochaineVerif = Time.time + 0.3f;
                if (_paume == null) _paume = TrouverPaumeCorps(joueur);
                if (_paume != null)
                {
                    ChargerBundle();
                    if (_montre == null && _prefab != null) ConstruireMontre();
                }
            }

            bool porte = PorteLaMontre(joueur);

            _boussoleJeu = TrouverBoussoleJeu();
            bool geste = porte && _boussoleJeu != null;
            if (porte && _boussoleJeu == null && Time.time >= _prochainLogPaume && _paume != null)
            {
                _prochainLogPaume = Time.time + 5f;
                string noms = "";
                foreach (Transform t in _paume) noms += t.name + (t.gameObject.activeInHierarchy ? "" : "(inactif)") + " | ";
                Log.LogInfo("Paume : objet de geste non trouve. Enfants : " + noms);
            }

            if (_montre != null)
            {
                if (_montre.activeSelf != geste) _montre.SetActive(geste);
                if (geste) CacherBoussole();
            }

            PlacerMontre();

            if (_montre != null && geste && Time.time >= _prochaineMaj)
            {
                _prochaineMaj = Time.time + 0.5f;
                MettreAJourTextes();
            }

            if (porte && Time.time >= _prochaineVerifBip)
            {
                _prochaineVerifBip = Time.time + 0.5f;
                VerifierBip();
            }

            if (_toucheNoter.Value.IsDown()) NoterValeurs();
        }

        private float _prochainLogPaume;

        private Transform TrouverBoussoleJeu()
        {
            if (_paume == null) return null;
            foreach (Transform t in _paume)
            {
                if (!t.gameObject.activeInHierarchy) continue;
                if (t.name.StartsWith("MontreMousqueton_Main")) continue;
                string n = t.name.ToLowerInvariant();
                if (t.name.StartsWith(NomBoussole) || n.Contains("kingwatch") || n.Contains("montre_objet")) return t;
            }
            return null;
        }

        private void CacherBoussole()
        {
            if (_boussoleJeu == null) return;
            foreach (Renderer r in _boussoleJeu.GetComponentsInChildren<Renderer>(true))
                if (r.enabled) r.enabled = false;
        }

        private Quaternion RotationMontre()
        {
            Quaternion depart = Quaternion.Euler(_baseX.Value, _baseY.Value, _baseZ.Value);
            Quaternion retourner = Quaternion.AngleAxis(_retourner.Value, Vector3.right);
            Quaternion pencher = Quaternion.AngleAxis(_pencher.Value, Vector3.up);
            Quaternion pivoter = Quaternion.AngleAxis(_pivoter.Value, Vector3.forward);
            return depart * retourner * pencher * pivoter;
        }

        private static Vector3 Miroir(bool gd, bool hb)
        {
            return new Vector3(gd ? -1f : 1f, hb ? -1f : 1f, 1f);
        }

        private void PlacerMontre()
        {
            if (_montre == null || _paume == null) return;
            _montre.transform.localPosition = new Vector3(_x.Value, _y.Value, _z.Value);
            _montre.transform.localRotation = RotationMontre();
            _montre.transform.localScale = Vector3.one * _taille.Value;

            MettreAJourLueur();
            if (_heure != null) _heure.Decaler(new Vector3(_hX.Value, _hY.Value, _hZ.Value));
            if (_raid != null) _raid.Decaler(new Vector3(_rX.Value, _rY.Value, _rZ.Value));
            bool pointsOn = !_clignote.Value || (((int)(Time.unscaledTime * 2f)) & 1) == 0;
            if (_heure != null) _heure.Clignoter(pointsOn);
            if (_raid != null) _raid.Clignoter(pointsOn);
        }

        private void LateUpdate() { if (_pret) PlacerMontre(); }
        private void AvantRenduGeneral() { if (_pret) PlacerMontre(); }
        private void AvantRendu(Camera cam) { if (_pret && cam != null) PlacerMontre(); }

        // ---------- PAUME ----------

        private static Transform TrouverPaumeCorps(Player joueur)
        {
            Transform racine = RacineJoueur(joueur);
            if (racine == null) return null;
            foreach (Transform t in racine.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.EndsWith(NomPaume)) continue;
                string c = Chemin(t);
                if (c.IndexOf("Root_Joint", StringComparison.Ordinal) >= 0 && c.IndexOf("weapon", StringComparison.OrdinalIgnoreCase) < 0) return t;
            }
            return null;
        }

        private static Transform RacineJoueur(Player joueur)
        {
            object t = Membre(joueur, "PlayerTransform");
            Transform tr = t as Transform;
            if (tr != null) return tr;
            Component c = joueur as Component;
            return c != null ? c.gameObject.transform : null;
        }

        private static string Chemin(Transform t)
        {
            string c = t.name;
            for (Transform p = t.parent; p != null; p = p.parent) c = p.name + "/" + c;
            return c;
        }

        // ---------- NOTER ----------

        private void NoterValeurs()
        {
            Log.LogInfo("===== VALEURS DE PLACEMENT (1.12.0) =====");
            Log.LogInfo("Montre  taille : " + _taille.Value.ToString("F3"));
            Log.LogInfo("Montre  position X/Y/Z : " + _x.Value.ToString("F4") + " / " + _y.Value.ToString("F4") + " / " + _z.Value.ToString("F4"));
            Log.LogInfo("Montre  depart X/Y/Z : " + _baseX.Value.ToString("F1") + " / " + _baseY.Value.ToString("F1") + " / " + _baseZ.Value.ToString("F1"));
            Log.LogInfo("Montre  retourner/pencher/pivoter : " + _retourner.Value.ToString("F1") + " / " + _pencher.Value.ToString("F1") + " / " + _pivoter.Value.ToString("F1"));
            if (_heure != null && _raid != null) Log.LogInfo("Decalages appliques : heure " + _heure.Changements + " fois, raid " + _raid.Changements + " fois (1.12.0).");
            Log.LogInfo("Couleur fond R/G/B : " + _couleurFond.Value.r.ToString("F3") + " / " + _couleurFond.Value.g.ToString("F3") + " / " + _couleurFond.Value.b.ToString("F3") + " | Indiglo : " + _indiglo.Value);
            Log.LogInfo("Couleur chiffres R/G/B : " + _couleurChiffres.Value.r.ToString("F3") + " / " + _couleurChiffres.Value.g.ToString("F3") + " / " + _couleurChiffres.Value.b.ToString("F3"));
            Log.LogInfo("Lueur   intensite : " + _lueur.Value.ToString("F2"));
            Log.LogInfo("Heure   decalage X/Y/Z : " + _hX.Value.ToString("F4") + " / " + _hY.Value.ToString("F4") + " / " + _hZ.Value.ToString("F4"));
            Log.LogInfo("Raid    decalage X/Y/Z : " + _rX.Value.ToString("F4") + " / " + _rY.Value.ToString("F4") + " / " + _rZ.Value.ToString("F4"));
        }

        // ---------- AFFICHEUR SEPT SEGMENTS ----------

        /// Un afficheur est une rangee de chiffres dessines avec de petits cubes (les segments).
        /// Dans l'espace local : un chiffre fait 1 de haut et ~0,6 de large ; Racine.localScale fixe la taille reelle.
        /// Afficheur sept segments deja present dans le modele (pieces Heure_Case0_SegA ... Raid_Case7_SegG).
        /// Le plugin ne cree rien : il allume ou eteint les pieces, donc tout bouge avec la montre.
        private class Afficheur
        {
            private readonly GameObject[][] _segs; // [case][7] a b c d e f g
            private readonly int[] _casesChiffres; // index des cases qui recoivent un chiffre
            private string _texteAffiche = null;
            public int Trouves, Changements;
            private readonly List<GameObject> _points = new List<GameObject>();
            private bool _pointsEtat = true;

            public void Clignoter(bool allume)
            {
                if (allume == _pointsEtat) return;
                _pointsEtat = allume;
                foreach (GameObject p in _points) if (p != null) p.SetActive(allume);
            }
            private readonly List<Transform> _ts = new List<Transform>();
            private readonly List<Vector3> _orig = new List<Vector3>();
            private Vector3 _dec = new Vector3(float.NaN, 0, 0);

            public void Decaler(Vector3 d)
            {
                if (d != _dec) { _dec = d; Changements++; }
                for (int i = 0; i < _ts.Count; i++) _ts[i].localPosition = _orig[i] + d;
            }

            private static readonly bool[][] Chiffres =
            {
                new[]{true,true,true,true,true,true,false},
                new[]{false,true,true,false,false,false,false},
                new[]{true,true,false,true,true,false,true},
                new[]{true,true,true,true,false,false,true},
                new[]{false,true,true,false,false,true,true},
                new[]{true,false,true,true,false,true,true},
                new[]{true,false,true,true,true,true,true},
                new[]{true,true,true,false,false,false,false},
                new[]{true,true,true,true,true,true,true},
                new[]{true,true,true,true,false,true,true}
            };

            public Afficheur(string prefixe, Transform modele, Material mat, int nbCases, int[] casesChiffres)
            {
                _casesChiffres = casesChiffres;
                _segs = new GameObject[nbCases][];
                var parNom = new Dictionary<string, GameObject>();
                foreach (Transform t in modele.GetComponentsInChildren<Transform>(true))
                    if (t.name.StartsWith(prefixe + "_Case")) parNom[t.name] = t.gameObject;
                const string lettres = "ABCDEFG";
                for (int c = 0; c < nbCases; c++)
                {
                    _segs[c] = new GameObject[7];
                    for (int k = 0; k < 7; k++)
                    {
                        GameObject go;
                        if (parNom.TryGetValue(prefixe + "_Case" + c + "_Seg" + lettres[k], out go))
                        {
                            _segs[c][k] = go;
                            Trouves++;
                            _ts.Add(go.transform); _orig.Add(go.transform.localPosition);
                            Renderer r = go.GetComponent<Renderer>();
                            if (r != null && mat != null) r.sharedMaterial = mat;
                        }
                    }
                }
                foreach (Transform t in modele.GetComponentsInChildren<Transform>(true))
                {
                    if (!t.name.StartsWith(prefixe + "_Colon")) continue;
                    _points.Add(t.gameObject);
                    _ts.Add(t); _orig.Add(t.localPosition);
                    Renderer r = t.GetComponent<Renderer>();
                    if (r != null && mat != null) r.sharedMaterial = mat;
                    t.gameObject.SetActive(true);
                }
                Vider();
            }

            private void Vider()
            {
                foreach (var cel in _segs)
                    foreach (var g in cel)
                        if (g != null) g.SetActive(false);
            }

            public void Afficher(string texte)
            {
                if (texte == _texteAffiche) return;
                _texteAffiche = texte;
                Vider();
                int n = 0;
                foreach (char ch in texte)
                {
                    if (ch < '0' || ch > '9') continue;
                    if (n >= _casesChiffres.Length) break;
                    GameObject[] cel = _segs[_casesChiffres[n]];
                    bool[] on = Chiffres[ch - '0'];
                    for (int k = 0; k < 7; k++) if (cel[k] != null) cel[k].SetActive(on[k]);
                    n++;
                }
            }
        }


        // ---------- VUE 3D (inspection, icones, hideout, loot) ----------
        // La main garde le modele d origine de la boussole (isAnimated = vrai : on ne touche a rien).
        // Pour tout le reste (isAnimated = faux), si l objet est NOTRE montre, on fournit montre_objet.
        // La vraie boussole n est jamais touchee. En cas d echec, le jeu garde le modele d origine.

        private static GameObject _prefabObjet;
        private static bool _objetEssaye, _objetLogue;
        private static Type _typePoolObject;

        private void PreparerVue3D()
        {
            try
            {
                Type usine = null;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!a.GetName().Name.StartsWith("Assembly-CSharp")) continue;
                    usine = a.GetType("EFT.ObjectsFactory");
                    if (usine == null) try { foreach (Type x in a.GetTypes()) if (x.Name == "ObjectsFactory") { usine = x; break; } } catch { }
                    try { foreach (Type x in a.GetTypes()) if (x.Name == "AssetPoolObject") { _typePoolObject = x; break; } } catch { }
                    if (usine != null) break;
                }
                if (usine == null) { Log.LogWarning("Vue 3D : ObjectsFactory introuvable, inspection d origine conservee."); return; }

                MethodBase cible = null;
                foreach (MethodInfo m in usine.GetMethods(Tous))
                    if (m.Name == "CreateItemAsync" && m.GetParameters().Length == 6) { cible = m; break; }
                if (cible == null) { Log.LogWarning("Vue 3D : CreateItemAsync introuvable, inspection d origine conservee."); return; }

                Type hT = null, hmT = null;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (hT == null) hT = a.GetType("HarmonyLib.Harmony");
                    if (hmT == null) hmT = a.GetType("HarmonyLib.HarmonyMethod");
                }
                if (hT == null || hmT == null) { Log.LogWarning("Vue 3D : Harmony introuvable."); return; }

                object harmony = Activator.CreateInstance(hT, "com.yann.montretactique.vue3d");
                MethodInfo patch = hT.GetMethods().Where(m => m.Name == "Patch" && m.GetParameters().Length >= 2)
                                     .OrderBy(m => m.GetParameters().Length).First();
                object pre = Activator.CreateInstance(hmT, typeof(MontreTactiquePlugin).GetMethod("PrefixCreerObjet", BindingFlags.NonPublic | BindingFlags.Static));
                object[] args = new object[patch.GetParameters().Length]; args[0] = cible; args[1] = pre;
                patch.Invoke(harmony, args);
                Log.LogInfo("Vue 3D : patch pose sur " + usine.FullName + ".CreateItemAsync (montre_objet fourni hors main).");
            }
            catch (Exception e) { Log.LogWarning("Vue 3D : echec, inspection d origine conservee. " + e.Message); }
        }

        private static string _dossierPlugin;

        private static GameObject ChargerObjet()
        {
            if (_prefabObjet != null) return _prefabObjet;
            if (_objetEssaye) return null;
            _objetEssaye = true;
            try
            {
                string chemin = null;
                foreach (string nom in new[] { "montre_objet.bundle", "montre_objet" })
                {
                    string c = Path.Combine(_dossierPlugin ?? "", nom);
                    if (File.Exists(c)) { chemin = c; break; }
                }
                if (chemin == null) { Log.LogWarning("Vue 3D : montre_objet(.bundle) absent du dossier du plugin, inspection d origine conservee."); return null; }
                AssetBundle ab = AssetBundle.LoadFromFile(chemin);
                if (ab == null) { Log.LogWarning("Vue 3D : impossible de charger " + chemin); return null; }
                GameObject choisi = null;
                foreach (GameObject g in ab.LoadAllAssets<GameObject>())
                {
                    if (g == null) continue;
                    if (choisi == null) choisi = g;
                    if (g.name.ToLowerInvariant().Contains("objet")) { choisi = g; break; }
                }
                if (choisi == null) { Log.LogWarning("Vue 3D : aucun prefab dans montre_objet."); return null; }
                _prefabObjet = choisi;
                Log.LogInfo("Vue 3D : montre_objet charge, prefab '" + choisi.name + "'.");
                return _prefabObjet;
            }
            catch (Exception e) { Log.LogWarning("Vue 3D : erreur chargement montre_objet : " + e.Message); return null; }
        }

        private static GameObject CreerModeleObjet()
        {
            GameObject prefab = ChargerObjet();
            if (prefab == null) return null;
            GameObject go = UnityEngine.Object.Instantiate(prefab);
            go.name = prefab.name + "(Clone)";
            Shader standard = Shader.Find("Standard");
            foreach (Collider c in go.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.Destroy(c);
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
                foreach (Material m in r.sharedMaterials)
                    if (m != null && standard != null && (m.shader == null || !m.shader.isSupported)) m.shader = standard;
            try { if (_typePoolObject != null && go.GetComponent(_typePoolObject) == null) go.AddComponent(_typePoolObject); } catch { }
            go.SetActive(false);
            if (!_objetLogue) { _objetLogue = true; Log.LogInfo("Vue 3D : modele de la montre fourni au jeu (hors main)."); }
            return go;
        }

        // Appele par Harmony (prefix) : les noms item, isAnimated et __result doivent rester tels quels.
        private static bool PrefixCreerObjet(Item item, bool isAnimated, ref Task<GameObject> __result)
        {
            try
            {
                if (isAnimated || !EstMontre(item)) return true;
                GameObject modele = CreerModeleObjet();
                if (modele == null) return true;
                __result = Task.FromResult(modele);
                return false;
            }
            catch (Exception e)
            {
                Log.LogWarning("Vue 3D : erreur, modele d origine utilise. " + e.Message);
                return true;
            }
        }

        // ---------- ICONE D INVENTAIRE ----------
        // Le jeu fabrique l icone d un objet a partir de son modele (ici la boussole clonee).
        // On remplace donc l icone de la montre par une image PNG : montre_icone.png, a cote de la DLL.

        private static Sprite _iconeSprite;
        private static Texture2D _iconeTex;
        private static Rect _iconeCadre;
        private static readonly Dictionary<string, Sprite> _iconesCache = new Dictionary<string, Sprite>();
        private static ConstructorInfo _ctorIcone;

        private void PreparerIcone()
        {
            try
            {
                string png = Path.Combine(Path.GetDirectoryName(Info.Location), "montre_icone.png");
                if (!File.Exists(png)) { Log.LogInfo("Icone : montre_icone.png absent, icone d origine conservee."); return; }

                Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                MethodInfo charger = null;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = a.GetType("UnityEngine.ImageConversion");
                    if (t != null) { charger = t.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) }); if (charger != null) break; }
                }
                if (charger == null) { Log.LogWarning("Icone : ImageConversion introuvable."); return; }
                charger.Invoke(null, new object[] { tex, File.ReadAllBytes(png) });
                tex.filterMode = FilterMode.Bilinear;
                _iconeTex = tex;
                CalculerCadre(tex);
                _iconeSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                _ctorIcone = typeof(ItemIcon).GetConstructor(new[] { typeof(int) });

                Type hT = null, hmT = null;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (hT == null) hT = a.GetType("HarmonyLib.Harmony");
                    if (hmT == null) hmT = a.GetType("HarmonyLib.HarmonyMethod");
                }
                if (hT == null || hmT == null) { Log.LogWarning("Icone : Harmony introuvable."); return; }

                object harmony = Activator.CreateInstance(hT, "com.yann.montretactique.icones");
                MethodInfo patch = hT.GetMethods().Where(m => m.Name == "Patch" && m.GetParameters().Length >= 2)
                                     .OrderBy(m => m.GetParameters().Length).First();
                int n = patch.GetParameters().Length;

                Type fabrique = typeof(EFT.UI.DragAndDrop.ItemViewFactory);
                MethodBase icone = fabrique.GetMethod("LoadItemIcon", BindingFlags.Public | BindingFlags.Static);
                MethodBase sprite = fabrique.GetMethod("GetItemSpriteAsync", BindingFlags.Public | BindingFlags.Static);

                if (icone != null)
                {
                    object pre = Activator.CreateInstance(hmT, typeof(MontreTactiquePlugin).GetMethod("PrefixIcone", BindingFlags.NonPublic | BindingFlags.Static));
                    object[] args = new object[n]; args[0] = icone; args[1] = pre;
                    patch.Invoke(harmony, args);
                }
                if (sprite != null)
                {
                    object pre = Activator.CreateInstance(hmT, typeof(MontreTactiquePlugin).GetMethod("PrefixSprite", BindingFlags.NonPublic | BindingFlags.Static));
                    object[] args = new object[n]; args[0] = sprite; args[1] = pre;
                    patch.Invoke(harmony, args);
                }
                Log.LogInfo("Icone : montre_icone.png charge (" + tex.width + "x" + tex.height + "), patchs LoadItemIcon=" + (icone != null) + " GetItemSpriteAsync=" + (sprite != null) + ".");
            }
            catch (Exception e)
            {
                Log.LogWarning("Icone : echec, icone d origine conservee. " + e.Message);
            }
        }

        /// Rectangle (en pixels) qui entoure la partie non transparente de l image.
        private static void CalculerCadre(Texture2D tex)
        {
            Color32[] px = tex.GetPixels32();
            int w = tex.width, h = tex.height, x0 = w, y0 = h, x1 = -1, y1 = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (px[y * w + x].a > 16)
                    {
                        if (x < x0) x0 = x; if (x > x1) x1 = x;
                        if (y < y0) y0 = y; if (y > y1) y1 = y;
                    }
            if (x1 < x0 || y1 < y0) { x0 = 0; y0 = 0; x1 = w - 1; y1 = h - 1; }
            _iconeCadre = new Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
        }

        /// Fabrique une image exactement a la taille de l objet dans l inventaire (63 pixels par case).
        private static Sprite SpritePourTaille(int largeurCases, int hauteurCases, int echelle)
        {
            if (_iconeTex == null) return _iconeSprite;
            if (largeurCases < 1) largeurCases = 1;
            if (hauteurCases < 1) hauteurCases = 1;
            if (echelle < 1) echelle = 1;
            string cle = largeurCases + "x" + hauteurCases + "@" + echelle;
            Sprite deja;
            if (_iconesCache.TryGetValue(cle, out deja) && deja != null) return deja;

            int W = 63 * largeurCases * echelle, H = 63 * hauteurCases * echelle;
            float marge = 0.04f;
            float dispoW = W * (1f - 2f * marge), dispoH = H * (1f - 2f * marge);
            float k = Mathf.Min(dispoW / _iconeCadre.width, dispoH / _iconeCadre.height);
            float dw = _iconeCadre.width * k, dh = _iconeCadre.height * k;
            float ox = (W - dw) * 0.5f, oy = (H - dh) * 0.5f;

            Color[] sortie = new Color[W * H];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    float u = (x + 0.5f - ox) / dw, v = (y + 0.5f - oy) / dh;
                    if (u < 0f || u > 1f || v < 0f || v > 1f) { sortie[y * W + x] = new Color(0, 0, 0, 0); continue; }
                    float tu = (_iconeCadre.x + u * _iconeCadre.width) / _iconeTex.width;
                    float tv = (_iconeCadre.y + v * _iconeCadre.height) / _iconeTex.height;
                    sortie[y * W + x] = _iconeTex.GetPixelBilinear(tu, tv);
                }
            }
            Texture2D t = new Texture2D(W, H, TextureFormat.RGBA32, false);
            t.SetPixels(sortie);
            t.filterMode = FilterMode.Bilinear;
            t.Apply(false, false);
            Sprite sp = Sprite.Create(t, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f), 100f);
            _iconesCache[cle] = sp;
            return sp;
        }

        private static void TailleEnCases(Item item, out int largeur, out int hauteur)
        {
            largeur = 1; hauteur = 2;
            try
            {
                object cs = item.CalculateCellSize();
                if (cs == null) return;
                Type t = cs.GetType();
                object x = null, y = null;
                FieldInfo fx = t.GetField("X"), fy = t.GetField("Y");
                if (fx != null && fy != null) { x = fx.GetValue(cs); y = fy.GetValue(cs); }
                else
                {
                    PropertyInfo px = t.GetProperty("X"), py = t.GetProperty("Y");
                    if (px != null && py != null) { x = px.GetValue(cs, null); y = py.GetValue(cs, null); }
                }
                if (x != null && y != null) { largeur = Convert.ToInt32(x); hauteur = Convert.ToInt32(y); }
            }
            catch { }
        }

        private static bool EstMontre(Item item)
        {
            try { return item != null && item.TemplateId.ToString() == MontreTpl; }
            catch { return false; }
        }

        // Appeles par Harmony (prefix) : le nom des parametres doit rester item et __result.
        private static bool PrefixIcone(Item item, int scaleFactor, ref ItemIcon __result)
        {
            if (_iconeSprite == null || _ctorIcone == null || !EstMontre(item)) return true;
            int l, h; TailleEnCases(item, out l, out h);
            ItemIcon icone = (ItemIcon)_ctorIcone.Invoke(new object[] { 0x4D4F4E54 });
            icone.Sprite = SpritePourTaille(l, h, scaleFactor);
            __result = icone;
            return false;
        }

        private static bool PrefixSprite(Item item, int scaleFactor, ref Task<Sprite> __result)
        {
            if (_iconeSprite == null || !EstMontre(item)) return true;
            int l, h; TailleEnCases(item, out l, out h);
            __result = Task.FromResult(SpritePourTaille(l, h, scaleFactor));
            return false;
        }

        // ---------- BIP DE FIN DE RAID ----------

        private void PreparerSource()
        {
            if (_son != null) return;
            GameObject go = new GameObject("MontreTactique_Bip");
            go.hideFlags = HideFlags.DontSave;
            DontDestroyOnLoad(go);
            _son = go.AddComponent<AudioSource>();
            _son.playOnAwake = false;
            _son.loop = false;
            _son.bypassEffects = true;
            _son.bypassListenerEffects = true;
            _son.bypassReverbZones = true;
            _son.priority = 0;
            _son.dopplerLevel = 0f;
            _son.rolloffMode = AudioRolloffMode.Linear;
            _son.minDistance = 0.3f;
            _son.maxDistance = 8f;
            _mixeurBranche = false;
        }

        private void ConstruireClip()
        {
            const int freq = 44100;
            float cycle = DureePip * 2f;
            int total = Mathf.CeilToInt(freq * cycle * NbPips);
            float[] data = new float[total];
            for (int i = 0; i < total; i++)
            {
                float t = (float)i / freq;
                float dansCycle = t % cycle;
                if (dansCycle >= DureePip) continue;
                float enveloppe = Mathf.Clamp01(dansCycle / 0.003f) * Mathf.Clamp01((DureePip - dansCycle) / 0.003f);
                float s = Mathf.Sin(2f * Mathf.PI * HauteurBip * t);
                float buzzer = Mathf.Clamp(s * 1.6f, -1f, 1f);
                data[i] = buzzer * 0.5f * enveloppe;
            }
            if (_clipBip != null) Destroy(_clipBip);
            _clipBip = AudioClip.Create("MontreBip", total, 1, freq, false);
            _clipBip.SetData(data, 0);
            _clipBip.hideFlags = HideFlags.DontUnloadUnusedAsset | HideFlags.DontSave;
        }

        private void BrancherMixeur()
        {
            if (_mixeurBranche || _son == null) return;
            try
            {
                Type typeAudio = null;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!a.GetName().Name.StartsWith("Assembly-CSharp")) continue;
                    typeAudio = a.GetType("BetterAudio") ?? a.GetType("EFT.BetterAudio");
                    if (typeAudio == null) try { foreach (Type x in a.GetTypes()) if (x.Name == "BetterAudio") { typeAudio = x; break; } } catch { }
                    if (typeAudio != null) break;
                }
                if (typeAudio == null) return;

                Type singleton = typeof(Singleton<>).MakeGenericType(typeAudio);
                PropertyInfo inst = singleton.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                PropertyInfo existe = singleton.GetProperty("Instantiated", BindingFlags.Public | BindingFlags.Static);
                if (existe != null && !(bool)existe.GetValue(null, null)) return;
                object audio = inst?.GetValue(null, null);
                if (audio == null) return;

                AudioMixerGroup groupe = null;
                string[] preferes = { "VeryStandartMixerGroup", "StandartMixerGroup", "SimpleMixerGroup" };
                foreach (string nom in preferes)
                {
                    groupe = Membre(audio, nom) as AudioMixerGroup;
                    if (groupe != null) break;
                }
                if (groupe == null)
                    foreach (FieldInfo f in typeAudio.GetFields(Tous))
                        if (typeof(AudioMixerGroup).IsAssignableFrom(f.FieldType)) { groupe = f.GetValue(audio) as AudioMixerGroup; if (groupe != null) break; }

                if (groupe != null)
                {
                    _son.outputAudioMixerGroup = groupe;
                    _mixeurBranche = true;
                }
            }
            catch (Exception e) { Log.LogError("Montre : erreur branchement audio : " + e); }
        }

        private void JouerBip()
        {
            try
            {
                PreparerSource();
                ConstruireClip();
                if (_son == null || _clipBip == null) { Log.LogWarning("Montre : impossible de creer le son."); return; }
                BrancherMixeur();

                if (_paume != null)
                {
                    _son.transform.position = _paume.position;
                    _son.spatialBlend = 1f;
                }
                else
                {
                    Camera cam = Camera.main;
                    if (cam != null) _son.transform.position = cam.transform.position;
                    _son.spatialBlend = 0f;
                }

                _son.volume = 1f;
                _son.PlayOneShot(_clipBip, _bipVolume.Value);
                Log.LogInfo("Montre : bip de fin de raid joue.");
            }
            catch (Exception e) { Log.LogError("Montre : erreur bip : " + e); }
        }

        private void VerifierBip()
        {
            double restant;
            try { restant = TempsRestant().TotalSeconds; }
            catch { return; }
            double seuil = _bipMinutes.Value * 60.0;
            if (_bipActif.Value && !_bipJoue && _dernierRestant > seuil && restant <= seuil && restant > 0)
            {
                _bipJoue = true;
                JouerBip();
            }
            _dernierRestant = restant;
        }

        // ---------- NETTOYAGE ----------

        private void Nettoyer()
        {
            if (_montre != null) Destroy(_montre);
            _montre = null; _paume = null; _boussoleJeu = null;
            _heure = null; _raid = null;
            _bipJoue = false; _dernierRestant = -1;
            _mixeurBranche = false;
            if (_son != null) _son.outputAudioMixerGroup = null;
            _joueurConnu = null;
        }

        // ---------- INVENTAIRE ET HEURE ----------

        private static bool PorteLaMontre(Player joueur)
        {
            try
            {
                Item poches = joueur.Inventory?.Equipment?.GetSlot(EquipmentSlot.Pockets)?.ContainedItem;
                if (!(poches is CompoundItem conteneur)) return false;
                foreach (Slot slot in conteneur.AllSlots)
                {
                    if (slot == null || slot.ID == null || !slot.ID.StartsWith("SpecialSlot")) continue;
                    Item objet = slot.ContainedItem;
                    if (objet != null && objet.TemplateId.ToString() == MontreTpl) return true;
                }
            }
            catch { }
            return false;
        }

        private void MettreAJourTextes()
        {
            string heure, raid;
            try { heure = Singleton<GameWorld>.Instance.GameDateTime.Calculate().ToString("HH:mm"); }
            catch { heure = "--:--"; }
            try
            {
                TimeSpan r = TempsRestant();
                if (r.TotalSeconds < 0) r = TimeSpan.Zero;
                int h = (int)r.TotalHours;
                raid = $"{h:00}:{r.Minutes:00}:{r.Seconds:00}";
            }
            catch { raid = "--:--"; }
            if (_heure != null) { _heure.Afficher(heure); }
            if (_raid != null) { _raid.Afficher(raid); }
        }

        private static object Membre(object obj, string nom)
        {
            if (obj == null) return null;
            for (Type t = obj.GetType(); t != null; t = t.BaseType)
            {
                var p = t.GetProperty(nom, Tous | BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0) { try { return p.GetValue(obj, null); } catch { } }
                var c = t.GetField(nom, Tous | BindingFlags.DeclaredOnly);
                if (c != null) return c.GetValue(obj);
            }
            return null;
        }

        private static TimeSpan TempsRestant()
        {
            object minuteur = Singleton<AbstractGame>.Instance.GameTimer;
            TimeSpan? session = Membre(minuteur, "SessionTime") as TimeSpan?;
            TimeSpan? passe = Membre(minuteur, "PastTime") as TimeSpan?;
            if (session.HasValue && passe.HasValue) return session.Value - passe.Value;
            if (Membre(minuteur, "EscapeDateTime") is DateTime d) return d - DateTime.UtcNow;
            throw new Exception("Minuteur introuvable");
        }
    }
}
