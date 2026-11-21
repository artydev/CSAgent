# CsAgent 0.9.20 — notes de version

La version 0.9.20 s'occupe de l'erreur `API 400: This model's maximum context length is 131072 tokens…` : quand la requête dépasse la fenêtre du modèle, l'historique est raccourci et la requête est renvoyée, au lieu d'arrêter la tâche. Les images des tours précédents ne restent plus dans l'historique. Elle fait suite à la 0.9.19.

- [Résumé](#résumé)
- [Notes de mise à jour](#notes-de-mise-à-jour)
- [Cause](#cause)
- [Comment ça marche](#comment-ça-marche)
- [Limites](#limites)
- [Tests](#tests)

## Résumé

| Domaine | Ce qui change |
|---|---|
| Fiabilité | Une erreur 400 « maximum context length » raccourcit l'historique et renvoie la requête, 2 fois au plus |
| Images | Seul le dernier message contenant des images les garde ; les images plus anciennes deviennent une note de texte |
| Code | `Shared/ContextRecovery.cs` (nouveau), `Core/Llm/LlmClient.cs` (reprise + événement `ContextShrunk`), `Core/Agent/CodingAgent.cs`, `Core/Agent/ContextBudget.cs` (`KeepImages`) |
| Tests | 338 tests (+7) : 7 nouveaux dans `Tests/ContextRecoveryTests.cs` |

Aucune option de ligne de commande ni variable d'environnement n'est ajoutée. `--no-compact` désactive aussi la suppression des anciennes images.

## Notes de mise à jour

1. **Rien à faire** : il n'est pas nécessaire de vider la mémoire (`conversation.json`) ni de changer de dossier `--mem`.
2. Les images des tours précédents sont remplacées par une note de texte dans l'historique et dans le fichier de conversation enregistré. Seule la dernière image reste envoyée au modèle.
3. Quand la reprise a lieu, un avertissement s'affiche : `Context too long for the model (… > … tokens): older history shortened (… characters removed), retrying.`

## Cause

Le budget `--context-chars` (96 000 caractères par défaut) ne compte que le texte des messages. Ce qui est envoyé en plus n'y est pas compté :

- les images : un message qui en contient n'est jamais retiré par `TrimHistory`, et son contenu en base64 est très long ;
- les arguments des appels d'outils (par exemple le contenu entier d'un `write_file`), que `CompactToolResults` ne raccourcit pas non plus ;
- les définitions d'outils et les blocs de mémoire.

Une longue session pouvait donc dépasser les 131 072 jetons du modèle. Le serveur répondait par une erreur 400 et la tâche s'arrêtait.

## Comment ça marche

`LlmClient` reconnaît la réponse 400 (« maximum context length », `context_length_exceeded`) et lit la limite et la taille réelle quand elles y figurent. Il appelle alors `ContextRecovery.Shrink` sur l'historique, qui est modifié sur place pour que l'étape suivante parte aussi d'un historique plus petit, puis renvoie la requête. Ces essais ne comptent pas dans ceux de la limite de débit 429.

Première passe (cible : environ 65 % de la fenêtre) :

1. les images, sauf celles du dernier message qui en contient, deviennent une note de texte ; l'image restante passe en `detail: "low"` ;
2. les anciens résultats d'outils sont coupés à 1 500 caractères (les 2 derniers restent entiers) ;
3. les arguments des anciens appels d'outils sont raccourcis, en restant du JSON valide ;
4. si nécessaire, les plus anciens messages sont retirés. Le message système, la première demande de l'utilisateur et les derniers messages sont gardés, et les paires appel/résultat d'outil sont réparées.

Deuxième passe (cible : environ 40 %) : toutes les images sont retirées et les résultats d'outils sont coupés à 400 caractères.

En plus, à la fin de chaque étape, les images sont retirées de tous les messages sauf le dernier qui en contient (`ContextBudget.KeepImages`, désactivé par `--no-compact`). Un message sans image retrouve un `content` en texte simple, que les modèles sans vision acceptent.

Si l'erreur persiste après 2 reprises, l'erreur `API 400` d'origine est affichée comme avant.

## Limites

- La cause n'est pas supprimée : le budget ne compte toujours pas les images, les arguments d'outils ni les définitions d'outils. Un dépassement peut encore se produire ; il est maintenant rattrapé.
- Une image n'est ni redimensionnée ni réencodée : elle est gardée ou retirée. Une seule image très grande peut dépasser la fenêtre ; la deuxième passe la retire.
- La dernière image reste dans l'historique jusqu'à ce qu'une nouvelle image arrive.
- Le champ `detail: "low"` est celui de l'API OpenAI. Un serveur qui l'ignore n'en tire simplement aucune économie.
- Les messages retirés ne sont plus envoyés au modèle. La mémoire hybride (`[MEMORY]`) et le résumé de session (`[SESSION CONTEXT]`) en gardent l'essentiel.

## Tests

338 tests (+7) : 7 nouveaux dans `Tests/ContextRecoveryTests.cs`.

- l'erreur 400 est reconnue avec sa limite (131 072) et sa taille réelle ; une autre erreur 400 ne l'est pas ;
- avec un serveur qui refuse une première fois, l'historique est raccourci sur place et la deuxième requête réussit ;
- avec un serveur qui refuse toujours, il y a 2 reprises au plus, puis l'erreur `API 400` ;
- `Shrink` laisse des arguments d'outils en JSON valide et des paires appel/résultat cohérentes ;
- seule l'image la plus récente est gardée, et un message sans image redevient du texte simple ;
- la première passe garde la dernière image en `detail: "low"`, la deuxième les retire toutes ;
- avec deux images de 200 000 caractères, la requête de reprise est nettement plus petite (moins de 60 %).
