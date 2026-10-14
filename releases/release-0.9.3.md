# CsAgent 0.9.3 — notes de version

La version 0.9.3 améliore la réaction de CsAgent aux limites de débit de l'API (erreurs HTTP 429) et affiche les tokens consommés. Elle fait suite à la 0.9.2.

- [Résumé](#résumé)
- [Notes de mise à jour](#notes-de-mise-à-jour)
- [1. Le quota du jour épuisé n'est plus réessayé](#1-le-quota-du-jour-épuisé-nest-plus-réessayé)
- [2. Pas de résumé de session après un 429](#2-pas-de-résumé-de-session-après-un-429)
- [3. Les tokens consommés sont affichés](#3-les-tokens-consommés-sont-affichés)
- [Limites](#limites)
- [Tests](#tests)

## Résumé

| Domaine | Ce qui change |
|---|---|
| Erreur 429 | CsAgent lit le message de l'API : une limite **par minute** est réessayée comme avant, une limite **par jour** ne l'est plus et s'arrête tout de suite avec une explication |
| Résumé de session | L'appel de distillation n'est plus lancé après un 429 (il serait refusé aussi et consommerait du quota) |
| Console | Une ligne en fin de tour indique les tokens du tour et de la session : `[tokens: this turn in 23,412 / out 310 · session in … / out …]` |
| Messages | Les nouveaux messages existent en français et en anglais (`--lang`) |
| Tests | 207 tests (10 de plus) |

Aucune option, variable d'environnement ou fichier de configuration n'est ajouté. `--max-retries` et `--retry-delay` gardent leur effet pour les limites par minute.

## Notes de mise à jour

1. **La console affiche une nouvelle ligne après chaque tour**, juste après `✓ Task complete.` : `[tokens: …]`. Un script qui lit la sortie de `csagent --prompt` doit l'ignorer ou la filtrer. Elle n'apparaît pas avec `--quiet`, et seulement si l'API renvoie ses comptes de tokens (le bloc `usage` des réponses).
2. **Un quota du jour épuisé arrête l'agent au premier 429.** Avant, il refaisait jusqu'à 6 essais (environ une minute) avant d'échouer avec le même message.
3. **Pour les développeurs de l'API interne** : l'échec final d'un 429 est maintenant une `RateLimitException` (qui reste une `HttpRequestException`, avec le même message `API 429: …`), et `IAgentObserver` a une nouvelle méthode `OnUsage` avec une implémentation par défaut : un observateur existant n'a rien à changer.

Les fichiers de mémoire, les arguments de la ligne de commande, `LLMRoutingRules` et `skills/` ne changent pas.

## 1. Le quota du jour épuisé n'est plus réessayé

Un fournisseur peut limiter les tokens d'entrée par minute et par jour, par exemple :

```
API 429: {"detail":"128000 input tokens per minute exceeded (remaining: 0)."}
API 429: {"detail":"1280000 input tokens per day exceeded (remaining: 0)."}
```

| Limite reconnue | Ce que fait CsAgent |
|---|---|
| **Par minute** (`per minute`, `per min`, `TPM`, `RPM`) | Réessaie avec le délai exponentiel et l'en-tête `Retry-After`, comme avant (6 essais par défaut) |
| **Par jour** (`per day`, `daily`, `TPD`, `RPD`) | S'arrête tout de suite et explique : le quota ne reviendra pas avant sa remise à zéro |
| Autre | Traitée comme une limite passagère : mêmes essais qu'une limite par minute |

Si les deux mots apparaissent dans le message, la limite du jour l'emporte. Le message affiché est : « Le quota de jetons du jour est épuisé : réessayer ne sert à rien avant sa remise à zéro. Attends la remise à zéro, ou utilise un autre modèle (`--model`) ou un autre serveur (`--endpoint`). » Si une limite par minute persiste après tous les essais, un autre message le dit.

## 2. Pas de résumé de session après un 429

À la fin d'un tour, CsAgent envoie un dernier appel au modèle pour mettre à jour le résumé de session (la distillation). Après un 429, cet appel n'est plus tenté : il serait refusé de la même façon et consommerait du quota. Les deux couches de mémoire sont quand même enregistrées, un avertissement le signale, et le résumé précédent est conservé.

## 3. Les tokens consommés sont affichés

En fin de tour, la console affiche, d'après les chiffres que l'API renvoie :

```
[tokens: this turn in 23,412 / out 310 · session in 140,221 / out 2,904]
```

- **this turn** : les tokens d'entrée et de sortie de ce tour, appel de résumé compris ;
- **session** : le total depuis le lancement de `csagent`.

Les tokens d'entrée sont ceux qui comptent pour un quota : chaque étape de l'agent renvoie tout l'historique de la conversation, d'où l'intérêt de voir le total monter. La ligne n'apparaît pas si le serveur n'envoie pas de comptes. En français : `[jetons : ce tour entrée … / sortie … · session entrée … / sortie …]`.

## Limites

- Le quota du jour est reconnu à la formulation du message de l'API. Un fournisseur qui l'écrit autrement est traité comme une limite passagère (les essais habituels).
- L'affichage des tokens existe dans la console seulement, pas dans `--ui`, `--leanui` ni `--api`. Les nouveaux messages d'erreur 429, eux, s'y affichent comme des avertissements.
- Cette version ne réduit pas la consommation : la taille de l'historique envoyé à chaque étape (jusqu'à 96 000 caractères) n'a pas changé. Elle aide à la voir et à réagir plus vite.
- Vérifié avec de faux serveurs, pas avec le vrai endpoint Albert.
- La publication native (AOT) de cette version n'a pas été essayée dans l'environnement de développement.

## Tests

207 tests (`dotnet run --project Tests -c Release`), dont les 10 nouveaux :

- la reconnaissance du type de limite (formulations Albert et OpenAI, limite du jour prioritaire) ;
- un 429 du jour n'est pas réessayé : une seule requête, `RateLimitException` ;
- un 429 par minute est réessayé puis réussit ; s'il ne passe jamais, l'échec arrive après le nombre d'essais prévu ;
- une autre erreur (500) reste une erreur simple, sans nouvel essai ;
- le client additionne les tokens renvoyés par l'API, et compte zéro sans `usage` ;
- l'agent s'arrête au 429 du jour, l'explique, et n'appelle pas le modèle de résumé ;
- l'agent rapporte les tokens du tour une seule fois, avec le total de session ;
- les messages sont traduits en français.

Le comportement a aussi été vérifié à la main avec l'application compilée et un faux serveur : la réponse du jour arrive immédiatement, en français comme en anglais, et la ligne de tokens s'affiche.