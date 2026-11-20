# CsAgent 0.9.19 — notes de version

La version 0.9.19 s'occupe de l'erreur `✗ API error: An error occurred while sending the request.` : une coupure de connexion est maintenant retentée automatiquement, et le message final dit ce qui s'est passé. Elle répare aussi l'Explorateur de tests de Visual Studio, qui échouait depuis la 0.9.18. Elle fait suite à la 0.9.18.

- [Résumé](#résumé)
- [Notes de mise à jour](#notes-de-mise-à-jour)
- [Cause](#cause)
- [Comment ça marche](#comment-ça-marche)
- [Limites](#limites)
- [Explorateur de tests](#explorateur-de-tests)
- [Tests](#tests)

## Résumé

| Domaine | Ce qui change |
|---|---|
| Fiabilité | Une connexion perdue avant toute réponse (réinitialisée, fermée par le serveur ou un proxy) est retentée 2 fois, après 2 s puis 4 s |
| Message d'erreur | Il nomme le serveur, le nombre d'essais, la chaîne complète des causes et une piste de vérification |
| Code | `Core/Llm/LlmClient.cs` (nouvelle méthode `DescribeNetworkError`), `Core/Llm/RetryPolicy.cs` (`ConnectionRetries`, `ConnectionDelayMs`) |
| Tests | 331 tests (+7) : 7 nouveaux dans `Tests/ConnectionRetryTests.cs` |
| Explorateur de tests | `Tests/TestKit.cs` : le groupe `ContextBudgetGroup` n'est plus lancé deux fois (voir ci-dessous) |

Aucune option de ligne de commande ni variable d'environnement n'est ajoutée.

## Notes de mise à jour

1. **Rien à faire** : la mémoire et les réglages existants sont inchangés.
2. Un délai dépassé (5 minutes sans réponse) n'est pas retenté : attendre à nouveau aussi longtemps n'aiderait pas. Le message l'indique et propose de démarrer une nouvelle conversation.
3. Les erreurs HTTP (400, 401, 500…) et la limite de débit 429 gardent leur comportement habituel.

## Cause

Quand la connexion échoue avant qu'une réponse complète n'arrive, .NET lève une exception dont le message est seulement « An error occurred while sending the request. ». La vraie cause (connexion réinitialisée, fermée prématurément, nom introuvable, TLS…) se trouve dans l'exception interne, que CsAgent n'affichait pas.

Avec un modèle qui réfléchit longtemps (gpt-oss derrière Albert), la requête reste sans réponse pendant un moment. Un historique volumineux allonge ce silence, et le serveur ou un proxy peut couper la connexion.

## Comment ça marche

- La requête est renvoyée jusqu'à 2 fois quand la connexion est coupée, après 2 s puis 4 s. Ces essais ne consomment pas les essais prévus pour la limite de débit 429.
- Si l'échec persiste, le message a la forme :
  `Connection to albert.api.etalab.gouv.fr failed after 3 attempts: An error occurred while sending the request. -> The response ended prematurely. The server or a proxy closed the connection without answering: server overloaded, or request too large or too slow. Try again, or start a new conversation.`
- Une annulation demandée par l'utilisateur (Ctrl+C) n'est jamais retentée.

## Limites

- Si le serveur est réellement indisponible, l'erreur apparaît après environ 6 secondes de plus.
- Une requête renvoyée après une coupure au milieu d'une génération peut être facturée deux fois par le fournisseur.
- Le message reste en anglais, comme les autres erreurs d'API.

## Explorateur de tests

La 0.9.18 a ajouté un second appel à `ContextBudgetGroup()` au début de `RunGroups()` dans `Tests/TestKit.cs`, alors que le groupe était déjà lancé plus bas. L'Explorateur de tests de Visual Studio refuse deux tests de même identifiant (`Duplicate test id`) : le test `Run` échouait avec « Exception has been thrown by the target of an invocation ». Le doublon est retiré. Le programme console passait quand même, car il lance simplement les groupes dans l'ordre.

Le nombre de tests est donc de 324 à la 0.9.18 (la note de cette version annonçait 331, doublons compris), puis de 331 avec les 7 nouveaux.

## Tests

331 tests (+7) : 7 nouveaux dans `Tests/ConnectionRetryTests.cs`.

- un serveur qui coupe les 2 premières connexions : le 3e essai réussit ;
- un serveur qui coupe toujours : message avec le serveur, le nombre d'essais et la cause, exception d'origine conservée ;
- `ConnectionRetries = 0` : un seul essai ;
- une erreur HTTP 500 n'est pas une connexion perdue : une seule requête ;
- une annulation par l'appelant n'est pas retentée ;
- `DescribeNetworkError` donne la chaîne des causes et une piste pour les cas courants ;
- les nouvelles valeurs de `RetryPolicy` sont validées.
