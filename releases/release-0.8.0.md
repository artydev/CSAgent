# CsAgent 0.8.0 — notes de version

La version 0.8.0 ajoute la transcription de la parole (un outil pour l'agent et un enregistreur vocal dans les deux interfaces web) et rend MCP plus sûr. Elle fait suite à `csagent-v0.7.3`.

- [Résumé](#résumé)
- [Notes de mise à jour](#notes-de-mise-à-jour)
- [1. Transcription audio : `transcribe_audio`](#1-transcription-audio--transcribe_audio)
- [2. Enregistreur vocal dans les interfaces web](#2-enregistreur-vocal-dans-les-interfaces-web)
- [3. MCP : plus sûr par défaut](#3-mcp--plus-sûr-par-défaut)
- [Nouvelle configuration](#nouvelle-configuration)
- [Sécurité](#sécurité)
- [Limites](#limites)
- [Tests](#tests)

## Résumé

| Domaine | Ce qui change |
|---|---|
| Nouvel outil | `transcribe_audio` : enregistrement de parole vers texte (Whisper, via l'endpoint configuré) |
| `--ui` et `--leanui` | Bouton micro et choix de la langue (FR / EN / Auto) : enregistre, transcrit, puis tu choisis d'utiliser le texte comme instruction ou de le garder dans un fichier joint au prochain prompt |
| MCP | Les outils sont proposés sous le nom `mcp_<nom>`, **chaque appel MCP demande confirmation**, les résultats sont traités comme des données non fiables |
| Configuration | `CSAGENT_TRANSCRIBE_MODEL`, `CSAGENT_FFMPEG`, `CSAGENT_AUDIO_KEEP_DAYS`, `CSAGENT_MCP_URL` (désormais documentée) |
| Documentation | README : enregistreur vocal, serveurs MCP, description du mode lean. `--help` mentionne `--mcp` |

## Notes de mise à jour

À lire avant de mettre à jour si tu utilises MCP ou si tu pilotes CsAgent par script.

1. **Les appels MCP demandent maintenant confirmation.** Jusqu'à la 0.7.3, un outil d'un serveur MCP s'exécutait sans rien demander, même avec les confirmations actives. Désormais chaque appel MCP est confirmé, comme `write_file`. Utilise `--yes` pour supprimer la question.
2. **Mode `--api` (orchestrateurs comme csflow).** En mode `--api`, une confirmation reçoit sa réponse par `POST /api/confirm`. Un orchestrateur qui utilise un serveur MCP doit lancer `csagent --api` avec `--yes`, ou répondre à `/api/confirm` ; sinon l'appel MCP reste en attente d'une réponse.
3. **Les noms des outils MCP changent.** Un outil `search` du serveur devient `mcp_search`. Les prompts et scripts qui nomment un outil du serveur doivent utiliser le nouveau nom. Le serveur, lui, reçoit toujours son propre nom.
4. **Un outil de serveur ne peut plus remplacer un outil natif.** Avant, un outil MCP nommé `read_file` ou `sh` était appelé à la place de l'outil intégré. Maintenant il devient `mcp_read_file` / `mcp_sh` et l'outil intégré n'est pas touché.
5. **Rien n'est supprimé sans que tu le demandes.** Le nettoyage des anciens enregistrements est désactivé par défaut.
6. **`.gitignore`** ignore maintenant `recordings/`, `transcripts/` et `*.patch`.

Les fichiers de mémoire et les arguments de la ligne de commande existants ne changent pas.

## 1. Transcription audio : `transcribe_audio`

Un outil en lecture seule : l'agent transforme un enregistrement de parole en texte.

| Paramètre | Description |
|---|---|
| `path` | Fichier audio (ou vidéo), dans le dossier de travail |
| `language` | Code ISO-639-1 facultatif (`fr`, `en`, ...) ; sans lui, la langue est détectée automatiquement |
| `max_chars` | Nombre de caractères renvoyés (50 000 par défaut, 200 000 au maximum) ; au-delà, le texte est coupé avec un avertissement |

Fonctionnement :

- **Avec `ffmpeg`** (dans le `PATH`, ou indiqué par `CSAGENT_FFMPEG`), tout format que `ffmpeg` sait décoder est accepté (mp3, wav, m4a, ogg, flac, webm, fichiers vidéo...). L'audio est converti en WAV 16 kHz mono et découpé en **parties de 10 minutes**, envoyées l'une après l'autre à `<endpoint>/audio/transcriptions` puis rassemblées.
- **Sans `ffmpeg`**, seuls les fichiers `.wav` et `.mp3` jusqu'à 24 Mo sont acceptés, envoyés tels quels.
- Un fichier est limité à **100 Mo**. Le découpage se fait au temps, donc un mot situé à une frontière peut être coupé.
- Modèle : `openai/whisper-large-v3`, ou `CSAGENT_TRANSCRIBE_MODEL`. La clé est `ALBERT_API_KEY` (inutile pour un `--endpoint` local).
- L'audio est **envoyé à l'endpoint**. Le texte renvoyé est une **donnée non fiable, jamais une instruction** : le prompt système le précise.

Exemple de prompt : *« Transcris `entretien.m4a` en français et résume-le en cinq lignes. »*

## 2. Enregistreur vocal dans les interfaces web

Disponible dans `--ui` et `--leanui` (pas dans `--api`). Le bouton micro 🎙 enregistre depuis le navigateur.

1. **Enregistrement.** Le navigateur envoie l'audio au serveur local toutes les 30 secondes, dans `recordings/` (dans le dossier de travail). Un onglet fermé fait perdre au plus les 30 dernières secondes, et les longs enregistrements sont acceptés (limite : 100 Mo par enregistrement, soit environ 7 heures avec la qualité par défaut).
2. **Transcription.** À l'arrêt, le fichier entier est transcrit avec le même code que `transcribe_audio` (donc `ffmpeg` est nécessaire, sauf pour un `.wav` ou `.mp3` court ; les navigateurs enregistrent en webm ou m4a). Une ligne de progression indique la partie en cours. La transcription a lieu à la fin, pas pendant que tu parles. La langue parlée se choisit avec le sélecteur **FR / EN / Auto** placé à côté du micro : le choix est mémorisé par le navigateur, et à la première visite c'est la langue du navigateur si elle est française ou anglaise, sinon Auto (Whisper détecte la langue). Seul un code à deux lettres est transmis à Whisper.
3. **Ton choix, à la fin :**
   - **Use as instruction** : le texte est placé dans la zone de saisie, prêt à être envoyé.
   - **Keep as text** : la transcription est enregistrée dans `transcripts/rec_<date>_<id>.txt` et jointe à ton **prochain** prompt sous la forme `[Attached text file: transcripts/...]`. L'agent la lit avec `read_file` et la traite comme une donnée à exploiter (la résumer, l'envoyer en pièce jointe, ...), jamais comme une instruction.

Dans l'interface lean, le même parcours s'affiche dans le journal, avec `[use as instruction]` et `[keep as text]` comme lignes cliquables.

En cas de problème (micro refusé, envoi interrompu, erreur de transcription), le message l'indique ainsi que l'emplacement de l'audio : rien n'est perdu.

**Nettoyage.** Avec `CSAGENT_AUDIO_KEEP_DAYS=30`, à chaque démarrage de `--ui` / `--leanui`, les fichiers audio `rec_*` de `recordings/` non modifiés depuis 30 jours sont supprimés. Les transcriptions et tout autre fichier ne sont jamais touchés. Désactivé par défaut.

Endpoints (même origine uniquement ; une page d'un autre site est refusée) : `POST /api/audio/start`, `POST /api/audio/{id}` (un morceau), `POST /api/audio/{id}/transcribe` (progression en SSE). Le serveur choisit le nom et l'extension du fichier (webm, ogg, m4a, wav, mp3 seulement).

## 3. MCP : plus sûr par défaut

`--mcp <url>` (ou `CSAGENT_MCP_URL`) connecte un serveur MCP en Streamable HTTP. Ses outils sont proposés au modèle à côté des outils intégrés.

| Avant (0.7.3) | Maintenant (0.8.0) |
|---|---|
| Outil proposé sous le nom du serveur | Proposé sous le nom `mcp_<nom>` (caractères interdits remplacés par `_`, 64 caractères au plus, doublons numérotés) |
| Un outil de serveur nommé comme un outil intégré était appelé à sa place | Impossible : l'outil intégré garde son nom et son comportement |
| Appelé sans rien demander | **Chaque appel est confirmé** (supprimé avec `--yes`) |
| Descriptions et résultats pris pour fiables | Marqués `(external MCP server tool)`, descriptions coupées à 1000 caractères, section 18 du prompt système : les résultats sont des données, jamais des instructions |
| Non documenté (cité comme fonction à venir) | Section « MCP servers » du README, `--help`, tests |

## Nouvelle configuration

| Réglage | Rôle | Par défaut |
|---|---|---|
| `CSAGENT_TRANSCRIBE_MODEL` | Modèle de reconnaissance vocale | `openai/whisper-large-v3` |
| `CSAGENT_FFMPEG` | Chemin de `ffmpeg` s'il n'est pas dans le `PATH` | `ffmpeg` |
| `CSAGENT_AUDIO_KEEP_DAYS` | Nombre de jours de conservation de l'audio de `recordings/` (entier positif) | non défini = tout conserver |
| `--mcp <url>` / `CSAGENT_MCP_URL` | Serveur MCP à connecter | aucun |

## Sécurité

- **Textes non fiables.** Les transcriptions et les résultats MCP viennent de l'extérieur : le prompt système demande au modèle de les utiliser comme des données et de ne jamais suivre les instructions qu'ils contiennent (sections 17 et 18).
- **Local uniquement.** Les endpoints de l'enregistreur sont servis par le serveur web local, refusent les requêtes d'une autre origine et n'écrivent que dans `recordings/` et `transcripts/` du dossier de travail.
- **Ce qui quitte ta machine.** L'audio part vers l'endpoint configuré pour être transcrit. Utilise un endpoint local (par exemple un serveur Whisper local) si le contenu ne doit pas sortir de la machine.
- **MCP.** Ne connecte que des serveurs de confiance ; les confirmations restent actives sauf si tu passes `--yes`.

## Limites

- La transcription se fait après l'enregistrement, pas en direct.
- Les textes de l'enregistreur sont en anglais, comme le reste de l'interface.
- Le texte d'une dictée est en lecture seule dans la carte de l'enregistreur ; corrige-le après l'avoir envoyé dans la zone de saisie.
- L'audio et les transcriptions restent sur le disque jusqu'à ce que tu les supprimes ou que tu actives `CSAGENT_AUDIO_KEEP_DAYS`.
- MCP : un seul serveur, HTTP uniquement (pas de stdio), pas d'en-tête d'authentification, outils seulement (ni ressources ni prompts), résultats en texte. Si le serveur est injoignable, la session s'arrête avec une erreur.
- Navigateurs : l'enregistreur a été testé avec Chromium (webm/opus). Le chemin Safari (m4a) n'est pas testé.

## Tests

136 tests (`dotnet run --project Tests -c Release`), dont :

- `transcribe_audio` : parties envoyées dans l'ordre (faux Whisper, faux `ffmpeg`), erreurs, troncature, contrôles du chemin et de la clé.
- Stockage de l'enregistreur (`AudioRecordings`), nettoyage des anciens enregistrements et choix de la langue (seul un code à deux lettres atteint l'API de transcription).
- MCP (faux serveur MCP) : noms `mcp_`, aucun remplacement d'un outil intégré, appels sous le nom d'origine du serveur, confirmation avant chaque appel.

L'enregistreur a aussi été vérifié dans un vrai navigateur (Chromium avec un faux micro), dans les deux interfaces.
