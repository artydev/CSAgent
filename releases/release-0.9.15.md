# CsAgent 0.9.15 — notes de version

La version 0.9.15 expose la transcription audio (parole vers texte) dans le mode `--api`, jusque-là réservée à la Web UI. Un programme, par exemple CSFlow, peut envoyer un enregistrement à CsAgent et recevoir le texte. Elle fait suite à la 0.9.14.

- [Résumé](#résumé)
- [Notes de mise à jour](#notes-de-mise-à-jour)
- [Les points d'accès](#les-points-daccès)
- [Exemple](#exemple)
- [Limites](#limites)
- [Tests](#tests)

## Résumé

| Domaine | Ce qui change |
|---|---|
| `--api` | Trois points d'accès audio : `POST /api/audio/start`, `POST /api/audio/{id}` et `POST /api/audio/{id}/transcribe` (flux SSE) |
| Code | `ApiHost.cs` appelle `MapAudioEndpoints()` ; la bannière de démarrage annonce `POST /api/audio/… (speech to text)` |
| Documentation | README, guide et page d'accueil décrivent les trois points d'accès |
| Tests | 310 tests, inchangés : aucun test n'a été ajouté pour cette version |

Aucun argument ni variable d'environnement n'est ajouté.

## Notes de mise à jour

1. **Rien à faire** : les points d'accès apparaissent avec `--api`. Les autres routes ne changent pas.
2. **CSFlow** : le nœud `user_input` avec micro (saisie vocale) a besoin de cette version. Avec une version plus ancienne, CSFlow signale que CsAgent est trop ancien (réponse 404 sur `/api/audio/start`).
3. La clé d'API (`--api-key`) protège ces routes comme les autres. Sans clé, elles sont ouvertes sur la machine locale, comme le reste de `--api`.
4. Les fichiers sont écrits dans `recordings/` (audio) et `transcripts/` (texte), dans le dossier de travail de CsAgent.

## Les points d'accès

| Appel | Rôle |
|---|---|
| `POST /api/audio/start?type=<mime>` | Ouvre un enregistrement. Répond `{"id","path"}` |
| `POST /api/audio/{id}` | Ajoute des octets d'audio, dans l'ordre (corps brut) |
| `POST /api/audio/{id}/transcribe?lang=fr` | Lance la transcription. Flux SSE : `progress`, puis `done` (`text`, `path`) ou `error` |

`lang` prend un code à deux lettres ; sans lui, la langue est détectée. Ce sont les mêmes routes que celles de la dictée de la Web UI.

## Exemple

```
curl -X POST "http://localhost:8080/api/audio/start?type=audio/wav" -H "Authorization: Bearer demo-secret"
curl -X POST "http://localhost:8080/api/audio/<id>" -H "Authorization: Bearer demo-secret" --data-binary @entretien.wav
curl -N -X POST "http://localhost:8080/api/audio/<id>/transcribe?lang=fr" -H "Authorization: Bearer demo-secret"
```

## Limites

- **Essayé en partie seulement** : le démarrage d'un enregistrement (`/api/audio/start`) a été vérifié sur un binaire compilé sous Windows. L'envoi des octets et la transcription par le point d'accès configuré (Albert) restent à vérifier de bout en bout.
- `ffmpeg` est nécessaire, sauf pour un fichier `.wav` ou `.mp3` de 24 Mo au plus. Un enregistrement est limité à 100 Mo.
- Le modèle de transcription est celui du point d'accès configuré : OpenRouter n'en propose pas.
- L'audio part vers ce point d'accès : choisissez un point d'accès local s'il ne doit pas quitter la machine.

## Tests

310 tests (`dotnet run --project Tests -c Release`), sans nouveau test. Les points d'accès audio existaient déjà pour la Web UI ; cette version ne change que leur exposition en `--api`.
