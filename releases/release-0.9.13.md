# CsAgent 0.9.13 — notes de version

La version 0.9.13 permet à CsAgent de passer derrière un proxy d'entreprise qui demande une authentification Windows (erreur HTTP 407). Tous les accès HTTP de l'agent sont désormais créés au même endroit. Elle fait suite à la 0.9.12.

- [Résumé](#résumé)
- [Notes de mise à jour](#notes-de-mise-à-jour)
- [Le problème](#le-problème)
- [Le correctif](#le-correctif)
- [Sous Linux et macOS](#sous-linux-et-macos)
- [Limites](#limites)
- [Tests](#tests)

## Résumé

| Domaine | Ce qui change |
|---|---|
| Proxy d'entreprise | Les identifiants Windows de la session sont donnés au proxy (`DefaultProxyCredentials`) : un proxy qui répond 407 ne bloque plus l'agent |
| Code | Un seul endroit crée les clients HTTP : `HttpClients.Create(délai)` dans `Shared/HttpClients.cs`. Le client LLM, le catalogue de modèles, `download_file`, les outils réseau, système et audio, et le transport MCP en HTTP l'utilisent |
| Tests | 288 tests (3 de plus) |

Aucun argument, variable d'environnement ni fichier de configuration n'est ajouté. Les délais d'attente ne changent pas.

## Notes de mise à jour

1. **Rien à faire** : sans proxy, ou avec un proxy qui n'exige pas d'authentification, le comportement est identique.
2. Les identifiants vont **au proxy seulement**, jamais aux serveurs : `UseDefaultCredentials` n'est volontairement pas utilisé, car il enverrait ces identifiants à tout serveur qui les demande.
3. Un code qui crée lui-même un `new HttpClient(` échoue désormais au test « no source file makes an HttpClient by itself any more » : il faut passer par `HttpClients.Create`.

## Le problème

Derrière un proxy d'entreprise qui exige l'authentification intégrée de Windows, chaque requête de CsAgent (modèle, pages web, téléchargement, serveurs MCP, audio) recevait une erreur 407 « Proxy Authentication Required », parce que le client HTTP ne présentait aucune identité au proxy. Les clients étaient en outre créés à neuf endroits différents : corriger l'un d'eux laissait les autres en échec.

## Le correctif

- `HttpClients.Create(délai)` fabrique le client avec `DefaultProxyCredentials = CredentialCache.DefaultCredentials` : l'identité de la session Windows est présentée au proxy.
- Les neuf endroits qui créaient un client directement passent par cette fonction.
- Un test vérifie que plus aucun fichier du code de l'application ne crée un client par lui-même.

## Sous Linux et macOS

- Le proxy est choisi par les variables `HTTP_PROXY`, `HTTPS_PROXY` et `NO_PROXY` (pas par un réglage du système).
- Pour un proxy qui demande un identifiant et un mot de passe, le plus simple est de les mettre dans la variable : `HTTPS_PROXY=http://utilisateur:motdepasse@proxy:3128`.
- L'authentification intégrée (Kerberos) ne fonctionne que si un ticket valide existe (`kinit`) et que la bibliothèque `libgssapi` est installée. Sinon, aucune identité n'est envoyée : l'erreur 407 reste, sans autre effet.

## Limites

- **Pas essayé avec un vrai proxy d'entreprise** : l'environnement de développement est sous Linux, sans domaine ni proxy authentifié. Les tests couvrent la configuration du client et un échange avec un serveur local, pas l'authentification elle-même. À vérifier sur une machine Windows de domaine.
- Un proxy qui demande un identifiant et un mot de passe distincts de la session Windows n'est pas pris en charge sous Windows (aucun argument pour les saisir) ; sous Linux et macOS, mettez-les dans `HTTPS_PROXY`.
- Les serveurs MCP lancés en local (`--mcp-stdio`) ne passent pas par HTTP : ils ne sont pas concernés.

## Tests

288 tests (`dotnet run --project Tests -c Release`), dont les 3 nouveaux (`HttpClientsTests.cs`) :

- le gestionnaire donne les identifiants par défaut au proxy et ne les donne pas aux serveurs ;
- `Create` applique le délai d'attente, et le client joint un serveur local ;
- aucun fichier du code de l'application ne crée un `HttpClient` par lui-même.
