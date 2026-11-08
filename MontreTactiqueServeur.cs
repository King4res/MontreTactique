using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Modding.Custom;
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace MontreTactique;

/// <summary>
/// Fiche d'identité du mod, lue par le serveur SPT au démarrage.
/// </summary>
public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.yann.montretactique";
    public string Name { get; init; } = "King Watch";
    public string Author { get; init; } = "Yann";
    public List<string>? Contributors { get; init; } = null;
    public Version Version { get; init; } = new("1.7.0");
    public Range SptVersion { get; init; } = new("~4.1.0");
    public bool HasPrepatcher { get; init; } = false;
    public List<string>? Incompatibilities { get; init; } = null;
    public Dictionary<string, Range>? ModDependencies { get; init; } = null;
    public string? Url { get; init; } = null;
    public string License { get; init; } = "MIT";
}

/// <summary>
/// Cree l'objet "Montre mousqueton tactique" dans la base de donnees du jeu,
/// l'autorise dans les slots speciaux et le met en vente chez Therapeute.
/// C'est un clone de la boussole : la boussole d'origine reste intacte et utilisable.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 1)]
public class MontreTactiqueServeur(
    ISptLogger<MontreTactiqueServeur> logger,
    CustomItemService customItemService,
    TemplateTable templateTable,
    TradersTable tradersTable
) : IOnLoad
{
    // ID unique de notre montre. Le plugin client utilise EXACTEMENT le meme.
    public const string MontreTpl = "6907a1f0c2b5a9d4e3f10001";

    // ID de l'offre chez le marchand (doit aussi etre unique)
    private const string OffreId = "6907a1f0c2b5a9d4e3f10002";

    // Objets du jeu de base qu'on utilise
    private const string BoussoleTpl = "5f4f9eb969cdc30ff33f09db";   // Boussole EYE MK.2
    private const string TherapeuteId = "54cb57776803fa99248b456e";  // Marchande Therapeute
    private const string RoublesTpl = "5449016a4bdc2d6f028b456f";    // Roubles

    private const int PrixRoubles = 1500000;     // prix chez Therapeute, manuel et marche aux puces
    private const int PrixEuros = 500;            // prix chez Ragman
    private const int NiveauLoyaute = 3;          // niveau de loyaute requis (Therapeute et Ragman)

    private const string RagmanId = "5ac3b934156ae10c4430e83c";   // Marchand Ragman
    private const string EurosTpl = "569668774bdc2da2298b4568";   // Euros
    private const string OffreRagmanId = "6907a1f0c2b5a9d4e3f10003";

    // Vue 3D : true = l objet utilise notre bundle montre_objet (dossier bundles + bundles.json du mod).
    // En cas de probleme, mets false, recompile, et la montre redevient comme avant (boussole en 3D).
    private const bool Vue3D = true;

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        if (!CreerMontre())
        {
            return Task.CompletedTask;
        }

        AutoriserDansSlotsSpeciaux();
        AjouterChezMarchand(TherapeuteId, "Therapeute", OffreId, RoublesTpl, PrixRoubles);
        AjouterChezMarchand(RagmanId, "Ragman", OffreRagmanId, EurosTpl, PrixEuros);

        logger.Success("[Montre Mousqueton Tactique] Montre ajoutee au jeu.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// On clone la boussole : la montre herite de son animation de bras
    /// et du fait qu'elle se range dans un slot special.
    /// </summary>
    private bool CreerMontre()
    {
        if (!templateTable.Items.TryGetValue(BoussoleTpl, out var boussole))
        {
            logger.Error("[Montre Mousqueton Tactique] Boussole introuvable dans la base, impossible de creer la montre.");
            return false;
        }

        // Meme categorie de manuel (handbook) que la boussole
        MongoId boussoleId = BoussoleTpl;
        var parentManuel = templateTable.Handbook.Items
            .FirstOrDefault(h => h.Id.Equals(boussoleId))?.ParentId;

        var details = new NewItemFromCloneDetails
        {
            ItemTplToClone = BoussoleTpl,
            NewId = MontreTpl,
            NewItemName = "montre_mousqueton_tactique",
            ParentId = boussole.Parent,
            HandbookParentId = parentManuel?.ToString(),
            AddToHandbook = parentManuel != null,
            HandbookPriceRoubles = PrixRoubles,
            FleaPriceRoubles = PrixRoubles,
            OverrideProperties = new TemplateItemProperties
            {
                ExaminedByDefault = true,
                Width = 1,
                Height = 2,
                Prefab = Vue3D ? new Prefab { Path = "montre_objet.bundle", Rcid = "" } : null
            },
            Locales = new Dictionary<string, LocaleDetails>
            {
                ["en"] = new LocaleDetails
                {
                    Name = "King Watch",
                    ShortName = "Watch",
                    Description = "Carabiner watch used to check the time in raid."
                },
                ["fr"] = new LocaleDetails
                {
                    Name = "King Watch",
                    ShortName = "Montre",
                    Description = "Montre mousqueton qui sert à voir l\'heure en raid."
                }
            }
        };

        var resultat = customItemService.CreateItemFromClone(details);
        if (!resultat.Success)
        {
            logger.Error($"[Montre Mousqueton Tactique] Echec de creation de la montre : {string.Join(", ", resultat.Errors)}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Partout ou la boussole est acceptee dans un slot special (poches, etc.),
    /// on accepte aussi la montre.
    /// </summary>
    private void AutoriserDansSlotsSpeciaux()
    {
        MongoId boussoleId = BoussoleTpl;
        MongoId montreId = MontreTpl;
        var compteur = 0;

        foreach (var item in templateTable.Items.Values)
        {
            var slots = item.Properties?.Slots;
            if (slots == null)
            {
                continue;
            }

            foreach (var slot in slots)
            {
                if (slot.Name == null || !slot.Name.StartsWith("SpecialSlot"))
                {
                    continue;
                }

                var filtres = slot.Properties?.Filters;
                if (filtres == null)
                {
                    continue;
                }

                foreach (var filtre in filtres)
                {
                    if (filtre.Filter != null && filtre.Filter.Contains(boussoleId) && filtre.Filter.Add(montreId))
                    {
                        compteur++;
                    }
                }
            }
        }

        logger.Info($"[Montre Mousqueton Tactique] Montre autorisee dans {compteur} slot(s) special(aux).");
    }

    /// <summary>
    /// Met la montre en vente chez un marchand, stock illimite.
    /// </summary>
    private void AjouterChezMarchand(string marchandId, string nomMarchand, string offre, string monnaieTpl, int prix)
    {
        var assort = tradersTable.GetTrader(marchandId)?.Assort;
        if (assort?.Items == null || assort.BarterScheme == null || assort.LoyalLevelItems == null)
        {
            logger.Warning($"[Montre Mousqueton Tactique] {nomMarchand} introuvable, la montre ne sera pas vendue chez lui.");
            return;
        }

        MongoId offreId = offre;

        assort.Items.Add(new Item
        {
            Id = offreId,
            Template = MontreTpl,
            ParentId = "hideout",
            SlotId = "hideout",
            Upd = new Upd
            {
                UnlimitedCount = true,
                StackObjectsCount = 9999999
            }
        });

        assort.BarterScheme[offreId] =
        [
            [
                new BarterScheme
                {
                    Count = prix,
                    Template = monnaieTpl
                }
            ]
        ];

        assort.LoyalLevelItems[offreId] = NiveauLoyaute;

        logger.Info($"[Montre Mousqueton Tactique] Montre en vente chez {nomMarchand}, niveau {NiveauLoyaute}.");
    }
}
