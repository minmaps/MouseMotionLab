# Dataset SQLite

La base `data/mousemotion.db` est en WAL et porte actuellement le schéma `3`. L’application
migre automatiquement les schémas `1` et `2` sans toucher aux événements existants.

- `profiles` : chemin Raw Input, nom affiché, DPI, polling nominal et sensibilité visuelle. Dans
  l’interface 3D, cette dernière est interprétée en degrés par millimètre.
- `sessions` : profil, version de l’application, OS et fréquence QPC.
- `trials` : segmentation, endpoint, durée, fréquence, médiane/max des intervalles,
  pauses supérieures à 8 ms, statut et raison d’exclusion.
- `events` : ordre, ticks QPC, deltas bruts, molette, drapeaux des boutons et déplacement
  cumulé optionnel du curseur Windows (`cursor_dx_pixels`, `cursor_dy_pixels`) depuis l’origine
  diagnostique. Ces colonnes sont conservées pour compatibilité ; la scène FPS et le modèle
  utilisent les counts Raw Input.

Les statuts `invalid`, `cancelled` et `excluded` restent en base pour audit mais ne sont pas
chargés par `prepare`. Les seuils par défaut sont 20–1 000 ms et 0,5 mm. Les splits sont faits
par session entière : aucune session ne peut se retrouver dans plusieurs partitions.

La préparation interpole la position Raw Input cumulative sur une grille canonique de 1 ms,
sans modifier les événements natifs. Les échantillons du curseur Windows sont conservés comme
référence comportementale mais ne remplacent pas les counts pour l’apprentissage. Le délai
armement→premier mouvement est conservé séparément dans `armed_to_motion_ms.npy` et n’entre
pas dans la trajectoire apprise.

Le DPI n’est pas exposé de manière fiable par Raw Input ; il doit donc être renseigné avec la
valeur réellement configurée dans le logiciel de la souris. Un DPI incorrect fausse directement
la normalisation physique.

Le panneau **Données → Voir les courbes de vitesse** affiche le minimum, le maximum et la
moyenne à chaque point de la durée normalisée (0–100 %), sur tous les essais acceptés de la
base. Chaque essai a le même poids dans la moyenne, quelle que soit sa durée ou son nombre
de paquets. Les vitesses en mm/s utilisent la longueur des deltas Raw Input, les intervalles
QPC réels et le DPI du profil. Les paquets ayant le même timestamp sont regroupés ; le
premier groupe n’a pas de vitesse mesurable, faute d’intervalle précédent. Les vitesses sont
placées au milieu des intervalles, interpolées sur 101 points et maintenues aux extrémités.
Les essais sans intervalle exploitable sont signalés et ignorés. Le graphique est actualisé
après chaque capture ou exclusion ; **Actualiser** relit également la base.
L’option **Échelle logarithmique** permet de lire les faibles vitesses lorsque les maxima
sont élevés ; la transformation `log(1 + vitesse)` conserve le zéro et les graduations
restent exprimées en mm/s.

