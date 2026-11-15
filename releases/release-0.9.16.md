# CsAgent 0.9.16 — notes de version

La version 0.9.16 permet de lancer un workflow CSFlow depuis la ligne de commande de CsAgent, sans passer par le modèle. Elle fait suite à la 0.9.15.

- [Résumé](#résumé)
- [Notes de mise à jour](#notes-de-mise-à-jour)
- [Comment ça marche](#comment-ça-marche)
- [Exemple](#exemple)
- [Limites](#limites)
- [Tests](#tests)

## Résumé

| Domaine | Ce qui change |
|---|---|
| Ligne de commande | `--workflow <nom>` lance un workflow de CSFlow ; `--fresh` vide la mémoire des étapes `agent` avant de commencer |
| Code | `Presentation/Web/WorkflowHost.cs` (nouveau) ; `ApiHost.cs` sépare la création du serveur de son démarrage ; `ArgumentParser.cs` lit `--workflow` et `--fresh` ; aide en ligne et traductions françaises |
| Documentation | README, page d'accueil, guide et historique des versions décrivent la commande |
| Tests | 318 tests (+8) : 8 nouveaux dans `WorkflowTests.cs`. Le projet de tests passe au SDK Web et compile le serveur API |

Aucune variable d'environnement n'est ajoutée.

## Notes de mise à jour

1. **Rien à faire** pour les usages existants : sans `--workflow`, le comportement ne change pas.
2. **Pour utiliser `--workflow`** : il faut le `mcp.json` de CSFlow (celui que crée `start_csagent`), avec un serveur MCP nommé `csflow`. L'exécutable compilé `FlowPortal.exe --mcp` suffit.
3. **Port 8080** : `--workflow` démarre son propre serveur `--api` sur ce port, que les étapes `agent` de CSFlow utilisent par défaut. Ne lancez pas en même temps le worker de `start_csagent`, ou donnez `--port` à l'un des deux.
4. **Projet de tests** : `Tests/CsAgent.Tests.csproj` utilise `Microsoft.NET.Sdk.Web` et compile `Infrastructure/` et le serveur API. Rien à faire de plus si vous compilez avec Visual Studio ou `dotnet`.

## Comment ça marche

CsAgent démarre le serveur MCP nommé `csflow` de `--mcp-config` (`FlowPortal.exe --mcp`), puis son propre serveur `--api` pour les étapes `agent`. Il appelle `csflow_run` puis `csflow_status` jusqu'à la fin du workflow. Chaque étape s'affiche une fois, quand elle se termine (`✓` réussie, `✗` échouée, `-` ignorée), suivie du début de sa sortie. À la fin, la sortie de la dernière étape est écrite, puis les deux serveurs sont arrêtés.

| Code de sortie | Sens |
|---|---|
| `0` | le workflow s'est terminé |
| `1` | le workflow a échoué (étape et raison sur la sortie d'erreur), ou Ctrl+C |
| `2` | il n'a pas démarré : pas de serveur `csflow`, workflow introuvable ou invalide, port 8080 déjà pris, ou étape `human_approval` / `user_input` |

Valeurs par défaut posées par `--workflow` : port 8080, mémoire `csflow_worker`, sans distillation. Une mémoire nommée avec `--mem` ou en argument est respectée. `--port`, `--api-key`, `--yes`, `--model`, `--endpoint` s'appliquent comme en mode `--api`.

`--fresh` supprime, avant de commencer, les quatre fichiers de la mémoire (`conversation.json`, `exact.json`, `semantic.json`, `summary.json`), jamais le dossier. Sans `--workflow`, l'option ne fait rien.

## Exemple

```
csagent --mcp-config mcp.json --workflow demo_report --yes
```

```
--- CSAgent for the agent steps: http://localhost:8080 ---
  ✓ preparer (shell)  3 fichiers lus
  ✓ analyser (agent)  Le rapport est écrit dans tmp/rapport.md
Workflow complete.
```

## Limites

- **Essayé sous Windows et sous Linux** : sous Windows avec CSFlow, sous Linux de bout en bout avec le serveur MCP de CSFlow lancé depuis les sources (`python3 app.py --mcp`).
- **Workflows avec validation humaine refusés** : CSFlow ne lance pas par MCP un workflow qui contient `human_approval` ou `user_input` ; il faut le lancer depuis son interface.
- **Un seul workflow à la fois**, parmi les fichiers du dossier `workflows/` de CSFlow.
- **Mémoire partagée** : tous les workflows partagent la mémoire `csflow_worker` et elle se conserve d'un lancement à l'autre ; `--fresh` ou `--mem` pour l'isoler.
- Le dossier de travail de CsAgent doit être celui de CSFlow pour que les fichiers produits par les étapes s'y retrouvent.

## Tests

318 tests (`dotnet run --project Tests -c Release`), dont 8 nouveaux dans `WorkflowTests.cs` : analyse des options et valeurs par défaut, `--fresh` (seuls les quatre fichiers sont supprimés), absence de serveur `csflow`, déroulement complet avec un faux serveur CSFlow (étapes affichées une fois chacune et dans l'ordre, serveur `--api` actif pendant l'exécution puis arrêté), échec, workflows refusés, messages français.
