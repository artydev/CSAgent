# CSAgent — Agent de codage autonome multiplateforme

🌐 [English](README.md) · **Français**

**CSAgent** est un agent de codage autonome multiplateforme qui fonctionne sous Windows, Linux et macOS. Il utilise une API compatible OpenAI (par exemple l'[API Albert](https://albert.api.etalab.gouv.fr)) pour comprendre des instructions en langage naturel et réaliser seul des tâches de codage : lire, écrire et lister des fichiers, et exécuter des commandes shell.

Il propose trois modes de présentation : une interface terminal (TUI), une interface web et une interface légère (Lean UI).

---

## Table des matières

- [Démarrage rapide](#démarrage-rapide)
- [Modes de fonctionnement](#modes-de-fonctionnement)
  - [Mode CLI (par défaut)](#mode-cli-par-défaut)
  - [Mode Web UI](#mode-web-ui)
  - [Mode Lean UI](#mode-lean-ui)
  - [Mode API sans interface (pour orchestrateurs)](#mode-api-sans-interface-pour-orchestrateurs)
  - [Vision et images jointes](#vision-et-images-jointes)
- [Modèles LLM](#modèles-llm)
  - [Modèles locaux avec Ollama](#modèles-locaux-avec-ollama)
  - [Régler le routage à la main (`LLMRoutingRules/`)](#régler-le-routage-à-la-main-llmroutingrules)
- [Fonctionnalités à venir](#fonctionnalités-à-venir)
- [Variables d'environnement](#variables-denvironnement)
- [Arguments de la ligne de commande](#arguments-de-la-ligne-de-commande)
- [Fonctions de sécurité](#fonctions-de-sécurité)
- [Outils disponibles](#outils-disponibles)
  - [Serveurs MCP](#serveurs-mcp)
- [Skills](#skills)
- [Mémoire et persistance de la conversation](#mémoire-et-persistance-de-la-conversation)
  - [Mémoire hybride (anti-amnésie)](#mémoire-hybride-anti-amnésie)
  - [Distillation de session (entre les sessions)](#distillation-de-session-entre-les-sessions)
  - [Fichiers de mémoire](#fichiers-de-mémoire)
  - [Organisation du code](#organisation-du-code)
- [Compilation depuis les sources](#compilation-depuis-les-sources)
- [Tests](#tests)
- [Publication AOT](#publication-aot)
- [Dépannage](#dépannage)

---

## Démarrage rapide

### Prérequis

- SDK .NET 10.0 ou supérieur (pour compiler depuis les sources)
- Une clé d'API pour un serveur compatible OpenAI (par exemple l'[API Albert](https://albert.api.etalab.gouv.fr))

### Lancer l'interface web

```bash
# Définir la clé d'API
set ALBERT_API_KEY=votre-cle-api

# Lancer le serveur web
csagent --ui
```

Ouvrez ensuite votre navigateur sur **http://localhost:5050** (ou sur le port choisi avec `--port`).

### Lancer en mode CLI

```bash
set ALBERT_API_KEY=votre-cle-api
dotnet run
```

### Une seule demande depuis la ligne de commande

```bash
csagent --prompt "hello my name is John"
```

CSAgent exécute cette demande puis quitte. Seule la réponse est écrite sur la sortie standard : elle peut être redirigée vers une autre commande (voir [`--prompt`](#arguments-de-la-ligne-de-commande)).

---

## Modes de fonctionnement

### Mode CLI (par défaut)

En mode CLI, CSAgent ouvre une session interactive en texte. Vous saisissez des instructions et l'agent les traite seul, étape par étape.

```
> User: Create a new C# console project that prints "Hello, World!"
```

L'agent va :
1. Réfléchir à la tâche
2. Exécuter des outils (écrire des fichiers, lancer des commandes shell)
3. Rendre compte des résultats
4. Continuer jusqu'à ce que la tâche soit terminée

Tapez `exit` pour quitter la session. Pour une seule demande sans session interactive, utilisez `--prompt "texte"`.

### Mode Web UI

En mode Web UI (option `--ui`), CSAgent démarre un serveur web local avec une interface moderne au thème sombre, qui offre :

- Le flux en temps réel des réflexions de l'agent, des appels d'outils et des résultats, via Server-Sent Events (SSE)
- La coloration syntaxique des blocs de code (via Prism.js)
- Une interface adaptée au bureau comme au mobile
- Une esthétique sobre, inspirée du terminal

L'interface web est servie par défaut sur **http://localhost:5050**. Utilisez `--port <n>` (ou `-p <n>`) pour changer le port.

#### Enregistreur vocal (Web UI et Lean UI)

Le bouton 🎙 enregistre depuis le navigateur. Les longs enregistrements ne posent pas de problème : l'audio est envoyé par morceaux de 30 secondes dans `recordings/`. (Dans la Lean UI, la transcription et les deux choix s'affichent dans le journal.) À l'arrêt, l'audio est transcrit via le serveur LLM (Albert, Whisper ; `ffmpeg` est nécessaire pour les enregistrements de plus de quelques minutes, voir `transcribe_audio`). Vous choisissez ensuite :

- **Use as instruction** (utiliser comme instruction) : le texte est placé dans la zone de saisie.
- **Keep as text** (garder comme texte) : la transcription est enregistrée dans `transcripts/<nom>.txt` et jointe à votre prochaine demande sous la forme `[Attached text file: chemin]` (donnée pour l'agent, par exemple à envoyer par e-mail en pièce jointe).

À côté du bouton, **FR / EN / Auto** règle la langue que vous parlez (cela aide Whisper, et le choix est mémorisé par le navigateur). À la première visite, c'est la langue du navigateur qui est suivie quand elle est le français ou l'anglais ; **Auto** laisse Whisper la détecter.

Les anciens fichiers audio peuvent être supprimés automatiquement : définissez `CSAGENT_AUDIO_KEEP_DAYS=30` et, à chaque démarrage de `--ui` / `--leanui`, les fichiers audio de `recordings/` non modifiés depuis 30 jours sont supprimés. Cette option est désactivée par défaut, et les transcriptions ne sont jamais supprimées.

Points d'accès (même origine uniquement) : `POST /api/audio/start`, `POST /api/audio/{id}` (morceau), `POST /api/audio/{id}/transcribe` (progression en SSE). Les enregistrements et les transcriptions restent sur le disque ; vous voudrez peut-être ajouter `recordings/` et `transcripts/` à votre `.gitignore`.

### Mode Lean UI

Le mode Lean UI (option `--leanui`) est une variante légère de l'interface web, dans le style d'un terminal. Il a ses propres ressources intégrées (`Presentation/LeanUI/assets`) et les mêmes points d'accès de conversation en SSE ; il se lance avec l'argument `--leanui`. Il est servi par défaut sur **http://localhost:5050** (utilisez `--port <n>` pour le changer).

### Mode API sans interface (pour orchestrateurs)

`--api` démarre le même serveur SSE que l'interface web **sans aucune interface** : pas de routes HTML/JS/CSS, pas de navigateur ouvert, pas d'accès au presse-papiers. Il est destiné à être piloté par un autre programme, comme un orchestrateur d'agents.

```bash
set ALBERT_API_KEY=votre-cle-llm

# En local, sans confirmation
csagent --api --yes

# Accessible depuis d'autres machines : une clé d'API est obligatoire
csagent --api --yes --host 0.0.0.0 --port 8080 --api-key s3cret
```

| Option | Effet |
|---|---|
| `--api` | Mode sans interface (prioritaire sur `--ui` / `--leanui`). |
| `--yes`, `-y`, `--auto-approve` | Chaque appel d'outil est approuvé automatiquement : l'agent n'attend jamais `POST /api/confirm`. Fonctionne aussi en modes CLI, `--ui` et `--leanui`. |
| `--host <adresse>` | Adresse d'écoute (par défaut `localhost`). Utilisée seulement avec `--api` ; les modes Web UI écoutent toujours sur `localhost`. |
| `--api-key <clé>` | Chaque requête doit présenter cette clé. Peut aussi être définie par la variable d'environnement `CSAGENT_API_KEY` (à préférer : une valeur passée en ligne de commande est visible dans la liste des processus). |

**`--yes` supprime seulement les demandes de confirmation.** Le filtre des commandes shell (`sudo`, `chmod`, `shutdown`, `/etc/`, …) et les restrictions de chemins restent actifs.

**Points d'accès**

| Point d'accès | Description |
|---|---|
| `GET /api/chat?prompt=…[&task=slug]` | Exécute une demande ; la réponse est un flux Server-Sent Events |
| `POST /api/chat` | Idem, en `multipart/form-data` (`prompt`, `task` facultatif, `image` facultative) |
| `POST /api/confirm` | Corps `true` ou `false` : répond à un événement `confirm` en attente (inutile avec `--yes`) |

**Authentification.** Quand une clé est définie, envoyez `Authorization: Bearer <clé>` ou `X-API-Key: <clé>` ; toute autre requête reçoit `401`. Le serveur parle en HTTP simple : dès qu'il est exposé au-delà de la machine locale, placez-le derrière un reverse proxy TLS. **Sans clé, un `--host` non local est refusé au démarrage**, parce que l'agent peut exécuter des commandes et écrire des fichiers.

**Événements.** Chaque message SSE a la forme `data: {"id": <n>, "type": "<type>", "data": …}` :

| `type` | `data` |
|---|---|
| `step` | `{"n": 1, "m": 30}` : numéro de l'étape / nombre maximal d'étapes |
| `thought` | Le texte du modèle |
| `model` | Le modèle qui a écrit le `thought` qui vient d'être envoyé : `"openweight-large"`, ou `"openweight-large (served: openai/gpt-oss-120b)"` quand le serveur indique un autre nom |
| `call` | `{"n": "<outil>", "a": "<arguments JSON>"}` |
| `result` | `{"r": "<sortie>", "e": false}` : `e` vaut `true` quand l'outil a échoué |
| `confirm` | `{"tool": "<outil>"}` : en attente de `POST /api/confirm` (jamais envoyé avec `--yes`) |
| `done` | La tâche est terminée |
| `error` | Une erreur fatale pour cette requête |
| `warning`, `danger` | Avertissements (par exemple la ligne sur le résumé de session) |

`done` n'est pas toujours le dernier message (un `warning` sur le résumé de session peut suivre) : lisez jusqu'à ce que le serveur ferme la connexion.

```bash
curl -N -H "Authorization: Bearer s3cret" \
     "http://localhost:8080/api/chat?prompt=create+hello.txt+containing+hi"
```

**Une conversation par serveur.** Toutes les requêtes partagent le même dossier de mémoire (`--mem`) et la même conversation : envoyez donc une requête à la fois. Pour exécuter plusieurs tâches indépendantes en parallèle, lancez une instance par tâche, chacune avec son propre nom `--mem` et son propre `--port`.

### Vision et images jointes

CSAgent gère les **demandes multimodales (vision)** : vous pouvez joindre une image à une demande et l'agent l'analysera. Cela fonctionne dans la Web UI, dans la Lean UI et en CLI/TUI.

- **Web UI / Lean UI :** cliquez sur le bouton **📎** (trombone) à côté de la zone de saisie pour joindre une image. Un petit aperçu apparaît ; cliquez sur **✕** pour le retirer avant l'envoi. Formats acceptés : **PNG, JPEG, GIF, WebP** (**10 Mo** au maximum).
- **CLI / TUI :** la pièce jointe passe par l'historique de la conversation. Seule la demande qui contient l'image utilise le modèle de vision ; les demandes suivantes reprennent le routage habituel.

Quand une image est jointe, CSAgent envoie automatiquement la demande à un **modèle capable de vision** (`gemma-4-31b-it` par défaut) à la place du modèle de texte habituel. Ce routage est automatique et ne concerne que la **demande en cours** : elle part vers le modèle de vision si, et seulement si, le dernier message de l'utilisateur contient une image jointe.

Les images des échanges précédents ne sont pas renvoyées : avant l'envoi à un modèle de code ou de chat, elles sont remplacées par le court texte `[image omitted: sent earlier in the conversation]` (le texte du message est conservé). **Pour poser une question de suivi sur une image, joignez-la de nouveau** : sinon le modèle ne dispose que de la description donnée au tour précédent.

Vous pouvez changer le modèle de vision avec `--vision-model` (ou `CSAGENT_VISION_MODEL`) ; `--model` impose un seul modèle pour tous les messages, images comprises. La valeur par défaut est définie par `LlmSettings.VisionModel`.

---

## Modèles LLM

CSAgent choisit le modèle **pour chaque message**, selon ce que vous demandez. Il existe trois profils, identiques dans tous les modes (CLI, `--ui`, `--leanui`, `--api`) :

| Profil | Modèle par défaut | Utilisé quand |
|---|---|---|
| **Code** | `deepseek-v4-flash` | Code, fichiers, shell, e-mails, audio, liens… et tout ce qui n'est pas clairement une question générale. C'est le modèle qui exécute les outils |
| **Chat** | `openweight-large` (alias Albert de `gpt-oss-120b`) | Recherches (web, actualités, météo, un lien à lire) et conversation générale sans rapport avec le code ou les fichiers : une explication, une question de culture, un texte à rédiger, un conseil |
| **Vision** | `gemma-4-31b-it` | Le message en cours contient une image jointe ; voir [Vision et images jointes](#vision-et-images-jointes) |

Comment le choix est fait (aucun appel LLM supplémentaire, aucun délai) :

1. `--model <nom>` l'emporte toujours, pour chaque message.
2. Une image jointe au message en cours sélectionne le modèle de vision.
3. Un message qui contient un nom de fichier, un chemin, du code (accents graves), un fichier joint ou une action sur votre ordinateur (« open in Edge », « ouvre Chrome », « télécharge… ») sélectionne le modèle de code, quoi qu'il dise d'autre.
4. Une recherche explicite sélectionne le modèle de chat, même quand le message contient aussi des mots qui ressemblent à du code : « search the web for C# async tips », « cherche sur internet… », « fetch latest scientific news », actualités, météo, Wikipédia, « actualités ». Un lien à lire (« résume https://... ») est aussi une recherche, et non un travail sur des fichiers.
5. Un message qui parle de code, d'une commande, d'e-mail, d'audio, de git, de tests… sélectionne le modèle de code.
6. Une réponse courte qui poursuit le tour précédent (elle commence par « yes », « ok », « go ahead », « d'accord », « merci »… et compte 8 mots ou moins) garde le modèle de ce tour : le modèle de chat après une recherche web, le modèle de code après toute autre utilisation d'outil. Un message court qui commence autrement est une nouvelle demande, routée d'après son propre contenu.
7. Sinon, c'est une conversation générale et le modèle de chat répond.

Le modèle de code garde tout ce qui touche à vos fichiers, au shell, à git ou aux e-mails ; le modèle de chat prend les recherches et les questions générales. Le modèle est indiqué à deux endroits. Avant chaque exécution, la CLI affiche le choix et sa raison, par exemple `[model: openweight-large (chat: question générale)]` ; les interfaces web affichent la même ligne quand le modèle de chat répond. Sous **chaque message de l'assistant**, toutes les interfaces montrent le modèle qui l'a écrit (`[model: ...]` en CLI, une ligne `[model]` en `--ui` et `--leanui`, un événement `model` en `--api`), avec entre parenthèses le nom indiqué par le serveur quand il diffère de l'alias demandé.

Avant d'utiliser le modèle de chat, CSAgent le vérifie dans la liste des modèles du serveur (la même que celle de l'outil `list_models`, récupérée une fois et conservée 10 minutes). Si le modèle est inconnu, signalé indisponible, ou n'est pas un modèle de texte, c'est le modèle de code qui répond à la place et la raison est affichée. Si la liste ne peut pas être récupérée, le modèle de chat est utilisé quand même.

Les alias `openweight-*` sont ceux de l'API Albert : ils restent les mêmes quand un modèle passe à une nouvelle version. Sur un `--endpoint` personnalisé (Ollama…), il n'y a pas de modèle de chat tant que vous n'en définissez pas un avec `--chat-model` : tous les messages utilisent alors le modèle de code, comme avant.

Réglages :

| Réglage | Effet |
|---|---|
| `--code-model <nom>` / `CSAGENT_MODEL_CODE` | Modèle du profil code |
| `--chat-model <nom>` / `CSAGENT_MODEL_CHAT` | Modèle du profil chat |
| `--vision-model <nom>` / `CSAGENT_VISION_MODEL` | Modèle du profil vision |
| `--no-route` / `CSAGENT_ROUTING=off` | Toujours utiliser le modèle de code (pas de choix automatique) |
| `--model <nom>` | Un seul modèle pour tout (l'emporte sur tout ce qui précède) |

Le modèle de chat doit gérer l'**appel d'outils** (tool calling), car la boucle de l'agent peut utiliser des outils dans n'importe quel message. Testez un modèle avec `csagent --model <nom> --prompt "list the files here and summarise the README"` avant d'en faire votre modèle de chat.

### Modèles locaux avec Ollama

Tout serveur compatible OpenAI convient. Avec [Ollama](https://ollama.com) :

```bash
ollama pull qwen2.5-coder:14b
csagent --endpoint http://localhost:11434/v1 --model qwen2.5-coder:14b
```

- Aucune clé d'API n'est nécessaire pour un serveur local (`localhost`, `127.x`, `::1`) ; un serveur distant exige toujours `ALBERT_API_KEY`.
- Choisissez un modèle qui gère l'**appel d'outils** (par exemple `qwen2.5-coder`, `qwen3`, `llama3.1`) ; la boucle de l'agent en dépend.
- Les images demandent un modèle de vision : `--vision-model llava` (sinon c'est le `gemma-4-31b-it` par défaut qui est demandé, et Ollama ne l'a pas).
- Définissez `CSAGENT_ENDPOINT` pour ne pas retaper l'URL. Fonctionne dans tous les modes (`--ui`, `--leanui`, `--api`).

### Exemples

```bash
# Mode CLI avec un autre modèle
csagent --model gpt-4o

# Mode Web UI avec un autre modèle
csagent --ui --model deepseek-v4-flash
```

### Régler le routage à la main (`LLMRoutingRules/`)

Le routage peut être ajusté sans recompiler. Créez le dossier avec `csagent --init-routing`, puis modifiez les fichiers JSON ; les changements s'appliquent au message suivant.

| Fichier | Rôle |
|---|---|
| `models.json` | Modèle de chaque profil : `{ "code": "...", "chat": "...", "vision": "..." }` (`null` = valeur intégrée) |
| `keywords.json` | Ajoute ou retire les mots, radicaux, expressions et extensions de fichier qui pilotent le choix automatique |
| `rules.json` | Vos propres règles, testées dans l'ordre (la première qui correspond l'emporte) avant la logique intégrée (`contains`, `contains_all`, `not_contains`, `starts_with`, `min_words`, `max_words`, `use`) |
| `keywords.defaults.json` | Copie de référence des listes intégrées (jamais lue, régénérée par `--init-routing`) |

- Recherche du dossier : `--rules <dossier>` (ou `CSAGENT_ROUTING_RULES`), puis `./LLMRoutingRules`, puis à côté de l'exécutable.
- Une erreur dans un fichier n'arrête jamais l'agent : un avertissement s'affiche une seule fois et les valeurs intégrées sont conservées.
- `csagent --explain-routing "votre message"` montre quel modèle serait choisi, et pourquoi, sans appeler aucun modèle.
- Les messages de routage existent en anglais et en français : `--lang fr|en`, puis `CSAGENT_LANG`, puis la langue du système. `--init-routing` écrit les commentaires et le README du dossier dans cette langue.
- `--no-route` / `CSAGENT_ROUTING=off` désactive aussi les règles de l'utilisateur. Le `README.md` du dossier contient la référence complète.

---

## Fonctionnalités à venir

Les fonctionnalités suivantes sont prévues pour de prochaines versions :

- **Scripts Python** — piloter CSAgent depuis des scripts Python : lancer des sessions, envoyer des demandes, et récupérer par programme les réponses et les événements de l'agent (étapes, appels d'outils, résultats), par exemple via un module csagent ou un client de l'interface web (SSE).

---

## Variables d'environnement

| Variable | Obligatoire | Description |
|---|---|---|
| `ALBERT_API_KEY` | Oui, sauf pour un serveur local | Votre clé d'API pour le serveur compatible OpenAI. Inutile quand `--endpoint` désigne cette machine (localhost, 127.x, ::1) |
| `CSAGENT_ENDPOINT` | Non | Équivaut à `--endpoint` (l'argument l'emporte) |
| `CSAGENT_VISION_MODEL` | Non | Équivaut à `--vision-model` |
| `CSAGENT_MODEL_CODE` | Non | Équivaut à `--code-model` (par défaut `deepseek-v4-flash`) |
| `CSAGENT_MODEL_CHAT` | Non | Équivaut à `--chat-model` (par défaut `openweight-large` sur le serveur Albert) |
| `CSAGENT_FALLBACK_MODEL` | Non | Modèle vers lequel passer quand le quota journalier du modèle en cours est épuisé (erreur 429). Par défaut, le modèle de code sur le serveur Albert ; `off` (ou `none`) désactive la bascule. Voir [Gestion des erreurs 429](https://artydev.github.io/csagent/gestion-429.html) |
| `CSAGENT_ROUTING` | Non | `off` (ou `0`, `false`, `no`) désactive le choix automatique du modèle, comme `--no-route` |
| `CSAGENT_ROUTING_RULES` | Non | Équivaut à `--rules` : dossier des règles de routage |
| `CSAGENT_LANG` | Non | Équivaut à `--lang` : langue des messages de routage (`fr` ou `en`) |
| `CSAGENT_TRANSCRIBE_MODEL` | Non | Modèle de transcription vocale utilisé par `transcribe_audio` (par défaut `openai/whisper-large-v3`) |
| `CSAGENT_FFMPEG` | Non | Chemin de `ffmpeg` pour `transcribe_audio` quand il n'est pas dans le `PATH` |
| `CSAGENT_AUDIO_KEEP_DAYS` | Non | Enregistreur web : supprime au démarrage l'audio de `recordings/` plus ancien que ce nombre de jours (par défaut : tout conserver ; les transcriptions ne sont jamais supprimées) |
| `CSAGENT_API_KEY` | Non | Clé que les clients doivent présenter en mode `--api` (équivaut à `--api-key`) |
| `CSAGENT_MCP_URL` | Non | Équivaut à `--mcp` (l'argument l'emporte) |
| `CSAGENT_MCP_CONFIG` | Non | Équivaut à `--mcp-config` : fichier JSON qui décrit les serveurs MCP |

---

## Arguments de la ligne de commande

| Argument | Description |
|---|---|
| `--ui` | Démarre en mode Web UI (lance un serveur web) |
| `--prompt "texte"` | Mode terminal : exécute une seule demande, affiche la réponse puis quitte (`csagent --prompt "hello my name is John"`). **Mode script** : seule la réponse finale est écrite sur la sortie standard, sans bannière, étapes, appels d'outils, modèle ni jetons, de sorte qu'elle peut être redirigée vers une autre commande (`csagent --prompt "liste les fichiers ici" --yes | clip`). Avertissements et erreurs vont sur la sortie d'erreur ; code de sortie 1 en cas d'erreur. Si l'entrée est redirigée, un outil qui demande confirmation est refusé (utilisez `--yes`). La conversation est enregistrée comme d'habitude dans le dossier de mémoire ; les outils demandent confirmation, sauf avec `--yes`. Ajoutez `--verbose` pour retrouver l'affichage complet. Un argument seul, sans tiret, reste le nom du dossier de mémoire. Sans texte, affiche l'usage (code de sortie 2) ; code de sortie 1 en cas d'erreur |
| `--init-routing` | Crée le dossier `LLMRoutingRules/` avec des fichiers JSON modifiables |
| `--explain-routing "msg"` | Montre quel modèle serait utilisé pour un message, sans appeler de modèle |
| `--rules <dossier>` | Utilise ce dossier de règles de routage (ou `CSAGENT_ROUTING_RULES`) |
| `--lang <en\|fr>` | Langue des messages de routage (raisons, avertissements, fichiers générés) ; par défaut la langue du système, ou `CSAGENT_LANG` |
| `--leanui` | Démarre en mode Lean UI (variante légère de la Web UI, dans le style d'un terminal) |
| `--api` | Démarre le serveur SSE sans interface (voir [Mode API sans interface](#mode-api-sans-interface-pour-orchestrateurs)) |
| `--verbose` | Avec `--prompt` : affiche la bannière, les étapes, les appels d'outils et les jetons comme en mode interactif (par défaut, seule la réponse est affichée) |
| `--quiet`, `-q` | CLI, `--ui` et `--leanui` : n'affiche que les messages de l'assistant (plus les avertissements, les erreurs et la ligne finale). Les étapes, appels d'outils et résultats sont masqués ; un appel d'outil en échec est signalé sur une ligne, et une action destructive montre son appel d'outil juste avant de demander confirmation. `--api` l'ignore (les orchestrateurs ont besoin de tous les événements) |
| `--yes`, `-y` | Approuve automatiquement chaque appel d'outil (aucune demande de confirmation ; le filtre des commandes shell reste actif) |
| `--host <adresse>` | Avec `--api` : adresse d'écoute (par défaut `localhost` ; une adresse non locale exige une clé d'API) |
| `--api-key <clé>` | Avec `--api` : clé exigée sur chaque requête (ou définissez `CSAGENT_API_KEY`) |
| `--mcp <url>`, `--mcp-url <url>` | Se connecte à un serveur MCP en Streamable HTTP (voir [Serveurs MCP](#serveurs-mcp)). Répétable pour plusieurs serveurs. Ou définissez `CSAGENT_MCP_URL` |
| `--mcp-stdio "<commande>"` | Démarre un serveur MCP local et dialogue avec lui sur son entrée et sa sortie standard, par exemple `--mcp-stdio "npx -y @modelcontextprotocol/server-filesystem ."`. Répétable |
| `--mcp-header "Nom: valeur"` | En-tête HTTP envoyé aux serveurs `--mcp` (`${VAR}` est remplacé par la variable d'environnement). Répétable |
| `--mcp-config <fichier>` | Fichier JSON qui décrit les serveurs MCP (HTTP ou stdio). Ou définissez `CSAGENT_MCP_CONFIG` |
| `--mem <nom>` | Dossier de mémoire contenant la conversation et les fichiers de mémoire (par défaut : `agent_memory`, voir [Fichiers de mémoire](#fichiers-de-mémoire)) |
| `--model <modèle>` | Utilise ce modèle pour chaque message (désactive le choix automatique, voir [Modèles LLM](#modèles-llm)) |
| `--code-model <nom>` | Modèle pour le code, les fichiers et les outils (par défaut : `deepseek-v4-flash`) |
| `--chat-model <nom>` | Modèle pour la conversation générale (par défaut sur Albert : `openweight-large` ; aucun sur un `--endpoint` personnalisé) |
| `--no-route` | Utilise toujours le modèle de code (pas de choix automatique du modèle) |
| `--endpoint <url>` | URL de base compatible OpenAI (par défaut : API Albert). Pour Ollama : `http://localhost:11434/v1` (voir [Modèles locaux avec Ollama](#modèles-locaux-avec-ollama)) |
| `--vision-model <nom>` | Modèle utilisé quand le message en cours contient une image (par défaut : `gemma-4-31b-it`) |
| `--port`, `-p <n>` | Numéro de port de la Web UI (par défaut : `5050`) |
| `--task <slug>` | Suit le travail dans `.csagent/tasks/<slug>-<date>/` (fichiers de plan et de progression créés par l'hôte, jamais par le modèle) |
| `--dry-run` | Simule l'exécution des outils sans rien modifier |
| `--max-retries <n>` | Nombre maximal de tentatives pour les erreurs HTTP 429 (limite de débit), première requête comprise (par défaut : `6`). Voir [Gestion des erreurs 429](https://artydev.github.io/csagent/gestion-429.html) |
| `--retry-delay <ms>` | Délai de base (en ms) avant la première nouvelle tentative (par défaut : `1000`) |
| `--context-chars <n>` | Taille au-delà de laquelle les plus anciens messages sont retirés de l'historique renvoyé à chaque étape (par défaut : `96000`, minimum `8000`). Plus elle est petite, moins on consomme de jetons d'entrée |
| `--no-compact` | Garde les anciens résultats d'outils en entier (par défaut, ceux au-delà des 6 derniers sont coupés à 1 500 caractères dans l'historique, et l'agent peut relancer l'outil) |
| `--all-tools` | Décrit tous les outils au modèle à chaque étape (par défaut, seuls ceux utiles à la demande sont décrits, voir [Outils disponibles](#outils-disponibles)) |
| `--no-distill` | Ne résume pas la session à la fin d'une exécution (économise un appel LLM ; voir [Distillation de session](#distillation-de-session-entre-les-sessions)) |
| `--help`, `-h`, `/?` | Affiche l'aide et quitte |
| `--version` | Affiche la version de CSAgent et quitte |
| `--doc` | Affiche cette documentation dans une vue de terminal mise en forme et quitte |
| `<nom>` | Argument positionnel : nom de la mémoire, sans l'option `--mem` |

### Exemples

```bash
# Web UI avec une mémoire personnalisée (dossier my_project_memory/)
csagent --ui --mem my_project_memory

# Mode Lean UI
csagent --leanui

# Web UI sur un port personnalisé
csagent --ui --port 8080

# Mode CLI avec une mémoire précise (dossier my_memory/)
dotnet run my_memory

# Une seule demande, puis on quitte
csagent --prompt "hello my name is John"

# Une seule demande, avec une mémoire précise et sans confirmation
csagent my_memory --prompt "list the files here" --yes

# Quel modèle serait choisi ? (aucun appel de modèle)
csagent --explain-routing "open in Edge browser"

# Mode dry run
csagent --dry-run

# Afficher la version
csagent --version

# Afficher la documentation dans le terminal
csagent --doc

# Changer le modèle LLM en mode CLI
csagent --model gpt-4o-mini

# Changer le modèle LLM en mode Web UI
csagent --ui --model deepseek-v4-flash

# Régler le comportement des nouvelles tentatives en cas de limite de débit
csagent --max-retries 5 --retry-delay 2000

# Ne pas résumer la session à la fin de l'exécution
csagent --no-distill

# Serveur SSE sans interface pour un orchestrateur, sans confirmation
csagent --api --yes

# Idem, accessible depuis le réseau (clé d'API obligatoire)
csagent --api --yes --host 0.0.0.0 --port 8080 --api-key s3cret
```

---

## Fonctions de sécurité

CSAgent comporte plusieurs niveaux de sécurité pour éviter d'endommager accidentellement votre système :

### 1. Confirmation des actions destructrices

L'outil `write_file` est classé comme **destructeur**, car il modifie des fichiers sur le disque. Avant de l'exécuter, l'agent demande confirmation :

```
[?] Allow destructive action 'write_file'? [Y/n]
```

Avec `--yes` (ou `-y`), ces demandes sont ignorées et chaque appel d'outil est approuvé automatiquement, ce dont un orchestrateur sans surveillance a besoin (voir [Mode API sans interface](#mode-api-sans-interface-pour-orchestrateurs)). Les deux filtres ci-dessous restent actifs.

Les commandes shell (`sh`) ne sont **pas** classées comme destructrices par défaut, mais elles sont tout de même filtrées pour les opérations dangereuses (voir plus bas).

### 2. Restriction des chemins

Les opérations sur les fichiers (`write_file`, `read_file`, `list_dir`) sont **limitées au répertoire de travail courant** et à ses sous-répertoires. Toute tentative d'accès à des fichiers en dehors de ce périmètre est bloquée :

```
Error: write_file - Path 'C:\Windows\System32\config' is not allowed for writing.
```

### 3. Filtrage des commandes dangereuses

Les commandes de `sh`, `run_terminal` et `excel_command` sont analysées avant exécution à la recherche de motifs potentiellement dangereux. Le filtre **dépend de la plateforme** :

#### Windows (cmd.exe)
Les motifs bloqués comprennent :
- `format` — Formatage de disques
- `del /f` / `del /s` — Suppression forcée ou récursive
- `rd /s` / `rmdir /s` — Suppression récursive de répertoires
- `reg delete` / `reg add` / `reg import` — Manipulation du registre
- `net user` / `net localgroup` / `net share` — Administration du système
- `takeown` / `icacls` / `cacls` — Changements de droits et de propriétaire
- `bcdedit` / `diskpart` — Configuration du démarrage et des disques
- `runas` / `powershell start-process -verb runas` — Élévation de privilèges
- `shutdown` / `reboot` — Contrôle du système
- `\windows\system32\` / `\windows\system\` — Accès aux répertoires système
- `\program files\` — Accès à un répertoire protégé

#### Unix/Linux/macOS (bash/sh)
Les motifs bloqués comprennent :
- `sudo` — Élévation de privilèges
- `chmod` — Changements de droits
- `shutdown` / `reboot` — Contrôle du système
- `dd` — Opérations disque de bas niveau
- `mkfs` — Création de système de fichiers
- `/etc/` / `/usr/bin/` / `/bin/` — Accès aux répertoires système

**Comment le filtre lit la commande.** Il cherche ces motifs dans le texte de la commande, y compris entre guillemets. Un motif qui commence par une lettre ne compte qu'en **début de mot** : `format c:` est bloqué, mais pas `.NumberFormat = "0.00"` (Excel), et `dd if=…` est bloqué, mais pas `git add fichier`. Quand une commande est refusée, le message **nomme le motif** (`blocked pattern 'format'`) pour que l'agent puisse la réécrire. Un texte qui contient le motif, par exemple une cellule Excel qui vaut « format c: », est bloqué lui aussi : il faut alors le reformuler.

### 4. Délai maximal des commandes

Toutes les commandes shell ont un **délai maximal de 60 secondes**. Si une commande dure plus longtemps, elle est automatiquement arrêtée :

```
Error: command timed out (60s).
```

### 5. Limite de taille des fichiers

La lecture de fichiers de plus de **500 Ko** est bloquée pour éviter les problèmes de mémoire :

```
Error: file too large (1024 KB). Use sh to grep/head.
```

---

## Outils disponibles

L'agent dispose de **38 outils intégrés**. Vous ne les appelez pas vous-même : l'agent choisit ceux dont il a besoin pour votre demande. Dans les tableaux, `*` marque un paramètre obligatoire et **Confirmation** indique que l'outil demande votre accord avant de s'exécuter (sauf avec `--yes`).

**Seuls les outils utiles sont décrits au modèle.** La description des 38 outils pèse environ 20 000 caractères et serait renvoyée à chaque étape. Depuis la 0.9.8, les outils sont répartis en groupes :

| Groupe | Outils | Envoyé |
|---|---|---|
| `core` | fichiers (`read_file`, `write_file`, `edit_file`, `copy_file`, `move_file`, `delete_file`, `read_json`, `parse_output`), `list_dir`, `tree`, `search_files`, `sh` | toujours |
| `git` | `git_status`, `git_diff`, `git_log`, `git_branch`, `git_commit` | si la demande parle de git, de commit, de branche… |
| `web` | `web_search`, `fetch_url`, `http_request`, `download_file` | si elle parle du web, d'une URL, d'une recherche… |
| `mail` | `read_msg`, `save_attachment`, `group_msg_by_sender`, `send_email` | e-mail, Outlook, `.msg`, expéditeur… |
| `calendar` | `read_calendar`, `create_appointment` | agenda, calendrier, rendez-vous, réunion, créneau, meeting… |
| `audio` | `transcribe_audio` | audio, enregistrement, transcription, `.mp3`… |
| `office` | `excel_command`, `close_excel` | Excel, tableur, `.xlsx`… |
| `clipboard` | `read_clipboard`, `write_clipboard` | presse-papiers, « ce que j'ai copié »… |
| `archive` | `zip`, `unzip` | archive, zip… |
| `terminal` | `run_terminal`, `close_terminal` | terminal interactif, serveur de dev… |
| `models` | `switch_model`, `list_models` | modèle, LLM… |

Les mots sont cherchés en français et en anglais dans le dernier message de l'utilisateur. Un groupe déjà utilisé dans la conversation reste actif, et un outil appelé alors que son groupe était éteint le réactive pour la suite. Si un groupe manque, l'agent le rallume avec l'outil `enable_tools` (décrit en une ligne, avec les groupes éteints) et appelle l'outil à l'étape suivante. Les outils d'un serveur MCP sont tous envoyés tant que leurs descriptions tiennent en 4 000 caractères ; au-delà, ils ne le sont que si la demande dit « MCP » ou nomme l'un d'eux, ou si l'agent les active avec `enable_tools`. `--all-tools` rétablit l'envoi de tous les outils.

#### Fichiers

| Outil | Rôle | Paramètres | Confirmation |
|---|---|---|---|
| `read_file` | Lit un fichier texte | `path`* | non |
| `read_json` | Lit un fichier JSON, ou une partie | `path`*, `query` (chemin pointé, par exemple `a.b[0].c`) | non |
| `write_file` | Écrit ou écrase un fichier texte | `path`*, `content`* | **oui** |
| `edit_file` | Remplace des morceaux exacts d'un fichier, tout ou rien | `path`*, `edits`* (liste de `old_string` / `new_string`) | **oui** |
| `list_dir` | Liste un répertoire | `path`, `recursive` | non |
| `tree` | Arborescence indentée (ignore les dossiers cachés, `bin/` et `obj/`) | `path`, `depth` (`-1` : illimité) | non |
| `search_files` | Cherche un texte (sans tenir compte de la casse) dans les fichiers | `pattern`*, `path`, `glob` (par exemple `*.cs`) | non |
| `copy_file` | Copie un fichier | `source`*, `destination`* | non |
| `move_file` | Déplace ou renomme un fichier | `source`*, `destination`* | **oui** |
| `delete_file` | Supprime définitivement un fichier | `path`* | **oui** |
| `zip` | Crée une archive zip d'un fichier ou d'un dossier | `source`*, `destination`* | non |
| `unzip` | Extrait une archive zip (écrase les fichiers existants) | `archive`*, `destination`* | **oui** |

#### Commandes

| Outil | Rôle | Paramètres | Confirmation |
|---|---|---|---|
| `sh` | Exécute une commande shell (`cmd.exe` sous Windows, `/bin/sh` ailleurs) | `cmd`* | non (mais filtrée) |
| `run_terminal` | Commande dans un shell persistant : le dossier et les variables sont conservés entre les appels | `cmd`*, `session`, `timeoutMs` (60 000 par défaut, 300 000 au maximum) | non (mais filtrée) |
| `close_terminal` | Ferme une session de `run_terminal` | `session` | non |
| `parse_output` | Transforme une sortie de commande en JSON | `output`*, `format` (`json`, `keyvalue`, `csv`, `auto`), `query` | non |

#### Git

| Outil | Rôle | Paramètres | Confirmation |
|---|---|---|---|
| `git_status` | État du dépôt | `path` | non |
| `git_diff` | Modifications non validées (ou indexées) | `path`, `staged` | non |
| `git_log` | Historique récent | `path`, `count` (20 par défaut) | non |
| `git_branch` | Branche courante et branches locales | `path` | non |
| `git_commit` | Indexe tout et crée un commit | `path`, `message`* | **oui** |

#### Web

| Outil | Rôle | Paramètres | Confirmation |
|---|---|---|---|
| `web_search` | Recherche sur le web | `query`*, `maxResults` (5 par défaut, 10 au maximum) | non |
| `fetch_url` | Lit une page web en texte | `url`*, `maxChars` (20 000 par défaut, 100 000 au maximum) | non |
| `http_request` | Requête HTTP quelconque | `url`*, `method`, `headers`, `body`, `timeoutMs` (30 000 par défaut, 120 000 au maximum) | non |
| `download_file` | Enregistre la réponse HTTP directement dans un fichier, sans la faire passer par le modèle (détails plus bas) | `url`*, `destination`*, `json_path`, `pretty`, `method`, `headers`, `body`, `timeoutMs` | **oui** |

#### Courrier, audio, presse-papier et Excel

| Outil | Rôle | Paramètres | Confirmation |
|---|---|---|---|
| `read_msg` | Lit un e-mail Outlook `.msg` (détails plus bas) | `path`*, `max_chars` | non |
| `save_attachment` | Enregistre les pièces jointes d'un `.msg` | `path`*, `index`, `destination` | **oui** |
| `group_msg_by_sender` | Renomme les `.msg` d'un dossier en noms ASCII, puis les range dans un sous-dossier par expéditeur (détails plus bas) | `directory`, `group_by`, `rename` | **oui** |
| `read_calendar` | Liste les rendez-vous de l'agenda Outlook pour une période (Windows, Outlook classique ; détails plus bas) | `start`, `end`, `query`, `max_items` | non |
| `create_appointment` | Crée un rendez-vous ou une réunion dans l'agenda Outlook : affiché pour relecture par défaut (Windows, Outlook classique ; détails plus bas) | `subject`*, `start`*, `end`, `duration_minutes`, `all_day`, `location`, `body`, `attendees`, `reminder_minutes`, `save`, `send` | **oui** |
| `send_email` | Compose un e-mail avec Outlook (Windows, Outlook classique) : brouillon affiché par défaut | `to`*, `subject`*, `cc`, `bcc`, `body`, `bodyHtml`, `attachments`, `send`, `saveToDrafts` | **oui** |
| `transcribe_audio` | Transcrit un enregistrement avec Whisper (détails plus bas) | `path`*, `language`, `max_chars` | non |
| `read_clipboard` | Lit le texte du presse-papier | aucun | non |
| `write_clipboard` | Remplace le contenu du presse-papier | `content`* | **oui** |
| `excel_command` | Commande PowerShell dans une session Excel persistante (Windows) | `command`*, `session`, `timeoutMs` | non |
| `close_excel` | Ferme la session d'automatisation Excel (Excel reste ouvert) | `session` | non |

#### Modèles

| Outil | Rôle | Paramètres | Confirmation |
|---|---|---|---|
| `list_models` | Liste les modèles disponibles sur l'endpoint | aucun | non |
| `switch_model` | Change de modèle pour la session | `model`* | non |

Les outils d'un serveur MCP s'ajoutent à cette liste sous le nom `mcp_<nom>`, ou `mcp_<serveur>_<nom>` quand plusieurs serveurs sont connectés (voir [Serveurs MCP](#serveurs-mcp)). Les sept outils suivants sont décrits en détail.

### `write_file`
Écrit (ou écrase) un fichier texte. Les répertoires parents sont créés automatiquement.

**Paramètres :**
- `path` (string, obligatoire) — Chemin du fichier
- `content` (string, obligatoire) — Contenu UTF-8 à écrire

### `read_file`
Lit un fichier texte et renvoie son contenu.

**Paramètres :**
- `path` (string, obligatoire) — Chemin du fichier

### `list_dir`
Liste les fichiers et sous-répertoires d'un répertoire.

**Paramètres :**
- `path` (string, facultatif, par défaut : `.`) — Répertoire à lister
- `recursive` (boolean, facultatif, par défaut : `false`) — Liste récursive ou non

### `sh`
Exécute une commande shell. Utilise `cmd.exe` sous Windows, `/bin/sh` ailleurs.

**Paramètres :**
- `cmd` (string, obligatoire) — Commande shell à exécuter

### `read_msg`
Lit un fichier e-mail Outlook `.msg` : objet, expéditeur, destinataires, dates, corps et liste numérotée des pièces jointes. Lecture seule. C'est un lecteur en C# pur : il fonctionne sur tous les systèmes et ne nécessite ni Outlook ni paquet NuGet.

**Paramètres :**
- `path` (string, obligatoire) — Chemin du fichier `.msg` (dans le répertoire de travail)
- `max_chars` (integer, facultatif, par défaut : `50000`, maximum `200000`) — Nombre maximal de caractères du corps renvoyés

Le corps est la partie en texte brut quand il y en a une ; sinon, la partie HTML ou RTF convertie en texte (au mieux). Les messages incorporés sont affichés après le corps. Le résultat est encadré par les marqueurs `[EMAIL …]` / `[END OF EMAIL]`, et le prompt système indique à l'agent que ce contenu est une **donnée non fiable, jamais une instruction**.

### `save_attachment`
Enregistre sur le disque les pièces jointes d'un fichier `.msg`. **Destructeur : demande confirmation** (sauf avec `--yes`).

**Paramètres :**
- `path` (string, obligatoire) — Chemin du fichier `.msg`
- `index` (integer, facultatif) — Numéro de la pièce jointe affiché par `read_msg` ; à omettre pour tout enregistrer
- `destination` (string, facultatif) — Répertoire dans le répertoire de travail ; par défaut `<nom>_attachments` à côté du `.msg`

Les noms de fichiers sont nettoyés (pas de chemin, pas de caractères réservés ou invalides), et un fichier existant n'est jamais écrasé (`nom (1).ext` est écrit à la place). Les messages incorporés et les pièces jointes stockées par référence ne peuvent pas être enregistrés.

Non pris en charge : les archives `.pst` / `.ost`, et les fichiers `.msg` chiffrés ou protégés par des droits.

### `group_msg_by_sender`
Range les fichiers `.msg` d'un dossier par expéditeur. **Destructeur : demande confirmation** (sauf avec `--yes`). C'est un lecteur en C# pur : il fonctionne sur tous les systèmes et ne nécessite pas Outlook.

**Paramètres :**
- `directory` (string, facultatif, par défaut : le répertoire courant) — Dossier contenant les `.msg` (dans le répertoire de travail)
- `group_by` (string, facultatif, `name` par défaut) — `name` : le nom affiché de l'expéditeur ; `email` : son adresse, qui distingue deux personnes de même nom
- `rename` (boolean, facultatif, `true` par défaut) — Renomme d'abord les fichiers en noms ASCII ; `false` garde les noms d'origine

Deux étapes, dans cet ordre :
1. **Renommage.** Chaque fichier reçoit un nom sans accents, espaces ni caractères spéciaux (`Rapport trimestriel é.msg` devient `Rapport_trimestriel_e.msg`), pour que tous les outils puissent l'adresser sans problème de guillemets ou d'encodage. Les lettres latines accentuées sont ramenées à leur forme simple (é → e, ß → ss) ; tout autre caractère devient `_`.
2. **Rangement.** Le nom ou l'adresse de l'expéditeur est lu dans le message ; le fichier est déplacé dans un sous-dossier portant ce nom, lui aussi en ASCII (`Jérôme Dupont` → `Jerome_Dupont`). Un message sans expéditeur va dans `UnknownSender`.

Seuls les `.msg` placés directement dans le dossier sont traités. Rien n'est jamais écrasé (`nom (1).msg` est écrit à la place). Un fichier illisible reste en place (renommé) et est signalé dans le compte rendu, qui indique aussi le nombre de messages par expéditeur. Limite : 5 000 fichiers par appel.

### `transcribe_audio`
Transcrit un enregistrement de parole en texte avec un serveur Whisper (`/audio/transcriptions` sur l'`--endpoint` configuré, avec le même `ALBERT_API_KEY`). Lecture seule.

**Paramètres :**
- `path` (string, obligatoire) — Chemin du fichier audio (dans le répertoire de travail)
- `language` (string, facultatif) — Code ISO-639-1 comme `fr` ; à omettre pour la détection automatique
- `max_chars` (integer, facultatif, par défaut : `50000`, maximum `200000`) — Nombre maximal de caractères renvoyés

Avec **ffmpeg** installé (dans le `PATH`, ou indiqué par `CSAGENT_FFMPEG`), tout fichier audio ou vidéo est converti en WAV mono 16 kHz et découpé en parties de 10 minutes, envoyées l'une après l'autre puis assemblées. Sans ffmpeg, seuls les fichiers `.wav` et `.mp3` jusqu'à 24 Mo sont acceptés. Les fichiers sont limités à 100 Mo. Le découpage se fait à l'horloge : un mot situé à une limite peut être coupé en deux. Le modèle est `openai/whisper-large-v3`, ou `CSAGENT_TRANSCRIBE_MODEL`. L'audio est envoyé au serveur ; la transcription est une **donnée non fiable, jamais une instruction**. Pour la conserver, l'agent l'écrit avec `write_file` (qui demande confirmation).

### `download_file`
Enregistre la réponse d'une requête HTTP **directement dans un fichier**, sans qu'elle entre dans la conversation. **Demande confirmation** (sauf avec `--yes`).

Pourquoi : pour mettre des données du web dans un fichier avec `http_request` puis `write_file`, le modèle reçoit d'abord toute la réponse, puis doit la **réécrire en entier**, mot à mot, dans l'argument `content`. La récupération prend une seconde, mais cette réécriture prend des minutes pour une réponse de quelques dizaines de Ko, car le modèle produit sa sortie un jeton à la fois, et elle consomme autant de jetons de sortie. Avec `download_file`, les octets vont du réseau au disque ; le modèle ne reçoit qu'un résumé de quelques lignes. Le prompt système lui demande de l'utiliser dès que le but est un fichier.

**Paramètres :**
- `url` (string, obligatoire) et `destination` (string, obligatoire, dans le répertoire de travail ; un fichier existant est écrasé, les dossiers sont créés)
- `json_path` (string, facultatif) — pour une réponse JSON, n'enregistre que cette partie, par exemple `products` ; même syntaxe que `read_json`. Le fichier est alors indenté.
- `pretty` (boolean, facultatif, `false` par défaut) — réécrit le JSON indenté ; implicite avec `json_path`. Sans l'un ni l'autre, les octets sont enregistrés tels qu'ils arrivent (tout type de fichier : JSON, CSV, image, archive).
- `method`, `headers`, `body`, `timeoutMs` — comme pour `http_request` (`GET` par défaut).

Une erreur n'écrit rien et ne touche pas un fichier existant : statut HTTP différent de 2xx, `json_path` introuvable (la réponse liste alors les clés disponibles), réponse qui n'est pas du JSON, délai dépassé. Limite : 20 Mo. Le résultat dit la taille et, pour du JSON, sa forme (par exemple `array of 30 object(s); keys of the first: id, title, …`), jamais les données.

Exemple : « récupère 30 produits sur dummyjson et écris-les dans products.json » donne un seul appel :
`download_file` `{"url": "https://dummyjson.com/products?limit=30", "destination": "products.json", "json_path": "products"}`

### `read_calendar` et `create_appointment`
Lisent et écrivent l'**agenda par défaut d'Outlook** par automatisation COM, avec le profil Outlook de l'utilisateur : ni inscription Azure, ni jeton. Windows seulement, Outlook classique installé et configuré (comme `send_email`). Les dates sont des heures locales ISO-8601 **sans fuseau** (`2026-10-16`, `2026-10-16T14:00`), lues de la même façon quelle que soit la langue de la machine.

`read_calendar` (lecture seule) :
- `start` (string, facultatif, par défaut : aujourd'hui 00:00) et `end` (string, facultatif, par défaut : 7 jours après `start`, 366 jours au plus). Une date seule pour `end` inclut tout ce jour-là.
- `query` (string, facultatif) — texte à trouver dans l'objet ou le lieu, sans tenir compte de la casse.
- `max_items` (integer, facultatif, `50` par défaut, `200` au plus) — le reste est seulement compté.

Les rendez-vous récurrents sont développés et triés par heure. Une ligne par rendez-vous : date et heure, objet, lieu, organisateur d'une réunion, indicateurs (`recurring`, `meeting`, `free`). La première ligne donne la **date et l'heure du jour**, ce qui permet à l'agent de convertir « demain » ou « lundi prochain ». Les objets et les lieux viennent d'autres personnes : le résultat est encadré comme une donnée non fiable, jamais comme une instruction.

`create_appointment` (**demande confirmation**, sauf avec `--yes`) :
- `subject`* et `start`* ; `end`, ou `duration_minutes` (`60` par défaut, `1440` au plus) ; `all_day` (pour une journée entière, `start` peut être une date et `end` est le dernier jour inclus).
- `location`, `body` (texte), `reminder_minutes` (`0` supprime le rappel).
- `attendees` (adresses ou noms séparés par `;`) en fait une réunion, mais **aucune invitation ne part** sans `send: true`.
- Par défaut, le rendez-vous est **affiché dans Outlook pour relecture** et n'est pas enregistré. `save: true` l'enregistre sans l'ouvrir ; `send: true` l'enregistre et envoie les invitations (irréversible, et exige `attendees`).
- Les chevauchements avec des rendez-vous existants (hors « libre ») sont signalés, jamais bloquants.

Le calendrier d'un autre utilisateur ou une boîte partagée ne sont pas lus : seul l'agenda par défaut l'est.

### Serveurs MCP

CsAgent se connecte à un ou plusieurs serveurs MCP, en Streamable HTTP ou en stdio, et propose leurs outils au modèle à côté des outils intégrés. À la première demande, l'agent liste les outils de chaque serveur. Cela fonctionne dans tous les modes (CLI, `--ui`, `--leanui`, `--api`).

```
csagent --mcp http://localhost:8000/mcp
csagent --mcp https://api.exemple.com/mcp --mcp-header "Authorization: Bearer ${MCP_TOKEN}"
csagent --mcp-stdio "npx -y @modelcontextprotocol/server-filesystem ."
csagent --mcp-config mcp.json
```

- **HTTP.** `--mcp <url>` (ou `CSAGENT_MCP_URL`). `--mcp-header "Nom: valeur"` ajoute un en-tête à chaque requête (clé d'API, jeton `Bearer`…) et s'applique aux URL de `--mcp`.
- **stdio.** `--mcp-stdio "<commande>"` démarre le programme et échange des messages JSON, une ligne par message, sur son entrée et sa sortie standard. Le programme est arrêté à la fin de l'exécution. Sous Windows, une commande sans extension (`npx`) passe par `cmd.exe /c`.
- **Plusieurs serveurs.** Répétez `--mcp` et `--mcp-stdio`, ou décrivez-les dans un fichier. Les serveurs sont connectés en parallèle. Un serveur qui ne répond pas donne un avertissement et les autres restent utilisables ; l'exécution ne s'arrête que si aucun ne répond.
- **Fichier de configuration.** `--mcp-config <fichier>` (ou `CSAGENT_MCP_CONFIG`) lit un fichier JSON (commentaires et virgules finales acceptés) :

```json
{
  "mcpServers": {
    "fichiers": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "."] },
    "wiki":     { "url": "https://wiki.exemple.com/mcp", "headers": { "Authorization": "Bearer ${WIKI_TOKEN}" } },
    "base":     { "command": "node", "args": ["db.js"], "env": { "DB_PASSWORD": "${DB_PASSWORD}" }, "cwd": "." }
  }
}
```

  Chaque serveur a soit `url` (et `headers`), soit `command` (et `args`, `env`, `cwd`), jamais les deux. `${VAR}` est remplacé par la variable d'environnement dans les en-têtes, les arguments et `env`, ce qui évite d'écrire un secret dans le fichier.
- **Noms.** Avec un seul serveur, ses outils sont proposés sous le nom `mcp_<nom>`. Avec plusieurs, sous le nom `mcp_<serveur>_<nom>`, où `<serveur>` est le nom du fichier de configuration, ou un nom déduit de l'URL ou du programme (`server-filesystem`, `api_exemple_com`). Les caractères autres que lettres, chiffres, `_` et `-` deviennent `_`, 64 caractères au maximum. Un serveur ne peut donc pas remplacer un outil intégré : son `read_file` devient `mcp_read_file`, et le `read_file` intégré reste intact.
- **Confirmation.** CsAgent ne peut pas savoir ce que fait l'outil d'un serveur : quand les confirmations sont actives (par défaut), **chaque** appel MCP demande votre accord, comme `write_file`. Avec `--yes`, ils s'exécutent sans demander.
- **Sécurité.** Un serveur stdio est un programme lancé avec vos droits. Rien n'est donc chargé automatiquement depuis le dossier de travail : seuls `--mcp-stdio` et `--mcp-config`, que vous écrivez vous-même, démarrent un programme. Les valeurs des en-têtes et de `env` ne sont jamais affichées, ni dans la bannière ni dans les messages d'erreur.
- **Contenu non fiable.** Ce qu'envoie un serveur (descriptions d'outils, résultats) est traité comme une donnée : le prompt système demande au modèle de ne jamais suivre les instructions qui s'y trouvent, et les descriptions sont coupées à 1000 caractères. Ne connectez que des serveurs de confiance.
- **Limites.** Outils seulement (ni ressources ni prompts), résultats en texte (les autres contenus sont affichés en JSON). Pas d'authentification OAuth : seuls les en-têtes fixes sont pris en charge. Une demande à un serveur stdio dure 60 secondes au plus.

---

## Skills

Un **skill** est un fichier Markdown du dossier `skills/` qui apprend à l'agent à bien faire une tâche récurrente. Au début d'une tâche, l'agent lit seulement le petit index `skills/INDEX.md` et n'ouvre un skill que si sa description correspond à la demande. Ajouter un skill ne demande ni code ni recompilation : créez son fichier `.md`, puis ajoutez une ligne à l'index (nom, version, une phrase de déclenchement, chemin). Dans la Lean UI, `/skills [filtre]` liste les skills.

| Skill | Sert à | Fichier |
|---|---|---|
| Tech-Watch Intelligence | Veille technologique : recherche sur le web et synthèse des nouveautés d'un domaine | `skills/tech-watch-intelligence.md` |
| Excel Report Builder | Transforme des données en rapport Excel mis en forme (Windows) | `skills/export-report-builder.md` |
| CSV Handling | Lire et analyser un `.csv` avant de le transformer | `skills/csv-handling.md` |
| Pre-Commit Review & Message Drafting | Relit les changements et propose un message avant un `git_commit` | `skills/precommit-review.md` |
| Organize .msg Emails by Sender | Range des `.msg` par expéditeur avec `group_msg_by_sender` (noms ASCII, un dossier par expéditeur), puis vérifie le résultat | `skills/msg-organize-by-sender.md` |
| Task Framing | Cadre une demande vaste ou floue avant de commencer | `skills/task-framing.md` |
| Plan Then Execute | Écrit puis suit un court plan avec une vérification par étape | `skills/plan-then-execute.md` |
| Final Verification | Vérifie le résultat avec des preuves avant la réponse finale | `skills/final-verification.md` |
| Failure Recovery | Diagnostique et change d'approche après deux échecs | `skills/failure-recovery.md` |
| Safe Batch File Operations | Renommer, déplacer ou supprimer beaucoup de fichiers sans risque | `skills/safe-batch-file-operations.md` |
| Product Owner Planning | Backlog, priorisation, feuille de route, risques et questions ouvertes à partir de documents, e-mails et enregistrements | `skills/po-planning.md` |

Le skill **Product Owner Planning** transforme des notes, des spécifications, des e-mails et des enregistrements en un backlog traçable (chaque story renvoie à sa source), priorisé et accompagné d'une feuille de route, de risques et de questions ouvertes, écrits dans `planning/`. Il n'invente ni exigence, ni date, ni chiffre.

---

## Mémoire et persistance de la conversation

CSAgent enregistre l'historique de la conversation, ainsi que les fichiers de son système de mémoire, dans un **dossier de mémoire** (par défaut : `agent_memory/`). Cela permet à l'agent de garder le contexte d'une session à l'autre.

- La mémoire est chargée automatiquement au démarrage de l'agent
- Elle est enregistrée après chaque étape
- Les anciens messages sont élagués quand le contenu total dépasse environ 96 Ko, pour garder un contexte maîtrisable
- Vous pouvez choisir une autre mémoire avec `--mem <nom>` ou comme argument positionnel ; le nom est celui du dossier

### Mémoire hybride (anti-amnésie)

L'élagage des anciens messages a un effet secondaire : l'agent oublie ce qu'il a déjà essayé et peut répéter les mêmes erreurs. Pour l'éviter, CSAgent ajoute une **mémoire hybride** au-dessus du fichier de conversation. Elle est coordonnée par `HybridMemoryManager` et n'utilise que la bibliothèque de classes de base de .NET (ni paquet NuGet, ni base de données).

| Couche | Ce qu'elle stocke | Rôle |
|---|---|---|
| **ExactMemory** | Les 50 dernières étapes (réflexions, appels d'outils, erreurs, réussites), mot pour mot | Montre à l'agent exactement ce qu'il vient d'essayer |
| **SemanticMemory** | Des schémas erreur / solution, étiquetés (par exemple `permission`, `not-found`, `timeout`, `syntax`) | Explique *pourquoi* quelque chose a échoué et ce qui a fonctionné à la place |

Exemple : l'écriture de `/opt/config.json` échoue avec *permission denied*, puis celle de `/home/config.json` réussit. Les deux faits sont enregistrés, de sorte que lors d'une écriture semblable, l'agent se voit rappeler l'emplacement qui a fonctionné.

À partir de l'étape 4, les entrées pertinentes sont injectées dans un unique message `[MEMORY]` juste avant chaque appel au LLM (le précédent est remplacé, jamais empilé), si bien qu'elles restent visibles même après que `TrimHistory()` a supprimé les anciens messages.

**Comment les leçons sont retrouvées.** La requête est construite à partir du dernier texte de l'agent, de l'appel d'outil qu'il s'apprête à faire (nom et arguments) et du dernier message de l'utilisateur. Elle est réduite à des mots-clés (mots vides anglais et français retirés, accents ramenés à la lettre de base, pluriels ramenés au singulier, chemins coupés sur `/`), et chaque leçon stockée marque un point par mot-clé qu'elle contient. Les trois meilleures leçons sont injectées, d'abord par meilleur score, puis par date la plus récente, puis par fréquence la plus élevée.

**Comment le stockage reste petit et utile.**
- *Dédoublonnage* : une même leçon (sans tenir compte de la casse, des accents, des espaces superflus ni des nombres comme les numéros de ligne) n'est stockée qu'une fois ; un compteur et une date de dernière occurrence sont mis à jour, et le prompt affiche `(seen 3x)`.
- *Plafond* : 200 leçons au maximum sont conservées ; les moins vues et les plus anciennes sont évincées en premier.
- *Solutions seulement après une erreur* : une réussite ne devient une leçon que si le même outil a échoué dans les 5 étapes précédentes. Seuls les arguments sont stockés, jamais le résultat, de sorte que le contenu des fichiers n'arrive pas dans la mémoire à long terme.
- *Les messages d'erreur* sont coupés à 300 caractères.

**Enregistrement sûr.** Les fichiers de mémoire sont écrits de façon atomique (fichier temporaire, puis remplacement), si bien qu'un plantage ou un lecteur simultané ne voit jamais un fichier à moitié écrit. Un fichier corrompu est renommé en `<nom>.bad` et l'agent démarre avec une mémoire vide au lieu d'échouer ; une entrée mal formée est ignorée et le reste est conservé. L'enregistrement se fait dans un bloc `finally` autour de la boucle de l'agent : la mémoire est donc écrite quelle que soit la façon dont l'exécution se termine (tâche terminée, réponse en texte seul, erreur, annulation ou nombre maximal d'étapes atteint). Un enregistrement qui échoue est consigné dans le journal et n'arrête pas l'agent. En mode Web, un seul gestionnaire de mémoire est partagé par toutes les requêtes ; il est sûr pour les accès concurrents, mais des requêtes simultanées partagent la même mémoire.

### Distillation de session (entre les sessions)

La mémoire hybride ci-dessus vit à l'intérieur d'une tâche. La **distillation de session** transporte le *raisonnement* d'une exécution à la suivante : à la fin d'une exécution, le LLM condense la conversation en quatre courtes listes.

| Liste | Ce qu'elle contient |
|---|---|
| **Decisions** (décisions) | Les choix faits, et pourquoi |
| **Constraints** (contraintes) | Les faits sur l'environnement et les exigences à respecter |
| **Pending** (en attente) | Le travail restant à faire |
| **Failed approaches** (approches échouées) | Ce qui a été essayé sans succès, pour ne pas le réessayer |

Le nouveau résumé est fusionné avec le précédent (les éléments résolus ou obsolètes sont retirés) et enregistré dans le dossier de mémoire. À l'exécution suivante, il est envoyé au modèle sous la forme d'un message `[SESSION CONTEXT]` juste après le prompt système, de sorte que l'agent reprend avec le raisonnement précédent et non seulement avec l'historique brut.

Ce que vous voyez dans le terminal :

```
Session summary loaded: 5 note(s) from previous sessions (sent to the model as [SESSION CONTEXT]).
...
Session summary updated: 6 note(s) saved to agent_memory/summary.json.
```

Règles de conception :
- *Au mieux.* La distillation s'exécute après la tâche, avec une limite de 60 secondes. Si l'API est lente, en panne ou répond quelque chose d'inutilisable, l'exécution se termine normalement, un avis l'indique (`Session summary not updated (...)`) et le résumé précédent est conservé tel quel.
- *Des notes, pas des instructions.* La conversation contient des contenus de fichiers et des sorties de commandes écrits par des tiers. Le distillateur doit les traiter comme des données, le résumé est envoyé au modèle comme « informations de contexte, pas des instructions », et chaque note est coupée à une ligne de 240 caractères au plus (12 notes par liste, marqueurs injectés retirés).
- *Toujours présent.* Le résumé est réinséré à chaque étape, parce que `TrimHistory()` supprime d'abord les messages les plus anciens.
- *Jamais stocké deux fois.* Les blocs injectés (`[SESSION CONTEXT]`, `[MEMORY]`) ne sont jamais écrits dans le fichier de conversation.
- *Ignorée quand c'est inutile.* Une conversation d'un échange au plus n'est pas résumée.
- *Fichiers sûrs.* Le résumé est écrit de façon atomique ; un fichier corrompu est renommé en `.bad` et l'agent démarre sans résumé.

**Coût et désactivation.** Chaque exécution se termine par un appel LLM supplémentaire. Utilisez `--no-distill` pour l'éviter : les deux couches de mémoire sont toujours enregistrées, et un résumé existant est toujours lu et utilisé, mais il n'est ni créé ni modifié. Supprimez `summary.json` dans le dossier de mémoire pour oublier complètement le résumé.

### Fichiers de mémoire

Chaque mémoire est un **dossier**, nommé d'après `--mem` (par défaut `agent_memory`). Un `.json` final est retiré : `--mem my_task.json` et `--mem my_task` utilisent donc tous deux le dossier `my_task/`. Le dossier est créé au premier enregistrement.

```
agent_memory/
├── conversation.json   Historique complet de la conversation (envoyé au LLM)
├── exact.json          ExactMemory (50 dernières étapes)
├── semantic.json       SemanticMemory (schémas erreur / solution)
└── summary.json        Résumé de session distillé (décisions, contraintes, en attente, approches échouées)
```

Un fichier qui n'a pas pu être lu est conservé à côté des autres sous le nom `<nom>.bad` (par exemple `semantic.json.bad` ; sans danger à supprimer).

Pour démarrer une **nouvelle tâche sans rapport**, utilisez un autre nom `--mem` (ou supprimez le dossier). Pour **continuer** une tâche, gardez le même nom.

> **Mise à jour depuis la version 0.7.0 ou antérieure.** L'ancienne organisation (`agent_memory.json`, `agent_memory.json.exact.json`, `agent_memory.json.semantic.json`, `agent_memory.json.semantic.json.summary.json`, tous côte à côte) n'est plus lue et il n'y a pas de migration automatique : l'agent démarre avec une mémoire vide. Pour conserver une ancienne mémoire, déplacez ses fichiers dans le nouveau dossier et renommez-les avec les quatre noms ci-dessus.

### Organisation du code

```
src/
├── Core/Agent/CodingAgent.cs       # Boucle de l'agent ; reçoit HybridMemoryManager (7e paramètre du constructeur)
├── Core/Llm/                       # Choix du modèle : ModelRouter, RoutingRules, Loc (traductions)
├── Services/
│   ├── ExactMemory.cs
│   ├── SemanticMemory.cs
│   ├── HybridMemoryManager.cs
│   ├── TextTokenizer.cs            # Extraction de mots-clés (EN + FR)
│   ├── SessionSummary.cs           # Les quatre listes d'une session distillée
│   └── SummaryMemory.cs            # Distille, enregistre et charge le résumé de session
├── Presentation/
│   ├── Tui/TuiHost.cs              # Crée et transmet le gestionnaire de mémoire ; mode --prompt (ScriptObserver : sortie réduite à la réponse)
│   └── Web/
│       ├── ApiEndpoints.cs         # Idem, pour la Web / Lean UI
│       └── ApiHost.cs              # Serveur --api sans interface (contrôle de la clé, pas d'UI)
├── LLMRoutingRules/                # Fichiers JSON de routage modifiables (copiés à la publication)
└── Tests/                          # Projet de tests (voir Tests)
```

---

## Compilation depuis les sources

### Prérequis

- [SDK .NET 10.0](https://dotnet.microsoft.com/download/dotnet/10.0) ou supérieur

### Compiler

```bash
dotnet build
```

### Exécuter

```bash
# Mode CLI
set ALBERT_API_KEY=votre-cle
csagent

# Mode Web UI
set ALBERT_API_KEY=votre-cle
csagent --ui
```

---

## Tests

Le dossier `Tests/` contient un projet de tests pour le système de mémoire (mémoire hybride, distillation de session, enregistrements atomiques, `--no-distill`), la boucle de l'agent, le choix du modèle et ses règles JSON. Il utilise **MSTest** (paquets NuGet réservés aux tests ; l'application elle-même n'a aucune dépendance). Un faux serveur compatible OpenAI remplace le LLM : aucune clé d'API ni accès réseau n'est nécessaire.

Dans Visual Studio, ouvrez l'**Explorateur de tests** et choisissez **Exécuter tous les tests** : chaque test apparaît sur sa propre ligne. Les mêmes tests s'exécutent aussi depuis la console :

```bash
dotnet run --project Tests -c Release
```

La sortie se termine par une ligne de synthèse (`TOTAL 187 | PASS 187 | FAIL 0 | FINDINGS 0`), le code de sortie du processus est `0` quand tout passe et `1` sinon, et un rapport complet est écrit dans `results.txt` à côté du binaire de test.

Le projet compile directement les sources `Core/`, `Services/` et `Shared/` de l'application (l'application est un projet Web / NativeAOT) avec la sérialisation JSON par réflexion désactivée, ce qui reproduit la contrainte AOT : un `JsonSerializer.Serialize<T>()` égaré fait échouer les tests, comme il ferait échouer le binaire publié.

| Fichier | Couvre |
|---|---|
| `MemoryLayerTests.cs` | Bases d'ExactMemory, SemanticMemory et HybridMemoryManager |
| `SemanticTests.cs` | Recherche par mots-clés (EN + FR), dédoublonnage, plafond de 200 entrées, solutions seulement après une erreur |
| `PersistenceTests.cs` | Enregistrements atomiques, chargement tolérant, mise en quarantaine `.bad`, fichier de conversation |
| `MemoryFolderTests.cs` | Nom du dossier de mémoire (`--mem`), création au premier enregistrement, les quatre fichiers d'une exécution |
| `ConcurrencyTests.cs` | Un gestionnaire de mémoire partagé par de nombreuses requêtes parallèles |
| `DistillationTests.cs` | Résumé de session : enregistrement, cas d'échec, nettoyage, injection à chaque étape |
| `NoDistillTests.cs` | Analyse et comportement de `--no-distill` |
| `ScriptModeTests.cs` | `--prompt` : seule la réponse finale sur la sortie standard, erreurs et avertissements sur la sortie d'erreur, confirmation refusée si l'entrée est redirigée, `--verbose` |
| `QuietModeTests.cs` | `--quiet` : ce que la CLI affiche (et masque), appels en échec, demandes de confirmation ; le même filtre pour les interfaces web (`QuietObserver`) |
| `LlmEndpointTests.cs` | `--endpoint`, `--vision-model`, serveurs locaux et clé d'API |
| `ModelRouterTests.cs` | Choix automatique du modèle (code / chat / vision) : messages généraux ou de code en français et en anglais, priorité de `--model`, suites après des outils, `--no-route`, serveurs personnalisés, et vérification dans la liste des modèles du serveur (cas inconnu, indisponible, non texte et hors ligne) |
| `RoutingRulesTests.cs` | Dossier `LLMRoutingRules` (`models.json`, `keywords.json`, `rules.json`) : valeurs par défaut, erreurs signalées sans jamais bloquer, recherche du dossier et rechargement, `--init-routing`, `--explain-routing` ; messages en anglais et en français ; option `--prompt` |
| `ToolSelectorTests.cs` | Choix des outils décrits au modèle : groupes, mots déclencheurs (FR et EN), groupes déjà utilisés, `enable_tools`, outils MCP, requêtes réelles envoyées au modèle, `--all-tools` |
| `GroupMsgTests.cs` | `group_msg_by_sender` : noms ASCII (accents, espaces, caractères spéciaux), un dossier par expéditeur, noms identiques, fichier illisible, rien d'écrasé, `rename` et `group_by`, dossiers refusés |
| `MsgTests.cs` / `MsgTestFile.cs` | Lecteur de fichiers Outlook `.msg` (un constructeur réservé aux tests écrit des fichiers `.msg` valides), conversion HTML/RTF en texte, `read_msg`, `save_attachment` et la sûreté des noms de fichiers |
| `TranscribeTests.cs` | `transcribe_audio` : parties envoyées dans l'ordre (faux Whisper, faux ffmpeg), erreurs, troncature, contrôles de chemin et de clé ; stockage de l'enregistreur web (`AudioRecordings`) |
| `McpTests.cs` | MCP : noms `mcp_` (pas d'ombre sur les outils natifs, collisions, 64 caractères), appels sous le nom propre du serveur, confirmation avant chaque appel MCP, l'outil natif l'emporte quand un serveur a le même nom (faux serveur MCP) |
| `CommandFilterTests.cs` | Filtre des commandes : `.NumberFormat` d'Excel et `git add` ne sont plus bloqués, les vraies commandes dangereuses le restent (au début, après `&` ou `;`, entre guillemets), le refus nomme le motif dans `sh`, `run_terminal` et `excel_command` |
| `DownloadTests.cs` | `download_file` (petit serveur HTTP local) : octets enregistrés tels quels, `json_path` et JSON indenté, résumé sans données, erreurs qui n'écrivent rien, refus avant tout appel réseau, confirmation, appel complet de l'agent (un seul appel, réponse courte au modèle) |
| `CalendarTests.cs` | Agenda Outlook (sans Outlook) : outils et confirmation, groupe `calendar` et mots déclencheurs, refus hors Windows, dates ISO, contrôles des arguments, PowerShell généré (dates à format fixe, texte échappé, affichage par défaut, `save`, `send`, journée entière), section du prompt système |
| `McpStdioTests.cs` | MCP en stdio (faux serveur lancé par les tests avec `--mcp-echo`) : démarrage et arrêt du programme, lignes parasites et notifications ignorées, serveur qui se ferme ou n'existe pas, plusieurs serveurs HTTP et stdio, noms `mcp_<serveur>_<outil>`, panne d'un seul serveur, en-têtes d'authentification et `${VAR}`, fichier de configuration, secrets absents des messages |
| `ApiModeTests.cs` | `--api`, `--yes`, `--host`, `--api-key` : analyse, politique d'hôte et de clé, authentification, approbation automatique dans la boucle de l'agent |
| `TestExplorer.cs` | Liste chaque test dans l'Explorateur de tests de Visual Studio |
| `AgentEndToEndTests.cs` | Exécutions complètes de `CodingAgent` contre le faux LLM |
| `MockServices.cs`, `TestKit.cs`, `Report.cs` | Faux serveur, observateur, petit exécuteur de tests, rapport |

---

## Publication AOT

CSAgent prend en charge la **compilation anticipée (AOT, Ahead-of-Time)** pour un démarrage rapide et un déploiement en fichier unique :

```bash
# Publier un binaire AOT en fichier unique
dotnet publish -c Release -r win-x64   # Windows
dotnet publish -c Release -r linux-x64 # Linux
dotnet publish -c Release -r osx-x64   # macOS
```

La compilation AOT produit un exécutable autonome, sans dépendance d'exécution. Le dossier `LLMRoutingRules/` est copié à côté de l'exécutable : c'est la configuration de départ du routage, que vous pouvez modifier (le dossier du répertoire courant passe en premier).

Comme la sérialisation JSON par réflexion est désactivée en AOT, n'utilisez **pas** `JsonSerializer.Serialize<T>()` / `Deserialize<T>()`. Tout le JSON (y compris les fichiers de mémoire hybride) est construit et lu avec `JsonNode`, `JsonObject`, `JsonArray` et `JsonValue`.

---

## Dépannage

### « API Key not set »
Vérifiez que la variable d'environnement `ALBERT_API_KEY` est définie avant de lancer l'agent.

### « API 401: ... »
Votre clé d'API est invalide ou a expiré. Vérifiez vos identifiants.

### « API 429: ... »
Vous avez atteint la limite de débit. CSAgent réessaie désormais automatiquement avec un délai exponentiel (en respectant l'en-tête `Retry-After` du serveur quand il est présent). Si l'erreur persiste après toutes les tentatives, l'API vous limite toujours : attendez un moment et réessayez. Vous pouvez régler ce comportement avec `--max-retries` et `--retry-delay` (voir [Arguments de la ligne de commande](#arguments-de-la-ligne-de-commande)). Quand le quota **journalier** d'un modèle est épuisé, CSAgent ne réessaie pas : il passe à un autre modèle (voir `CSAGENT_FALLBACK_MODEL` dans les [variables d'environnement](#variables-denvironnement)). Détails, délais et exemples : [Gestion des erreurs 429](https://artydev.github.io/csagent/gestion-429.html).

### « API 407: ... » ou « Proxy Authentication Required »
Un proxy d'entreprise exige une authentification. Sous Windows, CSAgent présente l'identité de votre session Windows au proxy (le proxy du système est utilisé). Sous Linux et macOS, le proxy vient des variables `HTTP_PROXY`, `HTTPS_PROXY` et `NO_PROXY` ; pour un proxy à identifiant et mot de passe, mettez-les dans la variable : `HTTPS_PROXY=http://utilisateur:motdepasse@proxy:3128`. L'authentification intégrée (Kerberos) sous Linux demande un ticket valide (`kinit`) et la bibliothèque `libgssapi`.

### « command timed out (60s) »
La commande shell a duré plus de 60 secondes. Essayez de découper la tâche en étapes plus petites.

### « file too large »
Le fichier dépasse la limite de lecture de 500 Ko. Utilisez `sh` avec des outils comme `grep`, `head` ou `find` pour en inspecter des parties précises.

### « Path is not allowed »
Les opérations sur les fichiers sont limitées au répertoire de travail courant. Placez-vous dans le répertoire visé avant de lancer l'agent, ou utilisez des commandes shell pour copier les fichiers dans l'espace de travail.

### Le navigateur ne s'ouvre pas automatiquement
Allez manuellement sur **http://localhost:5050** dans votre navigateur (ou sur le port choisi avec `--port`).

### « Unsupported image type » / l'image ne se joint pas
Seules les images **PNG, JPEG, GIF et WebP** sont prises en charge, et le fichier doit faire **10 Mo ou moins**. Si vous joignez un autre format (par exemple BMP, TIFF, SVG), convertissez-le d'abord dans un format pris en charge.

### Mon message a été envoyé à un autre modèle que prévu
Lancez `csagent --explain-routing "votre message"` : il indique le modèle choisi et la raison, sans appeler de modèle. Les règles de `LLMRoutingRules/rules.json` sont testées avant la logique intégrée ; `--model` ou une image jointe au message les devancent, et `--no-route` les désactive.

### « LLMRoutingRules : … ; les valeurs intégrées sont utilisées »
Un fichier du dossier `LLMRoutingRules/` contient une erreur (JSON invalide, clé inconnue…). L'agent n'est pas arrêté : l'avertissement s'affiche une fois et les valeurs intégrées sont conservées pour ce fichier. Corrigez le fichier ; il est relu dès qu'il change.

### `csagent "du texte"` ne lance pas ma demande
Un argument seul, sans tiret, est le nom du dossier de mémoire. Pour envoyer une demande depuis la ligne de commande, utilisez `csagent --prompt "du texte"`.

### Les fichiers de mémoire (`exact.json`, `semantic.json`) ne sont pas créés
Les fichiers sont écrits dans le dossier de mémoire (`agent_memory/` par défaut, ou le nom donné avec `--mem`), créé au premier enregistrement, à la fin de chaque étape puis à la fin de l'exécution, quelle que soit la façon dont elle se termine. Vérifiez le nom du dossier et que vous avez le droit d'écrire dans le répertoire de travail. Si `semantic.json` reste vide, c'est normal tant qu'aucune erreur n'a été rencontrée : seules les erreurs et leurs solutions y sont retenues.

### « Session summary not updated » / « unchanged »
*not updated (timed out / API error)* : le modèle n'a pas répondu en 60 secondes, ou a répondu autre chose que le JSON attendu. Votre tâche n'est pas affectée et le résumé précédent est conservé ; une nouvelle tentative aura lieu à la fin de la prochaine exécution. Si cela arrive à chaque fois, vérifiez le modèle et l'API, ou utilisez `--no-distill`.
*unchanged* : le modèle a jugé qu'il n'y avait rien à retenir (typique d'une tâche triviale). C'est normal.

### Aucun fichier `summary.json` n'apparaît
La conversation était trop courte (prompt système plus un échange au plus), `--no-distill` a été utilisé, ou le modèle n'a rien renvoyé à conserver. Lancez une tâche qui utilise quelques outils et cherchez la ligne `Session summary updated` à la fin.

### Un fichier de mémoire a été renommé en `.bad`
Le fichier n'était pas du JSON valide (par exemple après une modification manuelle ou un problème de disque). L'agent a démarré avec une mémoire vide et a conservé le fichier endommagé sous le nom `<nom>.bad` dans le dossier de mémoire (par exemple `semantic.json.bad`). Corrigez-le ou supprimez-le ; rien d'autre n'est nécessaire.

### « Reflection-based serialization has been disabled »
Un appel à `JsonSerializer.Serialize<T>()` / `Deserialize<T>()` s'est glissé dans une compilation AOT. Remplacez-le par une construction manuelle avec `JsonNode` / `JsonObject` / `JsonArray`.

### « Expected multipart/form-data »
Cette erreur apparaît quand le point d'accès `/api/chat` est appelé avec un `POST` qui n'est pas en `multipart/form-data`. La Web UI et la Lean UI envoient automatiquement le bon type de contenu ; cela n'arrive généralement qu'avec un client écrit à la main.

---

## Licence

CsAgent est distribué sous **licence MIT** (voir le fichier [LICENSE](LICENSE)).

Copyright (c) 2026 Salvatore DI DIO

Il est entièrement construit sur la bibliothèque de classes de base de .NET, sans aucune dépendance NuGet.

---