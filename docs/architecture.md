# Architecture technique

## Acquisition

`MouseMotionLab.Capture` utilise une fenêtre WPF pour l’interface et une fenêtre Win32
invisible, attachée à un thread dédié, pour `WM_INPUT`. L’inscription Raw Input reçoit les
notifications même si la file UI est occupée. Un garde applicatif accepte les paquets lorsque la
scène 3D possède la souris et uniquement si leur `hDevice` correspond à la souris choisie. La
caméra peut ainsi continuer à tourner en état `Idle`, tandis que la machine d’états ne conserve
les événements qu’entre l’armement et le clic terminal.

Chaque paquet est horodaté par `QueryPerformanceCounter`. Le thread natif ne dessine rien et
n’écrit jamais dans SQLite : il dépose les paquets dans une file bornée. La machine d’états
conserve au maximum 8 192 événements par essai ; un dépassement invalide explicitement l’essai.
L’écriture SQLite se fait ensuite dans une transaction sur un worker distinct.

États :

```text
Idle --Espace--> Armed --premier delta--> Recording --clic gauche--> Idle
                       \--clic sans delta--> Invalid
Armed/Recording --Échap--> Cancelled
Armed/Recording --focus perdu, déconnexion, débordement, fermeture--> Invalid
```

Un clic dans le `Viewport3D` sauvegarde le pointeur Windows, le masque et le confine à la scène.
Les deltas Raw Input sont agrégés entre deux frames WPF puis appliqués au lacet et au pitch de la
caméra ; le viseur est un overlay fixe. Le lacet boucle sur 360° et le pitch est borné à ±85°.
L’armement réinitialise seulement l’origine diagnostique du pointeur : il ne touche jamais à
l’orientation de caméra. Une lease unique restaure toujours le clip, la visibilité et la position
d’origine lors de `Échap`, d’une perte de focus, d’une déconnexion, d’une erreur ou de la fermeture.

## Représentation ML

Les événements originaux restent intacts dans SQLite. Le prétraitement :

1. interpole la position cumulative sur une grille de 1 ms entre le premier mouvement et le clic ;
2. convertit les counts en millimètres avec le DPI déclaré ;
3. tourne la trajectoire dans le repère longitudinal/lateral de sa cible ;
4. soustrait une trajectoire minimum-jerk ;
5. divise le résidu par la distance et le projette dans le sous-espace de somme nulle.

Le modèle de durée est un mélange de cinq gaussiennes sur `log(duration_ms)`. La trajectoire
est produite par un U-Net 1D conditionné par distance, angle et durée. Pendant chaque étape
DDIM, le résidu est reprojeté à somme nulle. La baseline atteint donc la cible, et le résidu ne
peut pas déplacer cet endpoint. Le dernier invariant est assuré lors du retour aux counts par
arrondi de la position cumulative, avec endpoint final forcé exactement.

L’artefact conserve également le domaine de distance/durée et les percentiles de tortuosité et
de déviation latérale du train. Une sortie diffusion dépassant le domaine est avertie ; un
résidu dépassant l’enveloppe géométrique réelle est réduit uniformément puis reprojeté, sans
changer la cible.

## Limite d’intégration

Le contrat public retourne uniquement des deltas et un clic logique. La fenêtre les rejoue sur
sa propre caméra 3D, à partir de l’orientation courante. Le projet ne contient ni `SendInput`, ni
hook, ni injection vers une autre application.

