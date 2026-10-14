# CsAgent 0.9.7 — notes de version

La version 0.9.7 ajoute l'outil `group_msg_by_sender`, qui range des e-mails Outlook `.msg` par expéditeur après avoir renommé les fichiers en noms ASCII. Elle fait suite à la 0.9.6.

- [Résumé](#résumé)
- [Notes de mise à jour](#notes-de-mise-à-jour)
- [L'outil `group_msg_by_sender`](#loutil-group_msg_by_sender)
- [Limites](#limites)
- [Tests](#tests)

## Résumé

| Domaine | Ce qui change |
|---|---|
| Nouvel outil | `group_msg_by_sender` : renomme les `.msg` d'un dossier en noms ASCII (sans accents, espaces ni caractères spéciaux), puis les range dans un sous-dossier par expéditeur. 35 outils au lieu de 34 |
| Documentation | README, `docs/index.html`, `docs/guide-csagent.html` et `docs/slides.html` : 35 outils, description de l'outil. Le numéro de version de l'interface web (qui affichait encore v0.9.5) est corrigé |
| Tests | 238 tests (7 de plus) |

Aucune variable d'environnement, aucun argument et aucun fichier de configuration ne sont ajoutés.

## Notes de mise à jour

1. **Un outil de plus.** `group_msg_by_sender` demande confirmation (comme `save_attachment`), sauf avec `--yes`. L'agent ne l'utilise que si la demande parle de ranger des `.msg`.
2. Le reste du comportement est inchangé : `--prompt` garde le mode script de la 0.9.6.

## L'outil `group_msg_by_sender`

```
csagent --prompt "range les .msg de ce dossier par expéditeur" --yes
```

Les noms de fichiers d'e-mails contiennent souvent des accents, des espaces et des caractères spéciaux, ce qui gêne les outils et les scripts. L'outil procède donc en deux étapes, dans cet ordre :

1. **Renommage.** Chaque `.msg` reçoit un nom sans accents, espaces ni caractères spéciaux : `Rapport trimestriel é.msg` devient `Rapport_trimestriel_e.msg`. Les lettres latines accentuées sont ramenées à leur forme simple (é → e, ß → ss, Œ → OE), tout autre caractère devient `_`.
2. **Rangement.** L'expéditeur est lu dans le message et le fichier est déplacé dans un sous-dossier à son nom, lui aussi en ASCII (`Jérôme Dupont` → `Jerome_Dupont`). Sans expéditeur : `UnknownSender`.

- Paramètres : `directory` (par défaut le dossier courant), `group_by` (`name` par défaut, ou `email` pour distinguer deux personnes de même nom), `rename` (`false` garde les noms d'origine).
- Seuls les `.msg` placés directement dans le dossier sont traités ; 5 000 fichiers au plus par appel.
- Rien n'est écrasé : un nom déjà pris reçoit ` (1)`, ` (2)`… Deux fichiers dont les noms deviennent identiques sont donc tous deux conservés.
- Un fichier illisible reste en place (renommé) et est signalé ; le compte rendu indique les fichiers renommés et déplacés, et le nombre de messages par expéditeur.
- C'est du C# pur : il fonctionne sur tous les systèmes et ne nécessite pas Outlook.

## Limites

- Les lettres latines (Europe occidentale et centrale) sont ramenées à l'ASCII ; les autres écritures (cyrillique, grec, chinois…) deviennent `_`, et un expéditeur écrit uniquement ainsi va dans `UnknownSender`.
- L'outil n'a pas été essayé avec un vrai modèle sur de vrais `.msg`, seulement par les tests.
- La publication native (AOT) de cette version n'a pas été essayée dans l'environnement de développement.

## Tests

238 tests (`dotnet run --project Tests -c Release`), dont les 7 nouveaux :

- les noms sont ramenés à l'ASCII (accents, espaces, caractères interdits, noms réservés, longueur, accent séparé) ;
- l'outil demande confirmation ;
- les fichiers sont renommés puis rangés dans un dossier ASCII par expéditeur ;
- les noms devenus identiques sont conservés tous les deux, et un fichier illisible reste en place avec une erreur signalée ;
- un fichier déjà présent dans le dossier de l'expéditeur n'est jamais écrasé ;
- `rename=false` garde les noms, `group_by=email` sépare deux personnes de même nom ;
- un dossier hors du répertoire de travail, absent ou sans `.msg`, et une valeur de `group_by` invalide, sont refusés ou signalés.
