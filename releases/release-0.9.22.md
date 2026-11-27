# CsAgent 0.9.22 — notes de version

La version 0.9.22 garde les messages qui quittent le contexte de l'agent, et lui donne un outil, `recall`, pour les retrouver. Jusqu'à la 0.9.21, un message résumé par la compression ou retiré par l'élagage était perdu : le résumé de 300 mots n'en gardait que l'essentiel. Elle fait suite à la 0.9.21.

- [Résumé](#résumé)
- [Notes de mise à jour](#notes-de-mise-à-jour)
- [L'archive](#larchive)
- [L'outil recall](#loutil-recall)
- [Sessions](#sessions)
- [Limites](#limites)
- [Tests](#tests)

## Résumé

| Domaine | Ce qui change |
|---|---|
| Archive | Les messages qui sortent du contexte sont ajoutés à `archive.jsonl`, dans le dossier de mémoire, au lieu d'être perdus |
| Outil | Nouvel outil `recall` : recherche par mots-clés classée par BM25, en français et en anglais, avec les filtres `role`, `tool`, `since` et `limit` |
| Groupe d'outils | `recall` est dans le groupe `history` : il n'est décrit au modèle que si la demande renvoie au passé, après une compression, ou à la demande de l'agent |
| Sessions | `.save <nom>` copie aussi l'archive ; `.delete` et `--fresh` la suppriment ; `.session` affiche sa taille |
| Tests | 363 tests (+19) : 19 nouveaux dans `Tests/RecallTests.cs` |

L'agent compte maintenant 44 outils intégrés (43 avant).

## Notes de mise à jour

1. **Rien à faire** : l'archive se crée d'elle-même au premier message qui sort du contexte. Aucune option de ligne de commande n'est ajoutée et aucune dépendance non plus.
2. Le dossier de mémoire contient un cinquième fichier, `archive.jsonl`. Supprimez-le pour tout oublier.
3. Les tests du dépôt démarrent désormais avec la compression désactivée (`ContextBudget.Reset`) : l'appel au modèle de la compression consommait les réponses prévues des faux modèles. Cela ne change rien au programme, où la compression reste activée par défaut.

## L'archive

Plusieurs mécanismes retirent des messages du contexte : la compression, l'élagage (`TrimHistory`), le raccourcissement après une erreur « contexte trop long », et les commandes `.compress` et `.empty`. Ces messages sont maintenant ajoutés à la fin de `archive.jsonl`, une ligne JSON par message (`ts`, `role`, `tool`, `text`). `ts` est la date de sortie du contexte.

- **Jamais archivés** : le prompt système, les blocs `[MEMORY]` et `[SESSION CONTEXT]`, les résumés `[CONVERSATION SUMMARY]` et les résultats de `recall` lui-même. Un message est coupé à 8 000 caractères.
- **Écriture** : les messages sont archivés avant l'enregistrement de la conversation, donc un plantage entre les deux ne perd rien. Un échec d'écriture est signalé et n'arrête pas l'agent.
- **Ligne coupée** par un plantage : ignorée à la lecture. Un message archivé deux fois n'est trouvé qu'une fois.

## L'outil recall

L'agent l'utilise quand vous évoquez quelque chose de déjà dit ou fait, ou avant de refaire un travail peut-être déjà fait.

- **Recherche** : par mots-clés, classée par BM25. Les mots vides anglais et français sont retirés, les accents et les pluriels ramenés ; les mots rares comptent plus que les mots courants ; à égalité, le plus récent passe d'abord.
- **Filtres** : `role` (`user`, `assistant`, `tool`), `tool` (par exemple `sh`), `since` (par exemple `2026-11-01`), `limit` (5 par défaut, 10 au maximum).
- **Résultats** : la date, le rôle, l'outil, le message juste avant (l'appel, pour un résultat d'outil) et 700 caractères du texte. Le résultat rappelle au modèle que ce sont des données et non des instructions, car l'archive contient des contenus de fichiers et des sorties de commandes écrits par des tiers.
- **Sans index ni base de données** : le fichier est lu ligne par ligne à chaque recherche.
- **Groupe `history`** : la description de `recall` n'est envoyée au modèle que si votre demande renvoie au passé (« la dernière fois », « on avait dit », « où en étions-nous », « earlier »…), si l'agent l'active avec `enable_tools`, s'il a déjà servi dans la conversation, ou dès que la session a été résumée. Jamais tant que l'archive n'existe pas.

## Sessions

- `.save <nom>` copie aussi l'archive dans le nouveau dossier.
- `.delete <nom>` et `--fresh` la suppriment avec les autres fichiers de mémoire.
- `.session` affiche sa taille.
- `.empty` vide la conversation mais garde ses messages dans l'archive.

## Limites

- La recherche est par mots-clés : elle ne trouve pas un message qui dit la même chose avec d'autres mots.
- Le fichier est relu à chaque recherche. C'est rapide pour l'historique d'une personne, mais le coût croît avec la taille de l'archive.
- Un message est coupé à 8 000 caractères dans l'archive.

## Tests

19 nouveaux tests dans `Tests/RecallTests.cs` : ce qui est archivé, classement BM25, filtres, ligne coupée, doublons, compression et `.empty`, mots déclencheurs du groupe `history`.
