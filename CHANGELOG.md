# Historique

## 1.0.0 — 15 novembre 2026

Première version publique, sous le nom **Eclipse**.

- **Luminosité des écrans** (DDC/CI) : un clic sur l'icône ouvre un panneau avec un curseur par écran et la case
  « Lier les écrans ». Ctrl+Alt+Maj+Haut / Bas règle tous les écrans de 10 % à la fois, avec un rappel en bas de
  l'écran. Les échanges avec les écrans ont leur propre fil : pendant qu'on glisse un curseur, seule la dernière valeur
  part vers l'écran.
- **Sous le minimum de l'écran** : à gauche du seuil « min », en orange, jusqu'à −90 %. L'écran reste à son minimum et
  l'image est assombrie par l'effet de couleur plein écran de Windows. Pas de fenêtre posée par-dessus, et l'effet
  disparaît de lui-même si Eclipse s'arrête. La table de gamma de Windows a été essayée d'abord : elle refuse
  d'assombrir de plus de 50 %.
- Le double-clic sur l'icône éteint toujours. Un simple clic ouvre le panneau.
- Tous les textes vouvoient.

## Avant la version publique

### 10 novembre 2026
- Une touche au hasard ne rallume plus tout. Une touche, un clic ou la souris réveillent l'écran 5 secondes, le temps
  de lire le rappel ; seuls le raccourci et les sorties de secours rallument.

### 9 novembre 2026
Une nuit, les écrans sont restés noirs au rallumage, avec le clavier et la souris sans effet, pendant qu'un calcul
occupait toute la carte graphique. Corrigé ainsi :
- chaque tâche a son fil : la fenêtre noire, le blocage du clavier et de la souris, le son et Sonar, la garde. Au
  rallumage, la fenêtre part d'abord et le clavier et la souris reviennent aussitôt ; viennent ensuite les écrans, le
  son et Sonar, et une étape qui échoue n'arrête pas les suivantes ;
- la sortie de secours Ctrl+Alt+Maj+Échap, vue par le blocage et, à part, par le fil de garde. Si la fenêtre ne part
  toujours pas, le programme se ferme de force et se relance ;
- le raccourci vu deux fois (par le blocage et par Windows) ne relance plus une extinction ;
- l'ordre d'extinction n'est plus envoyé à toutes les fenêtres que si l'envoi au programme seul n'a pas suffi, et des
  écrans rééteints dans les 15 secondes après le rallumage sont rallumés ;
- plus de fenêtre d'erreur cachée sous l'écran noir : tout va dans `%LOCALAPPDATA%\Eclipse\journal.txt`.

### 7 novembre 2026
- Le son : un simple « muet » des sorties par défaut, retiré au retour. Aucun volume n'est changé, et la sortie par
  défaut de Windows n'est jamais choisie par le programme.
- SteelSeries Sonar : quand les écrans s'éteignent, Windows retire leurs sorties son, et au rallumage Sonar pouvait
  basculer sur le mauvais écran. Ses sorties sont notées avant d'éteindre et remises au rallumage.
- Le logo : l'éclipse.
- Au rallumage, la souris n'est bougée d'un pixel, pour réveiller les écrans, que si c'est vous qui rallumez. Windows
  qui change de bureau tout seul ne doit pas faire croire aux programmes qui guettent votre activité que vous êtes
  revenu.

### 6 novembre 2026
- Première version : éteindre les écrans sans mettre le PC en veille.
