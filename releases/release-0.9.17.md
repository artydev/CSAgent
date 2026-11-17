# CsAgent 0.9.17 — notes de version

La version 0.9.17 corrige l'erreur HTTP 400 `'NoneType' object is not an iterator` qui bloquait `gpt-oss-120b` quand la mémoire contenait déjà une conversation. Elle fait suite à la 0.9.16.

- [Résumé](#résumé)
- [Notes de mise à jour](#notes-de-mise-à-jour)
- [Cause](#cause)
- [Comment ça marche](#comment-ça-marche)
- [Limites](#limites)

## Résumé

| Domaine | Ce qui change |
|---|---|
| Correction | L'historique envoyé au modèle est nettoyé : plus de champs `null`, plus de champs propres au serveur |
| Code | `Core/Llm/LlmClient.cs` : nouvelle méthode `SanitizeMessages`, appelée dans `CompleteChatAsync` |

Aucune option de ligne de commande ni variable d'environnement n'est ajoutée.

## Notes de mise à jour

1. **Rien à faire** : il n'est plus nécessaire de vider la mémoire (`conversation.json`) pour utiliser `gpt-oss-120b`.
2. Les fichiers de mémoire existants ne sont pas modifiés. Le nettoyage s'applique à une copie, au moment de l'envoi.
3. Le comportement avec les autres modèles (DeepSeek, etc.) ne change pas.

## Cause

CsAgent enregistre chaque réponse de l'assistant telle que le serveur la renvoie, puis la renvoie au modèle à la requête suivante. Certaines réponses contiennent des champs `null` (`tool_calls`, `content`) ou des champs propres au serveur (`refusal`, `annotations`…). Le serveur de `gpt-oss-120b` (vLLM, format Harmony) parcourt `tool_calls` sans vérifier qu'il existe : un `null` provoque l'erreur 400, dès la première étape, même pour un simple « bonjour ».

## Comment ça marche

Avant chaque requête, `SanitizeMessages` construit une copie de l'historique :

- les valeurs `null` et les tableaux `tool_calls` vides sont supprimés ;
- seuls `role`, `content`, `name`, `tool_calls`, `tool_call_id` et `reasoning_content` (quand il n'est pas `null`) sont conservés ;
- un `content` absent devient `""`.

Le contexte de la conversation (messages, appels d'outils, résultats) est conservé.

## Limites

- Le champ `reasoning`, quand le serveur utilise ce nom à la place de `reasoning_content`, n'est pas renvoyé. Ajoutez-le à `AllowedKeys` si votre serveur l'attend.
- Si l'erreur 400 persiste, la cause est ailleurs (par exemple un schéma d'outil MCP sans `parameters`).
