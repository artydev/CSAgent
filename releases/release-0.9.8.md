# CsAgent 0.9.8 — notes de version

La version 0.9.8 envoie au modèle la description des seuls outils utiles à la demande, au lieu des 35 à chaque étape. Cela réduit les jetons d'entrée, donc la consommation du quota. Elle fait suite à la 0.9.7.

- [Résumé](#résumé)
- [Notes de mise à jour](#notes-de-mise-à-jour)
- [Les outils envoyés à chaque étape](#les-outils-envoyés-à-chaque-étape)
- [Limites](#limites)
- [Tests](#tests)

## Résumé

| Domaine | Ce qui change |
|---|---|
| Outils décrits au modèle | `core` (fichiers, recherche, shell) toujours ; git, web, courrier, audio, Excel, presse-papiers, archives, terminal, modèles seulement quand la demande en parle. Une demande de code ordinaire envoie environ 6 800 caractères d'outils au lieu de 20 400 (67 % de moins) |
| Nouvel outil | `enable_tools` : l'agent rallume un groupe qui manque |
| Réglages | Nouvel argument `--all-tools` : rétablit l'envoi de tous les outils |
| Tests | 247 tests (9 de plus) |

Aucune variable d'environnement ni fichier de configuration n'est ajouté.

## Notes de mise à jour

1. **Le comportement par défaut change.** Une demande qui ne parle ni de git, ni du web, ni d'e-mails, d'audio, d'Excel, du presse-papiers, d'archives, de terminal ou de modèle ne décrit plus ces outils au modèle. `--all-tools` rétablit l'ancien envoi.
2. **Rien n'est interdit.** Un outil qui n'est pas décrit reste exécutable s'il est appelé ; le groupe de l'outil appelé est alors rallumé pour les étapes suivantes.
3. **`enable_tools`** apparaît dans la liste tant qu'un groupe est éteint. Sa description nomme les groupes éteints et leurs outils.
4. Le prompt système, lui, cite toujours tous les outils par leur nom : ce texte n'a pas changé.

## Les outils envoyés à chaque étape

À chaque étape, CsAgent renvoie au modèle la conversation, mais aussi la description de tous les outils (environ 20 000 caractères). Une demande de correction de code n'a pas besoin de lire des `.msg` ni de piloter Excel.

| Groupe | Outils | Envoyé |
|---|---|---|
| `core` | `read_file`, `write_file`, `edit_file`, `copy_file`, `move_file`, `delete_file`, `read_json`, `parse_output`, `list_dir`, `tree`, `search_files`, `sh` | toujours |
| `git` | `git_status`, `git_diff`, `git_log`, `git_branch`, `git_commit` | git, commit, branche, diff… |
| `web` | `web_search`, `fetch_url`, `http_request` | web, URL, recherche, actualités… |
| `mail` | `read_msg`, `save_attachment`, `group_msg_by_sender`, `send_email` | e-mail, Outlook, `.msg`, expéditeur… |
| `audio` | `transcribe_audio` | audio, enregistrement, transcription… |
| `office` | `excel_command`, `close_excel` | Excel, tableur, `.xlsx`… |
| `clipboard` | `read_clipboard`, `write_clipboard` | presse-papiers, « ce que j'ai copié »… |
| `archive` | `zip`, `unzip` | archive, zip… |
| `terminal` | `run_terminal`, `close_terminal` | terminal, serveur de dev… |
| `models` | `switch_model`, `list_models` | modèle, LLM… |

- Les mots sont cherchés, en français et en anglais, dans le dernier message de l'utilisateur.
- Un groupe dont un outil a déjà servi dans la conversation reste actif, même pour une relance courte (« et le deuxième ? »).
- Un outil d'un groupe éteint, appelé quand même, rallume son groupe pour la suite de la demande.
- **Serveur MCP :** ses outils sont tous envoyés tant que leurs descriptions tiennent en 4 000 caractères. Au-delà, ils ne le sont que si la demande dit « MCP » ou contient un mot du nom de l'un d'eux, ou après `enable_tools` avec le groupe `mcp`.
- Un outil absent de la liste des groupes (par exemple un futur outil) est classé dans `core`, donc toujours envoyé.

## Limites

- La mesure (6 781 caractères au lieu de 20 420 pour une demande de code) est en caractères, pas en jetons, et n'a pas été faite sur le vrai serveur Albert.
- Le choix repose sur des mots-clés : une demande qui n'en contient aucun mais a besoin d'un groupe coûte une étape de plus (`enable_tools`). Un mot courant peut aussi allumer un groupe inutilement (« modèle de données » allume `models`), ce qui coûte seulement des jetons.
- Le prompt système (sections sur le courrier, l'audio, Excel…) n'est pas allégé : c'est un autre chantier.
- La publication native (AOT) de cette version n'a pas été essayée dans l'environnement de développement.

## Tests

247 tests (`dotnet run --project Tests -c Release`), dont les 9 nouveaux :

- chaque outil est dans `core` ou dans un seul groupe, et `--all-tools` est lu sans être pris pour un nom de mémoire ;
- une demande de code ordinaire n'envoie que `core` (67 % de caractères en moins) ;
- les mots de la demande allument les bons groupes, en français et en anglais, sans faux positif sur une demande neutre ;
- un groupe déjà utilisé reste actif pour une relance courte ;
- `enable_tools` : listé tant qu'un groupe est éteint, allume un groupe, refuse un groupe inconnu, absent quand tout est allumé ;
- outils MCP : envoyés s'ils sont peu nombreux, sinon seulement si la demande les nomme ou dit « MCP » ;
- requêtes réelles : la première ne contient que `core`, la suivante contient le groupe allumé par `enable_tools` ;
- un outil appelé alors que son groupe était éteint garde le groupe allumé ;
- avec `--all-tools`, les 35 outils sont envoyés et `enable_tools` n'apparaît pas.
