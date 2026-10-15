# CsAgent 0.9.9 — notes de version

La version 0.9.9 étend le support MCP : serveurs locaux en stdio, plusieurs serveurs à la fois, en-têtes d'authentification. Elle fait suite à la 0.9.8.

- [Résumé](#résumé)
- [Notes de mise à jour](#notes-de-mise-à-jour)
- [Brancher des serveurs](#brancher-des-serveurs)
- [Limites](#limites)
- [Tests](#tests)

## Résumé

| Domaine | Ce qui change |
|---|---|
| Transports | Streamable HTTP (comme avant) et **stdio** : CsAgent démarre le programme et échange des messages JSON, un par ligne |
| Plusieurs serveurs | `--mcp` et `--mcp-stdio` sont répétables ; connexion en parallèle ; un serveur en panne donne un avertissement et les autres restent utilisables |
| Authentification | `--mcp-header "Nom: valeur"` ; `${VAR}` est remplacé par la variable d'environnement |
| Configuration | `--mcp-config <fichier>` (ou `CSAGENT_MCP_CONFIG`) : fichier JSON `mcpServers` |
| Tests | 264 tests (17 de plus) |

Nouvelle variable d'environnement : `CSAGENT_MCP_CONFIG`.

## Notes de mise à jour

1. **Un seul serveur : rien ne change.** Ses outils gardent le nom `mcp_<nom>`.
2. **Plusieurs serveurs :** les outils s'appellent `mcp_<serveur>_<nom>`. Le nom du serveur vient du fichier de configuration, ou est déduit de l'URL (hôte) ou du programme (`server-filesystem` pour `npx -y @modelcontextprotocol/server-filesystem`).
3. **Une panne n'arrête plus l'exécution** tant qu'au moins un serveur répond. Avant, un serveur injoignable arrêtait tout.
4. **Aucun chargement automatique.** Un serveur stdio est un programme lancé avec vos droits : seuls `--mcp-stdio` et `--mcp-config`, que vous écrivez vous-même, en démarrent un. Aucun fichier du dossier de travail n'est lu.
5. Les valeurs des en-têtes et de `env` ne sont jamais affichées (bannière, erreurs).
6. Chaque appel MCP demande toujours confirmation, sauf avec `--yes`.

## Brancher des serveurs

```
csagent --mcp https://api.exemple.com/mcp --mcp-header "Authorization: Bearer ${MCP_TOKEN}"
csagent --mcp-stdio "npx -y @modelcontextprotocol/server-filesystem ."
csagent --mcp-config mcp.json
```

```json
{
  "mcpServers": {
    "fichiers": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "."] },
    "wiki":     { "url": "https://wiki.exemple.com/mcp", "headers": { "Authorization": "Bearer ${WIKI_TOKEN}" } }
  }
}
```

- Chaque serveur a soit `url` (et `headers`), soit `command` (et `args`, `env`, `cwd`), jamais les deux.
- Commentaires et virgules finales acceptés dans le fichier.
- `--mcp-header` s'applique aux URL de `--mcp` ; sans URL, c'est une erreur.
- Sous Windows, une commande sans extension (`npx`) passe par `cmd.exe /c`.
- À la fin de l'exécution, CsAgent ferme l'entrée du programme, attend 500 ms, puis arrête le processus et ses enfants.

## Limites

- Outils seulement (ni ressources ni prompts) ; pas d'OAuth, seulement des en-têtes fixes.
- Une demande à un serveur stdio dure 60 secondes au plus ; une demande à la fois par serveur.
- Vérifié avec un faux serveur de test et avec le vrai `@modelcontextprotocol/server-filesystem` (Linux). Pas essayé sous Windows, ni avec ryaker/outlook-mcp, ni en publication native (AOT).

## Tests

264 tests (`dotnet run --project Tests -c Release`), dont les 17 nouveaux (`McpStdioTests.cs`, faux serveur stdio lancé par les tests avec `--mcp-echo`) :

- démarrage, échange et arrêt du programme ;
- lignes parasites et notifications ignorées, requêtes du serveur refusées proprement ;
- serveur qui se ferme, commande introuvable : erreur claire ;
- plusieurs serveurs HTTP et stdio : noms `mcp_<serveur>_<outil>`, appel routé au bon serveur ;
- un serveur en panne : avertissement, les autres restent utilisables ; tous en panne : erreur ;
- en-têtes envoyés à chaque requête, `${VAR}` développé, secrets absents des messages ;
- fichier de configuration : formats valides et erreurs (ni `url` ni `command`, les deux…) ;
- noms déduits de l'URL et du programme.
