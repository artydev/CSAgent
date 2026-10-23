# Client Python pour l'API de CSAgent

`csagent_client.py` pilote un serveur CSAgent (`csagent --api`, ou le mode
Web UI qui expose le même protocole) depuis des scripts Python. **Uniquement
de la bibliothèque standard** : `http.client`, `json`, `urllib` — aucune
dépendance à installer, dans l'esprit du projet (zéro NuGet côté C#).

## Démarrage

```bash
csagent --api --yes            # le serveur SSE (port 5050 par défaut)
```

```python
import sys
sys.path.insert(0, "/chemin/vers/CSAgent/python")
import csagent_client as cc

client = cc.CsAgentClient("http://localhost:5050", api_key="...")
reponse = client.ask("Liste les fichiers du dossier courant")
print(reponse)
```

`api_key` est requis quand le serveur démarre avec `--api-key` (ou la
variable `CSAGENT_API_KEY`) : il est envoyé dans l'en-tête
`Authorization: Bearer <clé>`.

## Ce que fait le client

- **`client.ask(prompt, ...)`** — envoie une demande sur `/api/chat`, suit le
  flux SSE jusqu'à l'événement `done`, et renvoie la réponse finale.
  Événements reçus : `step`, `thought`, `model`, `call`, `result`,
  `warning`, `danger`, `confirm`, `done`, `error`.
- **`client.stream(prompt, ...)`** — accès brut : un générateur de couples
  `(type, données)` ; les confirmations ne sont pas répondues ici (voir ci-dessous).
- **`client.confirm(True ou False)`** — répond à l'attente en cours sur
  `POST /api/confirm` (renvoie `False` si rien n'était en attente).
- **`client.skills()`** — lit la bibliothèque de skills (`GET /api/skills`).

## Confirmations

Le mode `--api` **sans** `--yes` demande une confirmation pour chaque appel
d'outil destructif : le serveur émet un événement `confirm` (avec le nom de
l'outil) et attend `POST /api/confirm` (`true` / `false`).

```python
# Approver tout
reponse = client.ask("Committe les modifications", confirm=True)

# Refuser tout
reponse = client.ask("Nettoie le dossier", confirm=False)

# Décider au cas par cas (le callback reçoit le nom de l'outil)
reponse = client.ask(
    "Committe les modifications",
    on_confirm=lambda outil: input(f"Autoriser {outil} ? [o/N] ") in ("o", "O"),
)

# Ne rien décider soi-même : une PendingConfirmation est levée
try:
    client.ask("Pousse la branche", confirm=None)
except cc.PendingConfirmation as attente:
    print("en attente :", attente.tool)
    client.confirm(True)      # y répondre plus tard
```

`on_confirm` a priorité sur `confirm`. Avec `stream()`, c'est le code qui
lit l'événement `('confirm', {'tool': ...})` et appelle `client.confirm()`.

## Étapes, journal et images

```python
# Journaliser chaque événement (étapes, pensées, appels, résultats)
client.ask(
    "Analyse ce dépôt",
    on_event=lambda t, d: print(t, d),
    task="analyse-depot",     # slug optionnel pour le suivi de tâches
)
```

`image` joint un fichier image (jpg, png, gif, webp) au tour, envoyé en
multipart (limite côté serveur : 10 Mo) :

```python
reponse = client.ask("Que voit-on sur cette capture ?", image="erreurs.png")
```

## Erreurs

- `cc.CsAgentError` — le serveur a émis un événement `error`, a répondu par
  un code HTTP inattendu, ou a fermé le flux avant `done`.
- `cc.PendingConfirmation` — un outil attend une confirmation et aucune
  politique n'était fournie (`confirm=None` et pas de `on_confirm`).

Une conversation par serveur : comme en mode `--api`, toutes les demandes
partagent la même conversation et le même répertoire mémoire — une requête à
la fois, ou une instance par tâche.

## Tests

La suite de tests utilise uniquement le standard (`unittest`, `http.server`)
: un serveur factice sur `127.0.0.1` parle le protocole SSE réel, et le
client est exercé comme contre un `csagent --api` réel.

```bash
python3 test_csagent_client.py
```
