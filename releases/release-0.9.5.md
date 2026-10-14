# CsAgent 0.9.5 — notes de version

La version 0.9.5 réduit le nombre de jetons d'entrée envoyés à chaque étape, ce qui aide à rester sous les quotas de l'API. Elle corrige aussi le README. Elle fait suite à la 0.9.4.

- [Résumé](#résumé)
- [Notes de mise à jour](#notes-de-mise-à-jour)
- [1. Les anciens résultats d'outils sont raccourcis](#1-les-anciens-résultats-doutils-sont-raccourcis)
- [2. La taille de l'historique se règle](#2-la-taille-de-lhistorique-se-règle)
- [3. README corrigé](#3-readme-corrigé)
- [Limites](#limites)
- [Tests](#tests)

## Résumé

| Domaine | Ce qui change |
|---|---|
| Historique envoyé au modèle | Les résultats d'outils plus anciens que les 6 derniers sont coupés à 1 500 caractères, avec une mention que l'outil peut être relancé |
| Réglages | Nouveaux arguments `--context-chars <n>` (taille de l'historique) et `--no-compact` (garder les anciens résultats entiers) |
| Documentation | README : les 34 outils, le routage des images depuis la 0.9.1, le dépannage de la mémoire ; guide de l'utilisateur ajouté dans `docs/` |
| Tests | 224 tests (7 de plus) |

Aucune variable d'environnement ou fichier de configuration n'est ajouté.

## Notes de mise à jour

1. **Le comportement par défaut change.** Un fichier lu il y a plus de 6 résultats d'outils n'est plus renvoyé en entier au modèle : il en reçoit le début (1 500 caractères) et une mention. S'il en a besoin, il relit le fichier. `--no-compact` rétablit l'ancien comportement.
2. **`--native` n'existe plus.** Le mode fenêtre native avait été retiré en 0.5 ; l'argument n'avait plus d'effet et l'aide le présentait encore. Il est retiré du code et de l'aide (changement déjà présent dans le tag 0.9.4, sans note).
3. **`--task <slug>` est documenté** dans `--help` et le README (il existait déjà).

Les fichiers de mémoire, `LLMRoutingRules` et `skills/` ne changent pas.

## 1. Les anciens résultats d'outils sont raccourcis

À chaque étape, CsAgent renvoie au modèle toute la conversation. Les résultats d'outils (un fichier lu, la sortie d'une commande) en sont la plus grosse partie, et un résultat vieux de dix étapes est rarement utile en entier.

- Les **6 derniers** résultats d'outils restent entiers. Tous les résultats de l'étape en cours le restent aussi, même s'ils sont plus de 6.
- Les résultats plus anciens et plus longs que 1 500 caractères sont coupés à 1 500 caractères, suivis de : `[… 8,500 of 10,000 characters left out: older tool result, run the tool again if you need it]`.
- Les résultats courts, les messages de l'utilisateur, ceux de l'assistant et le prompt système ne changent pas.
- L'opération se fait une seule fois par résultat : les étapes suivantes ne le retouchent pas. L'ordre des messages et les identifiants des appels d'outils sont conservés, donc la conversation reste valide pour l'API.
- La conversation enregistrée dans `conversation.json` est aussi plus petite.

## 2. La taille de l'historique se règle

| Argument | Effet |
|---|---|
| `--context-chars <n>` | Taille au-delà de laquelle les plus anciens messages sont retirés de l'historique (96 000 par défaut, minimum 8 000). Plus elle est petite, moins on consomme de jetons d'entrée, mais moins l'agent se souvient de la conversation |
| `--no-compact` | Ne raccourcit pas les anciens résultats d'outils |

Exemple : `csagent --leanui --context-chars 48000`.

La valeur par défaut (96 000) n'a pas changé. Une valeur inférieure au minimum est ramenée à 8 000, et une valeur qui n'est pas un nombre est ignorée.

## 3. README corrigé

- **Images.** Le README disait qu'une image présente dans l'historique imposait le modèle de vision pour la suite de la session. C'est faux depuis la 0.9.1 : seule l'image du message en cours le fait, et pour une question de suivi il faut la joindre de nouveau.
- **Outils.** « Quatre outils intégrés » devient « 34 outils intégrés », en six tableaux avec les paramètres et les outils qui demandent une confirmation.
- **Dépannage.** Un conseil pour développeur sur la mémoire hybride est remplacé par des explications pour l'utilisateur.
- **Arguments.** `--context-chars`, `--no-compact` et `--task` sont ajoutés au tableau ; le défaut de `--max-retries` est corrigé (6, pas 3).
- **Guide.** Une page `docs/guide-csagent.html` donne un guide de l'utilisateur avec sommaire latéral.

## Limites

- Le gain dépend de la session : il n'existe que si des résultats d'outils de plus de 1 500 caractères sont assez anciens. Une session de questions courtes ne change pas.
- Le raccourcissement n'a pas été mesuré sur le vrai serveur Albert, seulement avec un faux serveur.
- Le choix de ne conserver que 6 résultats et 1 500 caractères est fixe : seuls `--context-chars` et `--no-compact` se règlent.
- La publication native (AOT) de cette version n'a pas été essayée dans l'environnement de développement.

## Tests

224 tests (`dotnet run --project Tests -c Release`), dont les 7 nouveaux :

- seuls les anciens résultats longs sont coupés ; les derniers, les courts et les autres rôles ne changent pas ;
- l'opération est sans effet la deuxième fois ;
- une coupure ne sépare jamais une paire de substitution (caractères comme les émojis) ;
- `--context-chars` et `--no-compact` sont lus, avec leurs valeurs par défaut, et leur valeur n'est pas prise pour un nom de mémoire ;
- une valeur trop petite est ramenée au minimum ;
- avec le réglage par défaut, le modèle reçoit les 3 plus anciens résultats coupés et les 6 derniers entiers (9 lectures d'un fichier de 10 000 caractères) ;
- avec `--no-compact`, aucun résultat n'est coupé (le plus ancien est alors retiré par la limite de taille).
