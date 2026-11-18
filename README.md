# FlowPortal — portail de workflows pour CSFlow 

> Un portail local où chaque workflow est une carte : on le lance en un clic, on lit et on modifie son YAML,
> on retrouve le résultat de chaque lancement. FlowPortal repose sur le moteur de **CSFlow** (Python × CSAgent),
> dont toute la documentation ci-dessous reste valable.

## État du projet

| Élément | État |
|---|---|
| Moteur, nœuds, client et serveur MCP, interface CSFlow | repris de CSFlow (commit `625a317` et PR #9) |
| `portal.py` : cartes, prérequis, lecture et écriture validée du YAML, historique des lancements | **fait**, testé (`tests/test_portal.py`) |
| Exécutable compilé : `FlowPortal.exe` (Windows) / `FlowPortal` (Linux), à la place de `CSFlow.exe` | **fait** (scripts de build et de démarrage, exemples `mcp.*.example`) |
| Page du portail (`ui/portal.html`), ouverte par défaut (`--classic` pour l'ancienne interface) : cartes, lancement avec paramètres, validations et saisies, YAML (lecture, édition, plein écran), résultat de chaque lancement | **fait** |
| `engine.py` : `WorkflowEngine(..., inputs={...})` remplace les valeurs du bloc `inputs:` | **fait**, testé |
| Création d'un workflow depuis le portail (à la main, ou décrite à CSAgent par la voix ou au clavier) et modification assistée d'un workflow existant (`assist.py`) | **fait**, testé (`tests/test_assist.py`) |

### Lancer le portail

| Windows (exe compilé) | Linux (exe compilé) | Depuis les sources |
|---|---|---|
| `FlowPortal.exe` | `./FlowPortal` | `python app.py` |

Le portail s'ouvre par défaut. Pour l'interface classique de CSFlow, ajoutez `--classic` (`FlowPortal.exe --classic`,
`python app.py --classic`) ; elle reste aussi accessible depuis le portail (« Interface classique »).
Un seul workflow s'exécute à la fois. Le bouton **Arrêter** interrompt tout de suite un script `python`, une commande `shell`
(avec leurs processus fils) ou une étape CSAgent, et libère une validation ou une saisie en attente ; les autres étapes
(HTTP, MCP…) se terminent d'abord, puis le workflow s'arrête. Le lancement est enregistré avec le statut « Arrêté ».
Une étape CSAgent arrêtée coupe seulement la connexion du portail : le worker CSAgent peut poursuivre son travail de son côté. L'historique est écrit dans `runs.jsonl`, à côté de l'exécutable (200 lancements au plus ;
les valeurs des variables d'environnement qui ressemblent à des secrets, comme `GOAL_API_KEY`, y sont masquées).
Depuis le portail, l'édition d'un workflow est validée par le moteur avant l'écriture, et l'ancienne version est gardée en `.bak`.
Une étape `user_input` avec `voice: true` propose le bouton **Dicter** (prise de note vocale) : le navigateur enregistre,
CSAgent transcrit (worker `--api` démarré, comme dans l'interface classique), et le texte s'ajoute à la zone de saisie pour être relu et corrigé avant validation.
Le micro n'est accessible que si la page est ouverte en `localhost`, ce qui est le cas du portail.

### Liste, modification, copie, renommage et suppression

Le bouton **Vue liste** remplace les cartes par un tableau (workflow, nombre d'étapes, dernier lancement, prérequis manquants) avec, pour chaque ligne, **Lancer**, **Modifier** (ouvre directement l'éditeur plein écran), **Dupliquer**, **Renommer** et **Supprimer**. La recherche et les filtres s'appliquent aux deux vues, et le choix de la vue est retenu par le navigateur.
**Dupliquer** copie le workflow sous un nouveau nom (proposé : `<nom>-copie`) et met à jour son `name:` pour que les deux cartes se distinguent ; **Renommer** change le nom du fichier (sa copie `.bak` et l'historique des lancements suivent ; le `name:` est mis à jour seulement s'il était identique à l'ancien nom). Ces deux actions existent aussi dans le panneau d'un workflow ; renommer est refusé pendant un lancement.
**Supprimer** (également dans le panneau d'un workflow) demande une confirmation puis **déplace** le fichier dans `workflows/.corbeille/` (avec la date dans le nom, et sa copie `.bak`) : rien n'est effacé définitivement, on le récupère en le recopiant à la main dans `workflows/`. L'historique des lancements est conservé. La suppression est refusée pendant un lancement ou une demande à l'assistant. Ajoutez `workflows/.corbeille/` à votre `.gitignore`.

### Créer et modifier un workflow depuis le portail

Le bouton **+ Nouveau workflow** demande un nom de fichier (`.yaml` ajouté si besoin, refus si le nom existe déjà) puis un point de départ :
un squelette vide, un workflow à une étape, un workflow avec validation humaine, ou **Décrire à CSAgent**. Le workflow s'ouvre dans l'éditeur plein écran,
**sans être enregistré** : le fichier n'est créé que par **Enregistrer**, après la validation habituelle du moteur. Fermer l'éditeur abandonne le brouillon.

L'onglet **Assistant** de l'éditeur (également accessible par le bouton « Assistant » de l'éditeur du panneau) sert aussi bien à créer qu'à modifier un workflow existant :

1. Écrivez la demande (« ajoute une étape qui écrit le résultat dans un fichier ») ou cliquez sur **Dicter** (micro, transcription par CSAgent) ; relisez, puis **Envoyer à CSAgent** (Ctrl + Entrée).
2. CSAgent travaille sur une **copie** du YAML (`tmp/assist/<id>/draft.yaml`), jamais sur le fichier du workflow. Si le résultat ne passe pas la validation, le portail lui renvoie les erreurs (deux corrections au plus).
3. Vous recevez une **proposition** avec les lignes ajoutées et supprimées. **Appliquer dans l'éditeur** remplace le texte de l'éditeur (toujours non enregistré) ; **Rejeter** l'abandonne. Si vous avez modifié le texte entre-temps, un second clic confirme l'écrasement.
4. Les échanges restent visibles tant que l'éditeur est ouvert ; la fermeture efface l'historique de la conversation et les brouillons de `tmp/assist`.

Conditions : un worker CSAgent joignable (`--api`, comme pour une étape `agent`) ; la dictée demande en plus le micro (page ouverte en `localhost`). Le résultat d'un LLM varie : relisez toujours le diff.
Si CSAgent demande une confirmation (`Auto-approve: OFF`), elle s'affiche dans le panneau de l'assistant. **Arrêter** coupe la connexion avec CSAgent (le worker peut poursuivre de son côté).
L'assistant et les lancements de workflows ne s'exécutent pas en même temps.

`portal.py` n'a aucune dépendance au navigateur : il peut être testé et utilisé sans interface.

| Fonction | Rôle |
|---|---|
| `Portal.list_cards()` | un dictionnaire par workflow : titre, résumé, entrées, étapes, étiquettes (CSAgent, validation humaine, MCP), prérequis manquants, dernier lancement |
| `Portal.check_yaml(texte)` / `save_yaml(nom, texte)` | validation par `schema.check`, refus s'il y a des erreurs, copie `.bak`, écriture atomique, nom de fichier contrôlé |
| `Portal.recorder(nom, inputs)` | transforme les événements du moteur en un enregistrement (`runs.jsonl`, 200 lancements au plus, secrets masqués) |
| `Portal.runs()` / `get_run(id)` | historique, du plus récent au plus ancien |

Les prérequis d'une carte sont déduits des étapes `mcp_tools` / `mcp_call` : serveur absent de `mcp.json`, ou variable
`${VAR}` de ce serveur non définie dans l'environnement. Les clés ne sont jamais lues ni affichées, seulement leur présence.

---

# CSFlow — Python × CSAgent

> Automatisation de workflows : Python enchaîne les étapes, CSAgent s'occupe du raisonnement. 

---

## Démarrage rapide 

CSFlow tourne sous **Windows** et sous **Linux / WSL**. Les commandes sont données pour les deux ;
sauf mention contraire, le reste de ce document vaut pour les deux systèmes.

**1. Installer les dépendances** (Python 3.10 ou plus récent ; inutile avec l'exe compilé)

| Windows | Linux / WSL |
|---|---|
| `pip install -r requirements.txt` | `pip install -r requirements.txt` (ou `sudo apt install python3-yaml` pour le seul mode MCP) |

Sur les distributions Linux récentes, `pip` refuse d'installer hors d'un environnement virtuel :
`python3 -m venv .venv && . .venv/bin/activate`, puis la commande ci-dessus.

**2. Lancer CSAgent** *depuis le dossier de CSFlow* (CSAgent n'écrit que dans son dossier courant ;
`start_agent` doit être placé à côté de l'exécutable — la compilation l'y copie dans `build/`)

| Windows | Linux / WSL |
|---|---|
| `set ALBERT_API_KEY=ta-cle` | `export ALBERT_API_KEY=ta-cle` |
| `start_agent.bat` | `./start_agent.sh` |

Les deux scripts démarrent CSAgent en mode API (port 8080) **puis** CSFlow, et arrêtent CSAgent à la
fermeture de CSFlow. Variables facultatives : `MODEL` (nom du modèle ; sinon routage automatique),
`PORT`, `API_KEY`, `CSAGENT_BIN`.

**3. Lancer un workflow**

Interface graphique (EEL, ouvre un navigateur : Chrome ou Edge sous Windows, tout navigateur sous Linux) :

```
python app.py          (Windows)          python3 app.py          (Linux / WSL)
```

L'interface comprend : une barre latérale (workflows et étapes avec leur état), le **graphe** du workflow
(calculé depuis `next` / `on_approve` / `on_reject`), le **détail de l'étape** (onglets Détails,
Entrées / Sorties, Historique), l'**éditeur YAML** (l'étape choisie est surlignée) et les **événements**
d'exécution. Au-dessus de 1400 px de large, YAML et événements sont côte à côte ; en dessous, ils
passent en onglets.

Accessibilité : texte de 13 à 15 px minimum (en `rem`, il suit la taille de texte du navigateur),
contrastes WCAG AA, état toujours indiqué par un libellé et un symbole (jamais la couleur seule),
navigation complète au clavier (la liste des étapes remplace le graphe), anneau de focus visible,
bouton « Approuver » qui prend le focus à la demande de validation, animations coupées si le système
demande « réduire les animations ».

ou en ligne de commande, sans interface (Windows : `python engine.py workflows\demo_report.yaml` ;
Linux : `python3 engine.py workflows/demo_report.yaml`) :

```
python engine.py workflows/demo_autofix.yaml
python engine.py workflows/demo_report.yaml
python engine.py workflows/demo_code_review.yaml
```

### Sous Linux

Même procédure, les scripts `.sh` font le travail des `.bat` — et **sans binaire** :
`python3 engine.py …` et `python3 app.py --mcp` remplacent l'exécutable `CSFlow`.

```bash
pip install -r requirements.txt
export ALBERT_API_KEY=ta-cle
./start_agent.sh                        # csagent --api puis l'interface (python3 app.py)
python3 engine.py workflows/demo_autofix.yaml
```

- `csagent` doit être dans le PATH (`.NET 10`, multiplateforme : binaire publié ou `dotnet`).
- Machine sans navigateur (serveur, SSH) : `CSFLOW_NO_BROWSER=1 python3 app.py` —
  l'URL s'affiche, vous l'ouvrez dans le navigateur de votre choix.
- Mode MCP : sans binaire à côté, `./start_csagent.sh` câble `mcp.json` en mode sources
  (`python3 app.py --mcp`, voir `mcp.linux.source.example`).

**Navigateur sous Linux / WSL.** CSFlow choisit dans cet ordre : un navigateur Chromium (`chromium`,
`google-chrome`, `microsoft-edge`, `brave-browser`…) en fenêtre d'application ; sous **WSL**, le navigateur de
Windows (`wslview` ou `explorer.exe`, `localhost` étant relayé vers WSL2) ; Firefox ; sinon le navigateur par
défaut, **sauf Epiphany / GNOME Web**, qui affiche « Page Unresponsive ». Si rien ne convient, CSFlow affiche
l'adresse `http://localhost:<port>/index.html` et la marche à suivre (`sudo apt install chromium`).
`CSFLOW_NO_BROWSER=1` n'ouvre rien : à vous d'ouvrir l'adresse ; `CSFLOW_PORT` fixe le port.

### Compiler un exécutable unique

| Windows | Linux / WSL |
|---|---|
| `build_nuitka.bat` → `build\FlowPortal.exe` | `./build_nuitka.sh` → `build/FlowPortal` |
| Prérequis : `pip install nuitka -r requirements.txt` et un compilateur C | Prérequis : `sudo apt install gcc patchelf`, puis `pip install nuitka -r requirements.txt` |

L'exécutable embarque l'interpréteur Python : aucun Python n'est à installer sur le poste qui l'utilise.
Il pèse environ 14 Mo sous Windows et 50 Mo sous Linux.

### Conventions des workflows (Windows et Linux)

- Les chemins sont **relatifs** au dossier CSFlow/ (`tmp/sales.csv`), jamais absolus (`C:\temp\...`, `/home/...`) :
  l'agent ne peut pas écrire en dehors de son dossier courant.
- Séparez les chemins par « / » : `tmp/sales.csv` fonctionne sur Windows comme sur Linux
  (un backslash dans un chemin devient un caractère du nom de fichier sous Linux).
- Les commandes `shell` tournent sous `cmd.exe` sur Windows et `bash` sous Linux :
  préférez des commandes portables (`git`, `python`) ; pour forcer un shell précis,
  utilisez le paramètre `shell` du nœud (`bash`, `sh`, `pwsh`).
- Dans le YAML, les commandes contenant `\` s'écrivent entre **quotes simples**
  (en quotes doubles, `\` serait interprété comme un échappement).
- **Les démos fournies sont portables** : chemins en `/` et nœud `python` pour tout ce qui dépend du système
  (comptage de lignes, tests, calculs). Seules des commandes identiques partout (`git`) restent dans des étapes
  `shell`. Ne chaînez pas avec `&` (sous `bash` il lance en arrière-plan) : utilisez `&&`.

### Écrire un workflow : quels mots-clés ai-je le droit d'utiliser ?

Le vocabulaire est défini à un seul endroit : le décorateur `@register(..., params={...})` de chaque
nœud (dossier `nodes/`), complété par les mots-clés communs de `schema.py`. Trois outils en découlent :

```
python engine.py --nodes                   # liste les types d'étape et leurs paramètres (REFERENCE.md = même contenu)
python engine.py --check "workflows/*.yaml"   # valide sans exécuter : types inconnus, clé mal orthographiée,
                                           # paramètre obligatoire absent, next / on_approve vers une étape inexistante,
                                           # {{variable}} qui ne correspond à rien. Chaque message donne la ligne.
python engine.py --schema > csflow.schema.json   # régénère le schéma JSON de l'éditeur
```

(Sous Linux, `python3` ; les `/` fonctionnent aussi sous Windows.)

- **Autocomplétion dans VS Code** : installer l'extension « YAML » (Red Hat). La première ligne de chaque
  workflow, `# yaml-language-server: $schema=../csflow.schema.json`, suffit : VS Code propose les types,
  les clés de l'étape en cours et affiche leur description au survol.
- **Contrôle automatique** : `python engine.py fichier.yaml` et l'interface valident le fichier avant de
  l'exécuter. Les erreurs bloquent le lancement, les avertissements (clé inconnue, variable vide) s'affichent
  dans le log.
- **Ajouter un type d'étape** : créer `nodes/mon_node.py` avec `@register("mon_type", summary="...", params={...})`,
  l'importer dans `nodes/__init__.py`, puis régénérer `REFERENCE.md` et `csflow.schema.json`.

### Confirmations de CSAgent (`Auto-approve: OFF`)

Sans `--yes`, CSAgent demande une confirmation avant chaque outil destructeur
(`write_file`, `edit_file`, ...) et **attend** `POST /api/confirm`. Le nœud `agent` répond :

- par défaut, la demande apparaît dans l'interface (barre **Approuver / Rejeter**) ou au terminal (`y/n`) ;
- avec `auto_confirm: true` sur l'étape (ou la variable d'environnement `CSFLOW_AUTO_CONFIRM=1`), elle est approuvée automatiquement ;
- ou lance CSAgent avec `--yes` (c'est ce que font `start_agent.bat` et `start_agent.sh`) : aucune confirmation n'est alors émise.

```yaml
- id: fix_with_agent
  type: agent
  auto_confirm: true        # équivalent de csagent --yes pour cette étape
  prompt: ...
```

Si CSAgent reste silencieux plus de 15 s, le log affiche `⏳ en attente de CSAgent…` ;
au-delà de `timeout` (180 s par défaut) l'étape échoue au lieu de rester bloquée.

### Démo pour un chef de projet : `copil_projet.yaml`

Workflow en français (14 étapes) : « Préparation du COPIL » pour un projet fictif au bord du dérapage.

| Étape | Qui travaille | Ce que ça démontre |
|---|---|---|
| Plan de projet, notes de réunion | Python (`file_write`) | en vrai : un export Jira / Excel / MS Project |
| Indicateurs de pilotage, statut RAG | **Python** (`shell`) | les chiffres (valeur acquise, CPI, dérive en jours et en €) sont calculés exactement, jamais par une IA |
| Analyse des risques, actions et décisions, rapport COPIL | **CSAgent** (`agent`) | lecture de notes libres, rédaction pour la direction, chaque chiffre repris des fichiers |
| Note d'escalade au sponsor | CSAgent, **seulement si le statut est ROUGE** (`skip_if`) | branche conditionnelle |
| Validation de la cheffe de projet | **Humain** (`human_approval`) | rien n'est diffusé sans accord ; refus = diffusion annulée |

Lancer : `python app.py` (`python3` sous Linux), choisir « Préparation du COPIL », cliquer sur Run. Livrables dans `tmp/copil/`.
La date de référence (`date_reference`) est fixée au 16/10/2026 pour que la démo soit reproductible ; la modifier dans le YAML pour rejouer à une autre date.

---

## Ce que vous verrez

Exemple de sortie en ligne de commande (`python engine.py workflows\demo_autofix.yaml`). Les messages des
étapes (`Written … chars`, la réponse de l'agent) sont ceux produits par le programme.

```
╔══════════════════════════════════════════════════════════╗
║  🔧  auto-fix-demo                                       ║
╚══════════════════════════════════════════════════════════╝

────────────────────────────────────────────────────────────────
  [1/5] create_broken_script  (file_write)
  ✓  Written 312 chars → tmp/broken.py

────────────────────────────────────────────────────────────────
  [2/5] first_run  (shell)
  ✗  ZeroDivisionError: division by zero ⏎ …

────────────────────────────────────────────────────────────────
  [3/5] fix_with_agent  (agent)
  💭 There are two bugs: compute_average([]) and a typo 'totla'.
  🔧 read_file  {"path": "tmp/broken.py"}
  ✓  # Sales report…
  🔧 write_file {"path": "tmp/broken.py", "content": "…"}
  ✓  written

────────────────────────────────────────────────────────────────
  [4/5] verify_run  (shell)
  ✓  Average revenue :  11,175.00 ⏎ Total revenue   :  44,700.00

────────────────────────────────────────────────────────────────
  [5/5] done  (log)

  Script fixed and verified ✓
  Output:
  Average revenue :  11,175.00
  Total revenue   :  44,700.00

────────────────────────────────────────────────────────────────
  ✅  Workflow complete.
```

---

## Comment ça marche

```
workflow.yaml
     │
     ▼
 engine.py ── résout les {{variables}} ── confie l'étape au bon nœud
     │
     ├── file_write / file_read    →  Python pathlib
     ├── shell                     →  Python subprocess
     ├── python                    →  interpréteur embarqué (aucun Python à installer)
     ├── http_get / http_post      →  Python urllib
     ├── transform                 →  Python eval (restreint)
     ├── log / end                 →  affichage, fin du workflow
     ├── human_approval            →  pause jusqu'à la décision d'un humain
     └── agent                     →  CSAgent /api/chat  (flux SSE)
                                           ↑
                                     moteur de raisonnement en C#
                                     lit des fichiers, lance des commandes,
                                     réécrit les corrections sur le disque
```

Le nœud `agent` distingue CSFlow d'un simple script shell. Il reçoit l'instruction (dont les variables
sont déjà résolues) et raisonne seul pour y répondre — lire des fichiers, lancer des commandes,
réécrire du code — tout en renvoyant au terminal chaque réflexion et chaque appel d'outil.

---

## Référence des nœuds

| Type | Rôle | Paramètres principaux |
|---|---|---|
| `file_read` | Lire un fichier | `path` |
| `file_write` | Écrire un fichier (crée les dossiers) | `path`, `content` |
| `shell` | Lancer une commande shell | `cmd`, `timeout`, `shell` |
| `python` | Exécuter un script Python avec l'interpréteur embarqué | `script` ou `code`, `args`, `cwd`, `timeout` |
| `http_get` | Requête HTTP GET | `url`, `headers`, `timeout` |
| `http_post` | Requête HTTP POST | `url`, `body`, `headers`, `timeout` |
| `agent` | Étape de raisonnement confiée à CSAgent | `prompt`, `csagent_url`, `csagent_key`, `timeout`, `auto_confirm` |
| `transform` | Expression Python | `expression` |
| `human_approval` | Pause jusqu'à l'accord ou au refus d'un humain | `message` (avec `on_approve` / `on_reject`) |
| `log` | Afficher un message | `message`, `level` |
| `end` | Terminer le workflow | `message` |

La liste complète et à jour : `python engine.py --nodes`, ou `REFERENCE.md`.

---

## Syntaxe d'un workflow

```yaml
name: mon-workflow

steps:

  - id: fetch                     # référencé par {{fetch.output}}
    type: http_get
    url: "https://api.example.com/data"

  - id: process
    type: agent
    skip_if: "{{fetch.status_code}} != 200"   # étape ignorée si la condition est vraie
    prompt: |
      Analyse ces données : {{fetch.body}}
      Écris un résumé dans tmp/summary.md.

  - id: read_back
    type: file_read
    path: "tmp/summary.md"

  - id: show
    type: log
    message: "{{read_back.output}}"
```

### Variables intégrées

| Jeton | Valeur |
|---|---|
| `{{today}}` | Date du jour — `2026-10-16` |
| `{{now}}` | Date et heure — `2026-10-16T13:45:00` |
| `{{env.MA_VAR}}` | Variable d'environnement |

### Champs de sortie d'une étape

Chaque nœud renvoie au moins `output` et `status`. Champs propres à chaque nœud :

| Nœud | Champs supplémentaires |
|---|---|
| `shell`, `python` | `stdout`, `stderr`, `returncode` (entier) |
| `http_get` / `http_post` | `body`, `status_code` (entier) |
| `file_read` | `content` (alias de `output`) |
| `file_write` | `path` |
| `agent` | `thoughts` |
| `transform` | `value` (valeur Python brute) |

### `skip_if`

Toute étape peut être ignorée selon une condition :
```yaml
skip_if: "{{prev.returncode}} == 0"
skip_if: "{{fetch.status_code}} == 404"
```

### `continue_on_error`

Mettez `true` pour que le workflow continue même si l'étape échoue :
```yaml
- id: first_attempt
  type: shell
  cmd: "python script.py"
  continue_on_error: true
```

---

## Fichiers

```
workflow/
├── app.py                   ← interface EEL (python app.py) ; mode MCP : app.py --mcp
├── engine.py                ← exécuteur de workflows (--check, --nodes, --schema)
├── mcp_server.py            ← serveur MCP (stdio) : CSAgent lance et suit les workflows
├── schema.py                ← vocabulaire du YAML : validation, aide, schéma JSON
├── csflow.schema.json       ← schéma pour l'autocomplétion VS Code (généré)
├── REFERENCE.md             ← liste des types et paramètres (générée)
├── CSFLOW_LLM_GUIDE.md      ← guide destiné aux assistants IA qui écrivent des workflows
├── start_agent.bat / .sh    ← lance CSAgent (API) puis CSFlow, depuis ce dossier
├── start_csagent.bat / .sh  ← mode MCP : worker CSAgent + CSAgent interactif qui pilote CSFlow
├── mcp.json.example         ← config MCP (Linux, exe) ; mcp.windows.*.example, mcp.linux.source.example
├── build_nuitka.bat / .sh   ← compilation en exécutable unique
├── skills/                  ← skills CSAgent « CSFlow Workflows » et « CSFlow Workflow Authoring » (csflow.md, csflow-create-workflow.md, INDEX.md)
├── tests/mcp_smoke.py       ← test du serveur MCP, sans CSAgent
├── ui/index.html            ← interface (Umai embarqué, aucun CDN)
├── nodes/
│   ├── __init__.py          ← registre des nœuds
│   ├── file_node.py
│   ├── shell_node.py
│   ├── python_node.py       ← interpréteur embarqué
│   ├── http_node.py
│   ├── agent_node.py        ← intégration CSAgent (flux SSE)
│   ├── log_node.py
│   └── transform_node.py
├── workflows/
│   ├── copil_projet.yaml    ← DÉMO CHEF DE PROJET : préparation d'un COPIL (en français)
│   ├── demo_autofix.yaml    ← corrige un script cassé
│   ├── demo_report.yaml     ← analyse un CSV → rapport markdown
│   ├── demo_code_review.yaml← inspection + validation humaine + correction
│   └── dev_fix_tests.yaml   ← tests → CSAgent corrige → re-test → validation → commit
├── tmp/                     ← les workflows y écrivent leurs fichiers
└── requirements.txt         ← pyyaml + eel
```

---

## Prérequis

- Python 3.10 ou plus récent et `pip install -r requirements.txt` (pyyaml + eel) **pour lancer depuis les sources** ;
  inutile avec l'exe compilé (`build_nuitka.bat`), qui embarque l'interpréteur (voir le nœud `python`).
  Pour le seul mode MCP depuis les sources, PyYAML suffit.
- [CSAgent](https://github.com/artydev/CSAgent) lancé avec `--api --yes` (c'est ce que fait `start_agent.bat`).
- Une clé `ALBERT_API_KEY` (ou n'importe quel point d'accès compatible OpenAI via `--endpoint`).

## Workflow développeur : `dev_fix_tests.yaml`

Lance les tests d'un petit projet (dépôt git dans `tmp/devfix`), laisse CSAgent corriger le code
sans toucher aux tests, relance les tests, affiche `git diff --stat`, puis committe seulement
après votre validation (rejet : `git checkout`). Pour votre code, changez `inputs.project` et la
commande de test (`python -m unittest`).

## Disposition réglable

Les panneaux de l'interface se règlent à la souris comme au clavier (les réglages sont mémorisés dans le navigateur) :

- **Poignées** entre le graphe et les panneaux du bas (hauteur) et entre « Éditeur YAML » et « Événements » (largeur, écrans ≥ 1400 px). Souris : glisser. Clavier : Tab jusqu'à la poignée, puis flèches (2 %), Maj + flèches (10 %), Début / Fin (limites). Entrée ou double-clic : taille par défaut.
- **Masquer le détail** : le bouton à gauche de « Lancer le workflow » cache le panneau de détail de l'étape (à droite) pour donner plus de place au graphe. Le choix est mémorisé.
- **Agrandir** sur chaque panneau (graphe, YAML, événements) : il occupe toute la zone centrale. Échap ou « Restaurer » revient à la disposition normale.

## Console de développement du navigateur

Dans l'application, F12, Ctrl+Maj+I / J / C, Ctrl+U et le clic droit (« Inspecter ») sont désactivés. Ce confort d'usage n'est pas une protection : la page reste accessible dans un onglet normal du navigateur. Pour les réactiver (débogage de l'interface) :

```
set CSFLOW_DEBUG=1            (Windows)        export CSFLOW_DEBUG=1      (Linux / WSL)
python app.py                                  python3 app.py
```

## Scripts Python sans Python installé : le nœud `python`

Les scripts intégrés aux workflows (`file_write` d'un `.py`, puis exécution) n'ont plus besoin de
Python sur le poste : le nœud `python` les exécute avec l'interpréteur **embarqué dans l'exe**.

```yaml
- id: indicateurs
  type: python
  script: 'tmp/copil/kpi.py'            # ou  code: |  (Python inline) ; « / » marche sous Windows et Linux
  args: ['tmp/copil/taches.csv', '{{date_reference}}']
  # cwd: 'tmp/projet'   timeout: 60
```

Sorties identiques à `shell` : `output`, `stdout`, `stderr`, `returncode`. Dans l'exe compilé, CSFlow
se relance lui-même (`FlowPortal.exe --csflow-python …`, `./FlowPortal --csflow-python …` sous Linux), ce qui donne isolation et `timeout`.

Limites : seuls les modules de la bibliothèque standard listés dans `build_nuitka.bat` / `build_nuitka.sh`
(`--include-module`) sont disponibles — ajoutez-y les vôtres — et pas de `pip install` à l'exécution.
Les démos `copil_projet`, `demo_autofix` et `dev_fix_tests` utilisent ce nœud ; `shell` reste là
pour les autres commandes (git, etc.).

**CSAgent et Python :** l'agent exécute ses propres commandes (`sh`) et ne voit pas l'interpréteur embarqué.
Sur un poste sans Python, un agent qui tente `python` ou `py` échoue. Dans les prompts des étapes
`agent`, interdisez-lui d'exécuter du code (« n'exécute rien, utilise read_file/edit_file ») : c'est
l'étape `python` du workflow qui relance le script et rapporte le résultat.

## Client MCP : appeler un serveur MCP depuis un workflow (sans CSAgent)

Les étapes `mcp_tools` (lister les outils) et `mcp_call` (appeler un outil) parlent MCP directement, en bibliothèque standard : rien à installer. Les serveurs se déclarent **par leur nom** dans `mcp.json`, au même format que `csagent --mcp-config` (voir `mcp.client.example`) : transport stdio (`command`, `args`) ou HTTP (`url`, `headers`), `${VARIABLE}` pour les secrets.

```yaml
- id: lire
  type: mcp_call
  server: fichiers
  tool: read_text_file
  arguments:
    path: "tmp/rapport.txt"
  save_as: tmp/mcp/rapport.txt
```

Un workflow ne peut désigner qu'un serveur déclaré (il ne lance pas de commande de son choix), la connexion est partagée par les étapes puis fermée à la fin, et le résultat d'un outil reste une donnée (à relire par un agent depuis un fichier). Détails dans `REFERENCE.md`. Exemple complet : `workflows/goal_football.yaml` (football, serveur MCP de GOAL API ; copier `mcp.goal.example` en `mcp.json` et définir `GOAL_API_KEY`).

## Mode MCP : CSAgent lance vos workflows (`FlowPortal --mcp`)

`FlowPortal --mcp` démarre CSFlow **sans interface**, comme serveur MCP (stdio). CSAgent, en mode terminal
normal, peut alors lister, vérifier et exécuter vos workflows, et vous rendre compte du résultat.

```
Vous ──► CSAgent interactif (sans --yes) ──MCP──► FlowPortal --mcp ──► moteur ──► (étapes agent) ──► CSAgent worker --api :8080
```

### Mise en route (exe compilé, Windows)

Au premier lancement, l'exe crée à côté de lui `workflows\`, `tmp\`, `csflow.schema.json`, `CSFLOW_LLM_GUIDE.md` et `REFERENCE.md` (sans jamais écraser un fichier existant). Donnez le guide LLM à votre assistant pour qu'il écrive des workflows corrects.

1. Compilez avec `build_nuitka.bat` : `build\` contient `FlowPortal.exe`, `start_agent.bat`, `start_csagent.bat`,
   `mcp.windows.exe.example` et le dossier `skills\`.
2. Dans `build\` :
   ```
   set ALBERT_API_KEY=votre-clé
   set MODEL=nom-du-modèle        (facultatif : sinon routage automatique de CSAgent)
   start_csagent.bat
   ```
   Au premier lancement le script crée `mcp.json` (à partir de `mcp.windows.exe.example`), démarre le
   **worker** `csagent --api` (port 8080, mémoire `csflow_worker`) puis le CSAgent interactif avec
   `--mcp-config mcp.json`. Fermer CSAgent arrête le worker.
3. Dans CSAgent : « Liste les workflows CSFlow », « Vérifie demo_report », « Lance demo_report ».
   Chaque appel MCP vous est demandé (CSAgent interactif lancé sans `--yes`).

Autres cas : depuis les sources, les scripts choisissent la bonne config MCP — sous Windows,
`start_csagent.bat` copie `mcp.windows.source.example` (`python.exe app.py --mcp`) ; sous Linux,
`./start_csagent.sh` copie `mcp.json.example` (`./FlowPortal --mcp`) si l'exe est à côté, sinon
`mcp.linux.source.example` (`python3 app.py --mcp`).
CSAgent lit les skills `skills/csflow.md` (lancer un workflow) et `skills/csflow-create-workflow.md` (en écrire un : `name:` renseigné, fichiers produits sous `output/{{run_id}}/`, validation par `csflow_check`) depuis son dossier de travail : lancez-le depuis le dossier de CSFlow.

### Linux/WSL sans l'exe (depuis les sources)

Sous Linux, Python est en général déjà installé : inutile de compiler l'exe. CSAgent lance alors
`python3 app.py --mcp` au lieu de `./FlowPortal --mcp` ; le serveur MCP est le même.

- **Prérequis :** `python3` (3.10 ou plus récent) et PyYAML (`sudo apt install python3-yaml`, ou `pip install pyyaml`
  dans un environnement virtuel). `eel` n'est **pas** nécessaire pour le mode MCP, seulement pour l'interface.
- **Lancer :** depuis le dossier de CSFlow, `./start_csagent.sh`. Sans `./FlowPortal` mais avec `app.py`, le script crée
  `mcp.json` à partir de `mcp.linux.source.example` et vérifie `python3` et PyYAML.
- **Fichiers :** `workflows/` et `tmp/` sont dans le dossier des sources, à côté de `app.py`. Le nœud `python`
  utilise alors le `python3` du système au lieu de l'interpréteur embarqué.
- Le worker CSAgent (`--api`, port 8080) reste nécessaire pour les étapes `agent`.

### Les quatre outils

| Outil | Rôle |
|---|---|
| `csflow_list` | liste les workflows de `workflows/` (`needs_human`, `uses_agent`, nombre d'étapes) et le dossier de travail `cwd` |
| `csflow_check` | valide un workflow sans l'exécuter (`ok`, `errors`, `warnings`, `issues`) |
| `csflow_run` | valide puis lance un workflow ; attend au plus 55 s ; renvoie l'état |
| `csflow_status` | état d'un run (le dernier par défaut) ; attend au plus 55 s qu'il change |

États d'un run : `running`, `done`, `error`. Un run en erreur renvoie `failed_step` (id, code retour, sortie).
`csflow_run` répond `started: false` avec la raison si le workflow est invalide ou si un autre run est en cours.

### Garde-fous

- seuls les fichiers du dossier `workflows/` sont accessibles (aucun chemin, aucun `..`) ;
- un seul workflow à la fois ;
- un workflow contenant `human_approval` est **refusé** : à lancer depuis l'interface, où vous approuvez ;
- CSAgent coupe un appel MCP à 60 s : `csflow_run` et `csflow_status` rendent donc la main après 55 s au plus
  (`running`), et CSAgent rappelle `csflow_status` jusqu'à `done` ou `error` ;
- le CSAgent interactif n'est pas lancé avec `--yes` : chaque appel MCP vous est demandé.

**Limite à connaître :** les étapes `shell` et `python` d'un workflow ne passent pas par le filtre de
commandes de CSAgent, et un agent pourrait écrire un workflow. Ne confiez à ce mode que des workflows
et un dossier de confiance.

### Vérifier

- Sans CSAgent : `python tests\mcp_smoke.py` (Windows) ou `python3 tests/mcp_smoke.py` (Linux) (19 contrôles, doit finir par `TOUT EST OK`).
- Exe compilé : un exe sans console n'écrit rien dans `cmd` ; testez-le avec des tubes (voir `tests/mcp_smoke.py`
  pour le protocole), ou directement depuis CSAgent.

## Licence

MIT, voir [LICENSE](LICENSE).

<img width="1217" height="787" alt="image" src="https://github.com/user-attachments/assets/b8e047c0-e934-49e6-bc67-6bff150c9c93" />

