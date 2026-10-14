# CsAgent 0.9.1 — notes de version

La version 0.9.1 est une version de correction. Elle corrige le choix du modèle de vision, qui restait actif après l'envoi d'une seule image, et remplace le texte de `/help` par un menu sélectionnable dans l'interface `--leanui`. Elle fait suite à la 0.9.0.

- [Résumé](#résumé)
- [Notes de mise à jour](#notes-de-mise-à-jour)
- [1. Le modèle de vision ne suit plus toute la conversation](#1-le-modèle-de-vision-ne-suit-plus-toute-la-conversation)
- [2. LeanUI : un menu `/help` sélectionnable](#2-leanui--un-menu-help-sélectionnable)
- [Limites](#limites)
- [Tests](#tests)

## Résumé

| Domaine | Ce qui change |
|---|---|
| Routage des modèles | Seule l'image envoyée **avec le message courant** fait choisir le modèle de vision. Avant, une image envoyée une fois envoyait tous les tours suivants vers ce modèle |
| Historique | Les anciennes images sont remplacées par un court texte avant d'être envoyées à un modèle de code ou de chat |
| `--leanui` | `/help` ouvre un menu : on choisit une commande avec les flèches ou la souris. Taper `/` propose aussi les commandes |
| Tests | 192 tests (5 de plus) |

Aucune option, variable d'environnement ou fichier de configuration n'est ajouté ni modifié.

## Notes de mise à jour

1. **Une question sur une image doit être posée avec l'image.** Si tu poses une question de suivi sans coller à nouveau l'image, le modèle ne la voit plus : il ne dispose que de la description donnée au tour précédent. Colle l'image de nouveau pour qu'il la revoie.
2. **Le routage reprend son cours normal après une image.** Un tour de code, de travail avec outils ou de discussion qui suit un tour avec image est de nouveau choisi par les règles habituelles (`--model`, tes règles, mots-clés), et non plus forcé vers le modèle de vision.
3. **`/help` n'imprime plus de texte** dans `--leanui` : il ouvre le menu. Un script qui lisait le texte de `/help` dans cette interface doit être adapté.

Les fichiers de mémoire, les arguments de la ligne de commande et le dossier `LLMRoutingRules` ne changent pas.

## 1. Le modèle de vision ne suit plus toute la conversation

**Le problème.** Le routeur choisit le modèle de vision dès qu'une image est présente dans la conversation, et cette règle passe avant toutes les autres. Il regardait tout l'historique : après une seule image, chaque message suivant partait sur le modèle de vision, même une question générale (« qu'est-ce que la diarisation ? ») ou une tâche de code avec outils.

**La correction.** Un tour est un tour de vision seulement si le **dernier message de l'utilisateur** contient une image. Sinon :

- les images de l'historique sont remplacées par le texte `[image omitted: sent earlier in the conversation]` ; le texte du message est conservé, et le message devient une simple chaîne ;
- le modèle est choisi par le routage habituel (code ou chat), sans jamais recevoir de bloc image qu'il ne saurait pas lire.

Les images ne sont de toute façon pas enregistrées dans les fichiers de mémoire. Le changement s'applique à la console, à `--ui`, à `--leanui` et au mode `--api`. `--model` garde la priorité sur tout.

## 2. LeanUI : un menu `/help` sélectionnable

`/help` n'écrit plus des lignes de texte dans le journal : il ouvre un petit menu au-dessus de la zone de saisie, avec les commandes `/new [name]`, `/clear` et `/help`.

| Geste | Effet |
|---|---|
| Taper `/` | Propose les commandes, filtrées au fur et à mesure (`/c` ne garde que `/clear`) |
| ↑ / ↓ | Déplace la sélection (retour au début en fin de liste) |
| Entrée ou Tab | Choisit la commande |
| Échap | Ferme le menu |
| Souris | Le survol sélectionne, le clic choisit, un clic ailleurs ferme le menu |

- `/clear` et `/help` s'exécutent tout de suite. Pour `/new`, le champ est complété avec `/new ` : tu ajoutes un nom si tu veux, puis Entrée.
- Une commande tapée en entier (par exemple `/new` puis Entrée) est envoyée telle quelle, sans être complétée.
- Le champ de saisie garde le focus. Menu fermé, ↑ / ↓ parcourent toujours l'historique et Échap arrête toujours la génération.

Le menu est un composant web (`<cs-menu>`, Shadow DOM) défini dans `app.js` : aucune dépendance, aucun fichier ajouté. Pour ajouter une commande, ajoute une ligne à la liste `SLASH_ITEMS` et un cas dans `handleSlashCommand`.

## Limites

- Le menu n'existe que dans `--leanui`, pas dans `--ui`.
- Le menu a été testé avec Chromium. Firefox et Safari ne sont pas testés.
- Le choix du modèle après une image a été vérifié avec de faux serveurs, pas avec le vrai endpoint Albert.
- La publication native (AOT) de cette version n'a pas été essayée dans l'environnement de développement.

## Tests

192 tests (`dotnet run --project Tests -c Release`), dont les 5 nouveaux :

- une image dans le dernier message de l'utilisateur donne un tour de vision ;
- une image envoyée plus tôt, suivie d'une question texte, ne donne pas un tour de vision ;
- une réponse de l'assistant après l'image ne la masque pas (c'est toujours le dernier message de l'utilisateur qui compte) ;
- les anciennes images deviennent le texte de remplacement, le texte du message est conservé, le contenu devient une chaîne ;
- un historique sans image n'est pas modifié.

Le menu de `--leanui` a été vérifié à la main dans Chromium : ouverture, filtre, flèches, souris, Échap, clic ailleurs, focus conservé, historique des commandes (19 vérifications).
