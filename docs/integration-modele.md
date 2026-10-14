# Utiliser le modèle dans un logiciel de mouvements de souris

Ce guide explique comment intégrer le générateur **MouseMotionLab** dans un logiciel qui
produit ou modifie des déplacements de souris. Les exemples correspondent au code présent
dans ce dépôt : API Python, interface en ligne de commande et client C#.

## 1. Ce que reçoit et produit le modèle

Le logiciel fournit un **déplacement relatif total** `(dxCounts, dyCounts)`. Le générateur
retourne une trajectoire complète, composée de petits déplacements et de leurs durées :

```text
Déplacement souhaité en counts + DPI + fréquence + seed
                            |
                            v
                  Générateur MouseMotionLab
                            |
                            v
                 Trajectoire : (dtUs, dx, dy)
                            |
                            v
           Horloge de rejeu dans le logiciel consommateur
                            |
                            v
       Fonction existante qui applique les déplacements
```

Exemple : demander `(800, -250)` produit des deltas dont la somme vaut exactement
`(800, -250)`. Le modèle choisit la durée et la forme du mouvement. La génération s'achève
avant le début du rejeu ; sa latence s'ajoute donc au délai avant le premier mouvement.

Deux modes sont disponibles :

| Mode | Utilisation | Artefact nécessaire |
| --- | --- | --- |
| `baseline` | Trajectoire droite minimum-jerk pour vérifier l'intégration. | Facultatif. Sans artefact, durée estimée par une formule. |
| `diffusion` | Trajectoire issue du modèle de durée et de la diffusion entraînés. | Oui. |

Avec un artefact, la baseline utilise aussi le modèle de durée et la couverture du dataset,
mais sa géométrie reste une droite minimum-jerk.

L'API actuelle génère un déplacement dont la cible est déjà connue. Pour remplacer un
déplacement synthétique dans votre logiciel, envoyez son endpoint au générateur puis faites
consommer la trajectoire par votre moteur. Transformer chaque paquet d'une souris physique
en temps réel demanderait un mécanisme de filtrage et de replanification supplémentaire.

## 2. Préparer l'environnement et le modèle

Les commandes PowerShell suivantes s'exécutent depuis la racine du dépôt :

```powershell
Set-Location 'C:\Users\RAID\Desktop\ai'
```

Sur une installation neuve, suivre le [démarrage rapide](../README.md#démarrage-rapide).
Le package ML déclare Python `>=3.11,<3.15` ; le client .NET du dépôt cible .NET 10.
Si un environnement Python et PyTorch compatibles sont déjà installés, installer le package
dans cet environnement suffit pour appeler le générateur :

```powershell
.\.venv\Scripts\python.exe -m pip install --editable .\ml
```

Pour entraîner à partir des captures locales :

```powershell
.\scripts\train-example.ps1
```

Ce script prépare les données, entraîne et évalue le modèle. Il écrit le modèle dans
`artifacts/latest`. Pour un modèle entraîné sur Colab, utiliser le dossier `model` extrait
selon le [guide Colab](../README.md#entraîner-sur-google-colab), par exemple
`artifacts/colab-model`.

Distribuer **le dossier d'artefact complet**, avec ces cinq fichiers nécessaires à l'inférence :

| Fichier | Rôle |
| --- | --- |
| `metadata.json` | Version, architecture, paramètres d'échantillonnage et domaine appris. |
| `diffusion.safetensors` | Poids du modèle de trajectoire. |
| `duration.safetensors` | Poids du modèle de durée. |
| `normalization.npz` | Statistiques de normalisation. |
| `coverage.npz` | Références permettant d'estimer la couverture de la demande. |

`history.json`, lorsqu'il est présent, sert au suivi de l'entraînement. La base de captures
et le dossier `prepared` ne sont pas nécessaires pour générer avec un artefact existant.
Conserver ensemble les fichiers issus du même entraînement.

## 3. Choisir les unités et les paramètres

| Paramètre Python | Option CLI | Signification |
| --- | --- | --- |
| `dx_counts` | `--dx` | Déplacement relatif horizontal total, entier en counts. |
| `dy_counts` | `--dy` | Déplacement relatif vertical total, entier en counts. |
| `dpi` | `--dpi` | DPI réel utilisé pour convertir les counts en distance physique ; entre 100 et 100 000. |
| `output_hz` | `--output-hz` | Fréquence d'échantillonnage de la trajectoire ; entre 125 et 8 000 Hz, défaut 1 000. |
| `seed` | `--seed` | Graine aléatoire ; utiliser par exemple `42` pour reproduire un cas. |
| `mode` | `--mode` | `baseline` ou `diffusion` ; défaut `diffusion`. |

La cible `(0, 0)` est rejetée. Dans un logiciel consommateur, traiter ce cas comme une
absence de déplacement, sans appeler le modèle. Pour partager les seeds entre Python et C#,
utiliser des entiers non négatifs représentables par un `long` C#.

### Counts, millimètres et angles

Un count est une unité de déplacement brut de souris. La conversion physique est :

```text
millimètres = counts × 25,4 / DPI
counts = arrondi(millimètres × DPI / 25,4)
```

À 800 DPI, `(800, -250)` représente environ `(25,4 mm, -7,9375 mm)`.
Le DPI affecte le conditionnement du modèle même si l'endpoint en counts reste identique.

Dans la caméra de MouseMotionLab, la sensibilité `S` est exprimée en degrés par millimètre :

```text
degrés_par_count = 25,4 × S / DPI
variation_yaw   =  dx × degrés_par_count
variation_pitch = -dy × degrés_par_count
```

Pour demander une rotation relative `(Δyaw, Δpitch)` avec cette convention :

```text
dxCounts = arrondi( Δyaw   / degrés_par_count)
dyCounts = arrondi(-Δpitch / degrés_par_count)
```

Dans ce repère, `dx > 0` tourne à droite et `dy > 0` incline vers le bas.
Le pitch de la caméra est limité à ±85° : un endpoint exact en counts peut donc être écrêté
à l'affichage. La garantie du modèle porte sur la somme des deltas, avant les contraintes
appliquées par le logiciel.

### Si votre logiciel utilise des pixels

Les pixels du curseur Windows et les counts Raw Input sont des unités différentes.
Raccorder la sortie au point de votre moteur qui accepte des counts, ou prévoir une conversion
calibrée vers les unités de son API. Pour une conversion linéaire de `P` pixels par count,
une cible de `Δx` pixels correspond à `arrondi(Δx / P)` counts.

Si le moteur applique une accélération ou une transformation non linéaire, ce facteur constant
ne suffit pas. Mesurer le déplacement réellement obtenu et conserver les fractions lors des
conversions successives pour éviter une dérive d'arrondi. Une API de déplacement du curseur
système n'offre pas automatiquement les mêmes unités ni le même chemin d'entrée que Raw Input.

## 4. Premier essai par la ligne de commande

Vérifier d'abord le contrat sans modèle entraîné :

```powershell
.\.venv\Scripts\python.exe -m mousemotion.cli generate `
  --dx 800 --dy -250 --dpi 800 --output-hz 1000 `
  --seed 42 --mode baseline --json-stdout
```

Puis utiliser le modèle entraîné et sauvegarder le résultat :

```powershell
.\.venv\Scripts\python.exe -m mousemotion.cli generate `
  --dx 800 --dy -250 --dpi 800 --output-hz 1000 `
  --seed 42 --mode diffusion --artifact artifacts\latest `
  --device auto --output artifacts\integration\trajectory.json
```

`--device auto` choisit CUDA si PyTorch la détecte, sinon le CPU. `--device cpu` impose le
CPU ; `--device cuda` échoue si CUDA est indisponible.

Pour intégrer depuis un langage quelconque, lancer la commande avec `--json-stdout`, lire
stdout comme un document JSON unique et lire stderr séparément. Le code de sortie `0`
indique une génération réussie ; une erreur traitée par la CLI est écrite sur stderr avec
le code `1`. Consommer les deux flux sans blocage et vérifier le code de sortie avant le JSON.
La CLI reçoit des arguments, pas un document de requête sur stdin.

Chaque invocation lance Python et recharge l'artefact. Cette méthode convient à une première
intégration ou à des générations ponctuelles. Pour des appels répétés, garder un worker Python
vivant avec un générateur déjà chargé ; le protocole de communication de ce worker reste à
implémenter dans votre logiciel.

## 5. Intégration Python : charger une fois, générer plusieurs fois

Enregistrer cet exemple dans un fichier et l'exécuter avec le Python du virtualenv. Adapter
`repository_root` si le dépôt se trouve ailleurs.

```python
from pathlib import Path

from mousemotion.contracts import TrajectoryRequest
from mousemotion.generation import TrajectoryGenerator

repository_root = Path(r"C:\Users\RAID\Desktop\ai")
generator = TrajectoryGenerator(
    repository_root / "artifacts" / "latest",
    device="auto",
)

for dx, dy, seed in ((800, -250, 42), (-400, 100, 43)):
    request = TrajectoryRequest(
        dx_counts=dx,
        dy_counts=dy,
        dpi=800,
        output_hz=1000,
        seed=seed,
        mode="diffusion",
    )
    result = generator.generate(request)
    result.validate_against(request)

    print(result.to_json())
    # Transmettre result.samples au moteur de rejeu du logiciel.
```

`result.samples` contient des objets avec `dt_us`, `dx` et `dy`. `result.click_at_us`
donne la durée totale et l'instant du clic logique. Le JSON utilise les noms camelCase
décrits dans la section suivante.

Pour une baseline autonome, construire `TrajectoryGenerator()` puis demander
`mode="baseline"`. Donner un chemin d'artefact au constructeur déclenche son chargement,
même si la demande suivante est une baseline.

L'appel `generate()` est synchrone : l'exécuter sur un worker si votre logiciel a une
interface ou une boucle sensible à la latence. Sérialiser les demandes sur ce worker et
transmettre seulement les résultats complets au thread qui applique les mouvements.

## 6. Lire et valider le résultat JSON

Le format est défini dans [trajectory-v1.schema.json](../schemas/trajectory-v1.schema.json).
Cet exemple fictif de deux échantillons illustre les champs et les sommes ; il ne représente
pas une sortie typique du modèle :

```json
{
  "schemaVersion": 1,
  "samples": [
    { "dtUs": 1000, "dx": 100, "dy": -30 },
    { "dtUs": 1000, "dx": 700, "dy": -220 }
  ],
  "clickAtUs": 2000,
  "coverageScore": 0.7,
  "warnings": [],
  "modelVersion": "exemple-de-contrat-v1",
  "seed": 42
}
```

Les `dx` et `dy` sont des **deltas relatifs**, pas des positions absolues. `dtUs` est
l'intervalle en microsecondes depuis l'échantillon précédent. Le premier delta est dû
après son premier `dtUs`, à compter du début du rejeu.

Avant d'appliquer un résultat, vérifier :

1. `schemaVersion == 1` et une liste `samples` non vide.
2. Chaque `dtUs` est un entier strictement positif ; `dx` et `dy` sont des entiers.
3. `sum(dx) == dxCounts` et `sum(dy) == dyCounts` de la demande.
4. `sum(dtUs) == clickAtUs`.
5. `coverageScore` est fini et compris entre `0` et `1`.

La validation de schéma vérifie les types et les bornes. Les égalités entre les sommes et
la requête se vérifient séparément. Les contrats Python et C# fournissent
`validate_against(request)` et `ValidateAgainst(request)` pour les invariants de trajectoire.

`coverageScore` mesure la proximité avec les distances et directions du dataset. Ce score
n'est pas une probabilité de réalisme ou une certification de qualité.

| Avertissement | Interprétation |
| --- | --- |
| `coverage_model_unavailable` | Baseline sans artefact : couverture inconnue, score à zéro. |
| `out_of_distribution:...` | Distance ou direction éloignée du domaine appris ; la trajectoire est quand même retournée. |
| `duration_clamped_to_training_range` | Durée ramenée dans l'intervalle observé à l'entraînement. |
| `duration_clamped_to_model_maximum` | Durée limitée par la longueur maximale du modèle. |
| `residual_scaled_to_training_geometry:...` | Résidu réduit pour respecter l'enveloppe géométrique du dataset. |

Les avertissements sont des chaînes dont certaines portent un détail après `:`.
Pour les classer, comparer leur préfixe. Définir une politique dans le logiciel : conserver
la sortie avec un indicateur, sélectionner explicitement la baseline ou refuser le rejeu.
La génération ne sélectionne pas automatiquement un autre mode.

## 7. Intégration C# avec le client existant

Deux fichiers fournissent déjà l'interface nécessaire :

- [TrajectoryContracts.cs](../src/MouseMotionLab.Core/Contracts/TrajectoryContracts.cs) :
  requête, résultat et validation.
- [TrajectoryGeneratorClient.cs](../src/MouseMotionLab.Capture/Services/TrajectoryGeneratorClient.cs) :
  lancement Python, lecture JSON et gestion des erreurs.

Dans MouseMotionLab, ces classes sont déjà disponibles. Pour un autre logiciel C#,
référencer `MouseMotionLab.Core` et intégrer le fichier du client à votre projet,
ou reprendre les contrats et le client en adaptant leurs namespaces et leur framework cible.
Le fichier du client ne dépend pas des contrôles WPF.

Cet extrait s'utilise dans une méthode `async` ; `cancellationToken` est le token de votre
opération. `repositoryRoot` désigne la racine du dépôt, pas le dossier du modèle :

```csharp
using MouseMotionLab.Core.Contracts;
using MouseMotionLab.Capture.Services;

var repositoryRoot = @"C:\Users\RAID\Desktop\ai";
var client = new TrajectoryGeneratorClient(repositoryRoot);
var request = new TrajectoryRequest(
    DxCounts: 800,
    DyCounts: -250,
    Dpi: 800,
    OutputHz: 1000,
    Seed: 42,
    Mode: "diffusion");

TrajectoryResult trajectory = await client.GenerateAsync(
    request,
    artifactPath: @"artifacts\latest",
    cancellationToken: cancellationToken);

trajectory.ValidateAgainst(request);
```

Pour la baseline autonome, utiliser `Mode: "baseline"` et `artifactPath: null`.
Le client cherche `.venv/Scripts/python.exe`, puis utilise `python` si ce fichier manque.
Il configure `PYTHONPATH` vers `ml/src` et résout les chemins relatifs d'artefact depuis
`repositoryRoot`.

Ce client lance un processus par demande et laisse la CLI choisir `device=auto`.
Pour imposer le CPU, ajouter les arguments `--device cpu` dans une adaptation du client,
ou appeler directement la CLI. Son token peut annuler l'attente ; le code actuel n'arrête
pas explicitement le processus Python. Si votre logiciel doit interrompre le calcul,
prévoir aussi la terminaison et la récupération du processus dans votre adaptation.

## 8. Rejouer les mouvements dans votre logiciel

Faire démarrer une horloge monotone lorsque la trajectoire est prête. À chaque mise à jour
du moteur, appliquer tous les échantillons dont l'échéance cumulée est atteinte, dans l'ordre.
Conserver les échéances d'origine pour éviter de cumuler une dérive après chaque attente.

Voici un lecteur C# qui peut être appelé depuis votre boucle de mise à jour. Il ne lance
aucune génération et sa callback reçoit les counts attendus par votre moteur :

```csharp
using System;
using System.Diagnostics;
using MouseMotionLab.Core.Contracts;

public sealed class TrajectoryPlayback
{
    private readonly TrajectoryResult _trajectory;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _index;
    private long _scheduledUs;

    public TrajectoryPlayback(TrajectoryRequest request, TrajectoryResult trajectory)
    {
        trajectory.ValidateAgainst(request);
        _trajectory = trajectory;
        _clock.Restart();
    }

    public bool Finished => _index == _trajectory.Samples.Count;

    public void Tick(Action<int, int> applyDeltaCounts)
    {
        double elapsedUs = _clock.Elapsed.TotalMicroseconds;
        while (!Finished)
        {
            TrajectorySample sample = _trajectory.Samples[_index];
            long dueUs = _scheduledUs + sample.DtUs;
            if (dueUs > elapsedUs)
            {
                break;
            }

            applyDeltaCounts(sample.Dx, sample.Dy);
            _scheduledUs = dueUs;
            _index++;
        }
    }
}
```

Après génération, créer `new TrajectoryPlayback(request, trajectory)`, puis appeler
`playback.Tick(votreFonctionDeDeplacementEnCounts)` sur le thread qui possède l'état du
mouvement. La fonction passée est le raccordement à votre logiciel : caméra, simulateur,
ou moteur de déplacements avec conversion d'unités si nécessaire.

Lorsque `Finished` devient vrai, signaler la fin une seule fois et retirer le lecteur de la
boucle. `clickAtUs` représente un clic logique au terme de la trajectoire ; le générateur
n'émet pas de bouton de souris. Un logiciel qui ne modifie que le mouvement peut utiliser
cet instant comme marqueur de fin, sans déclencher de clic.

À 1 000 Hz, une frame de 60 Hz traite souvent plusieurs échantillons. Le lecteur conserve
leur ordre et leur somme, mais cette boucle ne garantit pas une émission matérielle à
1 000 Hz. `outputHz` règle l'échantillonnage du résultat, pas la précision de l'ordonnanceur
du logiciel ou le polling du périphérique. Mesurer la cadence effective de votre backend.

Appliquer les deltas dans l'ordre est utile si le moteur borne les positions ou les angles :
additionner plusieurs deltas avant de les appliquer peut changer le résultat aux limites.

### Remplacer ou interrompre un mouvement

Conserver au plus une trajectoire active par canal de mouvement. Pour remplacer un déplacement
synthétique existant, retirer son ancien programme de deltas avant d'activer le nouveau.
Si vous ajoutez aussi les deltas physiques de l'utilisateur, le déplacement final sera la
somme des deux sources.

Pour annuler, retirer le lecteur actif de la boucle. Les deltas déjà appliqués restent acquis ;
l'endpoint initial n'est garanti que si la trajectoire est consommée en entier. En cas de
nouvelle cible, calculer le déplacement restant depuis l'état courant, générer la nouvelle
trajectoire et décider explicitement si l'ancien rejeu continue pendant le calcul.
Associer un identifiant aux demandes pour écarter un résultat devenu obsolète avant son retour.

## 9. Vérifier l'intégration et diagnostiquer les problèmes

Commencer avec la baseline et un seul déplacement, puis essayer plusieurs directions,
distances et seeds avec le modèle. Journaliser la demande, `modelVersion`, les avertissements,
la latence de génération, la durée demandée par `clickAtUs` et les deltas réellement appliqués.
Vérifier les invariants en counts, puis mesurer l'endpoint dans les unités du logiciel après
ses conversions et contraintes.

| Symptôme | Vérification |
| --- | --- |
| `No module named mousemotion` | Utiliser le Python du virtualenv et installer `ml`, ou configurer `PYTHONPATH` vers `ml/src`. |
| `A trained artifact is required...` | Fournir le dossier complet avec `--artifact`, ou choisir la baseline. |
| Fichier manquant ou poids incompatibles | Vérifier les cinq fichiers d'inférence et leur origine commune. |
| `CUDA was requested but is not available...` | Essayer `--device cpu` et vérifier l'installation PyTorch utilisée par le processus. |
| Bonne somme de counts, mauvaise distance à l'écran | Vérifier la conversion counts/pixels, la sensibilité, l'accélération et les limites du moteur. |
| Mouvement trop lent ou saccadé | Séparer la latence de génération du temps de rejeu ; mesurer la fréquence réelle de consommation. |
| Trajectoire très droite | Vérifier le mode choisi ; un résidu diffusion peut aussi être réduit par l'enveloppe géométrique. |
| Avertissements de couverture fréquents | Comparer les demandes au domaine de `metadata.json`, collecter les cas manquants puis réentraîner. |

Une seed fixe aide à reproduire un cas avec le même artefact et le même environnement.
Elle ne garantit pas une sortie identique entre des versions de PyTorch ou des matériels
différents. Conserver l'artefact utilisé pour les essais de référence.

Les nouvelles captures ne mettent pas à jour un générateur déjà chargé. Réentraîner,
publier un nouveau dossier complet, puis reconstruire le générateur du worker pour le charger.
La qualité dépend de la diversité des captures ; l'évaluation sur des sessions séparées
reste nécessaire pour apprécier les trajectoires sur des demandes nouvelles.

## 10. Points de référence dans le dépôt

- [generation.py](../ml/src/mousemotion/generation.py) : génération baseline et diffusion.
- [contracts.py](../ml/src/mousemotion/contracts.py) : objets Python et sérialisation JSON.
- [cli.py](../ml/src/mousemotion/cli.py) : arguments et comportement du processus.
- [artifacts.py](../ml/src/mousemotion/artifacts.py) : chargement des fichiers d'inférence.
- [MainWindow.xaml.cs](../src/MouseMotionLab.Capture/MainWindow.xaml.cs) :
  `GenerateAndReplayAsync`, `StartReplay` et `AdvanceReplay` montrent le raccordement existant.
- [FirstPersonLookController.cs](../src/MouseMotionLab.Capture/Services/FirstPersonLookController.cs) :
  conversion des counts en orientation dans la scène locale.
- [Architecture](architecture.md) et [dataset](dataset.md) : représentation apprise et captures.

Le rejeu livré dans MouseMotionLab applique les deltas à sa caméra locale. Pour votre logiciel,
le raccordement à son moteur de mouvements est la partie à fournir.
