# MouseMotionLab

MouseMotionLab collecte des flicks personnels à la fréquence native de la souris, conserve les
paquets Raw Input et entraîne un générateur conditionné par un endpoint. L’application présente
une scène FPS 3D locale : la génération fait tourner uniquement sa caméra et ne déplace jamais le
curseur système dans une autre application.

## Démarrage rapide

Prérequis : Windows x64, Python 3.13 et, pour l’entraînement accéléré, un GPU NVIDIA récent.
Le script installe si nécessaire le SDK .NET 10 et peut donc déclencher une demande UAC :

```powershell
.\scripts\bootstrap.ps1
.\scripts\build.ps1
.\scripts\run-capture.ps1
```

Le bootstrap installe PyTorch 2.14.1 CUDA 13.0 lorsqu’un pilote NVIDIA est détecté. Sur la
machine actuelle, `torch.cuda.is_available()` reconnaît la GeForce RTX 3060 Ti.

## Collecter

1. Choisir explicitement la souris Raw Input — les autres périphériques sont ignorés.
2. Renseigner le DPI réel et le polling nominal.
3. Cliquer dans la scène 3D. Le pointeur Windows est alors masqué et confiné à la scène ; les
   deltas Raw Input font tourner la caméra comme dans un FPS et le viseur reste fixe au centre.
4. Appuyer sur `Espace` (par défaut) pour armer. Dans **Raccourci pour armer**, cliquer sur le
   raccourci puis appuyer sur la touche ou le bouton de souris souhaité, éventuellement avec
   Ctrl, Alt ou Maj. Les clics gauche, droit, molette et les boutons Souris 4/5 sont acceptés.
   Dans la scène, le raccourci souris utilise les paquets Raw Input de la souris sélectionnée.
   Si le clic gauche arme, ce premier clic est consommé ; le clic gauche suivant termine le flick.
   Le choix est conservé après un redémarrage ; **Réinitialiser** rétablit Espace. Échap
   annule le choix. Échap, Retour arrière et F11 restent réservées aux actions existantes.
   L’orientation courante est conservée : armer un essai ne
   recentre ni la caméra ni le viseur.
5. Effectuer le flick librement ; le premier delta Raw Input commence la trajectoire.
6. Enfoncer le clic gauche pour terminer et étiqueter l’endpoint. La souris reste capturée afin
   de pouvoir continuer à regarder autour de soi et enchaîner avec la touche d’armement choisie.
7. Utiliser `Retour arrière` pour conserver mais exclure le dernier essai. `Échap` annule
   l’essai éventuel puis libère immédiatement la souris.

Le bouton `Plein écran` ou la touche `F11` transforme la scène en véritable surface de jeu et
masque les panneaux. Il faut terminer ou annuler l’essai courant avant de changer de mode. Quand
la souris est capturée, un premier `Échap` la libère ; un second quitte le plein écran.

La sensibilité 3D est exprimée en degrés par millimètre. La conversion appliquée à chaque count
est `25,4 × sensibilité / DPI`. Le lacet est cyclique sur 360° et le pitch est limité à ±85°.

La liste représente les **interfaces souris HID**, pas nécessairement des souris physiques. Un
clavier analogique, un pavé tactile ou un récepteur peut donc apparaître. MouseMotionLab affiche
le nom produit signalé par le bus USB afin de les distinguer.

Chaque paquet conserve les counts Raw Input de la souris sélectionnée. La fréquence, l’intervalle
médian/maximal, les pauses supérieures à 8 ms, le backlog et les débordements sont calculés depuis
les timestamps QPC ; l’application n’invente pas de paquets immobiles entre deux rapports HID.
Les anciennes colonnes de position du pointeur Windows restent conservées pour compatibilité,
mais la caméra 3D et l’apprentissage utilisent exclusivement les counts bruts, comme un FPS en
Raw Input.

Le dataset est écrit dans `data/mousemotion.db`. Viser au moins 5 000 essais variés en direction
et distance ; le prétraitement avertit sous 2 000.

Les nouveaux essais ne modifient pas automatiquement le générateur : il faut relancer
`scripts\train-example.ps1` pour produire un nouvel artefact. Dans l’interface, **Baseline —
droite minimum-jerk** est volontairement lisse et rectiligne et n’utilise pas les trajectoires
apprises. **Diffusion — modèle entraîné** charge `artifacts/latest` et utilise le dataset.

## Préparer, entraîner et évaluer

Le script suivant enchaîne les trois étapes avec CUDA lorsqu’elle est disponible :

```powershell
.\scripts\train-example.ps1
```

Commandes équivalentes :

```powershell
.\.venv\Scripts\python.exe -m mousemotion.cli prepare `
  --db data\mousemotion.db --output prepared\default

.\.venv\Scripts\python.exe -m mousemotion.cli train `
  --prepared prepared\default --artifact artifacts\latest --device auto

.\.venv\Scripts\python.exe -m mousemotion.cli evaluate `
  --prepared prepared\default --artifact artifacts\latest `
  --output artifacts\latest-evaluation --device auto
```

Les poids d’inférence sont stockés en `safetensors`. L’artefact contient aussi les statistiques
de normalisation, le modèle de couverture k-NN, le hash du dataset, la configuration et les
courbes de loss.

## Entraîner sur Google Colab

Exécuter `scripts\export-colab.ps1` pour produire le notebook
`notebooks/MouseMotionLab-GPU.ipynb` et l'archive `artifacts/colab-export/mousemotion-colab-bundle.zip`.
L'export utilise l'API de backup SQLite pour inclure les données validées même si le collecteur
est ouvert. Il inclut seulement le code ML, son manifeste et une copie des captures.

Importer le notebook dans Colab, sélectionner un runtime GPU, puis exécuter les cellules dans
l'ordre. La cellule d'import demande l'archive ZIP. Les réglages incluent epochs, batch size,
learning rates, weight decay, EMA, seed et canaux du U-Net. Le notebook conserve le PyTorch CUDA
fourni par Colab et prend en charge Python 3.11 à 3.14.

Télécharger le modèle dès la fin : les fichiers du runtime Colab sont temporaires et le pipeline
ne permet pas encore de reprendre un entraînement interrompu. Extraire le dossier `model`
dans `artifacts/colab-model`, puis indiquer ce chemin dans l'application en mode Diffusion.
L'export doit être refait pour incorporer de nouvelles captures. Les splits par session et les
avertissements de couverture restent identiques au pipeline local.

## Générer

La baseline fonctionne avant tout entraînement :

```powershell
.\.venv\Scripts\python.exe -m mousemotion.cli generate `
  --dx 800 --dy -250 --dpi 800 --output-hz 1000 `
  --seed 42 --mode baseline --json-stdout
```

Après entraînement, remplacer `baseline` par `diffusion` et ajouter
`--artifact artifacts\latest`. Le JSON suit [le schéma v1](schemas/trajectory-v1.schema.json).
Pour chaque résultat, `sum(dx)` et `sum(dy)` correspondent exactement à l’endpoint demandé.
Une demande éloignée du dataset est générée mais porte un avertissement `out_of_distribution`.
Pour un petit dataset, le résidu est aussi limité à l’enveloppe géométrique observée afin
d’éviter une trajectoire anormalement bruitée, sans modifier l’endpoint demandé.

## Structure

- `src/MouseMotionLab.Capture` : WPF, Raw Input, scène FPS 3D et replay caméra sandbox.
- `src/MouseMotionLab.Core` : machine d’états, SQLite et contrats partagés.
- `ml` : préparation, diffusion, entraînement, génération et évaluation.
- `tests` et `ml/tests` : tests .NET et Python, dont un mini pipeline entraînement→génération.

Les détails des threads et du modèle sont dans [docs/architecture.md](docs/architecture.md), et
le stockage est décrit dans [docs/dataset.md](docs/dataset.md).
