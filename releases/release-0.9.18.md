# CsAgent 0.9.18 — notes de version

La version 0.9.18 corrige l'erreur HTTP 400 `Unexpected role 'tool' after role 'user'` qui apparaissait quand l'historique avait été élagué ou quand on changeait de modèle. Elle fait suite à la 0.9.17.

- [Résumé](#résumé)
- [Notes de mise à jour](#notes-de-mise-à-jour)
- [Cause](#cause)
- [Comment ça marche](#comment-ça-marche)
- [Limites](#limites)
- [Tests](#tests)

## Résumé

| Domaine | Ce qui change |
|---|---|
| Correction | Un résultat d'outil (`tool`) n'est plus jamais envoyé sans l'appel de l'assistant (`tool_calls`) qui le précède, et inversement |
| Code | `Shared/JsonHelpers.cs` : nouvelle méthode `RepairToolPairs`, appelée à la fin de `TrimHistory` ; `Core/Llm/LlmClient.cs` : `SanitizeMessages` l'appelle aussi |
| Tests | 331 tests (+6) : 6 nouveaux dans `ToolPairTests.cs` |

Aucune option de ligne de commande ni variable d'environnement n'est ajoutée.

## Notes de mise à jour

1. **Rien à faire** : il n'est pas nécessaire de vider la mémoire (`conversation.json`) ni de changer de dossier `--mem`.
2. Les fichiers de mémoire existants ne sont pas modifiés. La réparation s'applique à la copie envoyée au modèle, et à l'historique en mémoire après l'élagage.
3. Une conversation dont les appels d'outils et les résultats sont déjà cohérents n'est pas changée.

## Cause

`TrimHistory` supprime les plus anciens messages un par un dès que l'historique dépasse `--context-chars` (96 000 caractères par défaut). Il ne tient pas compte des appels d'outils : il peut supprimer le message `assistant` qui contient `tool_calls` et garder les messages `tool` qui en sont les résultats. La requête suivante contient alors un résultat d'outil sans appel, juste après le message `[SESSION CONTEXT]` (rôle `user`).

Les serveurs à modèle de conversation strict (vLLM : Mistral, Gemma et d'autres) refusent cette suite avec l'erreur 400 `Unexpected role 'tool' after role 'user'`. DeepSeek l'accepte, ce qui fait apparaître l'erreur surtout après un changement de modèle.

## Comment ça marche

`RepairToolPairs` applique deux règles :

- un message `tool` doit suivre le message `assistant` dont `tool_calls` contient son `tool_call_id` (ou un autre message `tool` du même appel). Sinon il est retiré ;
- un message `assistant` avec `tool_calls` doit être suivi d'un résultat pour chaque appel. Sinon ce tour est retiré, avec les résultats restants.

Elle est appelée à deux endroits :

- à la fin de `TrimHistory`, pour que l'historique en mémoire reste valide après l'élagage ;
- dans `SanitizeMessages` (introduite par la 0.9.17), pour que la copie envoyée au serveur soit valide quel que soit l'état du fichier de mémoire, y compris pour les conversations déjà enregistrées dans cet état.

## Limites

- Un tour retiré n'est plus envoyé au modèle. La mémoire hybride (`[MEMORY]`) et le résumé de session (`[SESSION CONTEXT]`) en gardent l'essentiel.
- La réparation ne corrige que la structure des appels d'outils. Une autre cause d'erreur 400 (par exemple un schéma d'outil MCP sans `parameters`) reste à traiter séparément.

## Tests

331 tests (+6) : 6 nouveaux dans `Tests/ToolPairTests.cs`.

- un résultat d'outil juste après un message `user` (l'erreur signalée) est retiré ;
- une conversation valide, avec plusieurs appels dans un même tour, n'est pas modifiée ;
- un tour de l'assistant dont un résultat manque est retiré avec les résultats restants ;
- un appel d'outil final sans résultat, et un résultat avec un mauvais identifiant, sont retirés ;
- `TrimHistory` ne laisse plus de résultat d'outil sans son appel ;
- `SanitizeMessages` envoie une copie sans résultat orphelin et ne modifie pas l'historique enregistré.