# CsAgent 0.9.2 — notes de version

La version 0.9.2 ajoute un menu `/skills` à l'interface `--leanui` et corrige la complétion des commandes dans le menu `/help`. Elle fait suite à la 0.9.1.

- [Résumé](#résumé)
- [Notes de mise à jour](#notes-de-mise-à-jour)
- [1. LeanUI : un menu `/skills`](#1-leanui--un-menu-skills)
- [2. Correction : compléter une commande avec un préfixe](#2-correction--compléter-une-commande-avec-un-préfixe)
- [Nouvelle route](#nouvelle-route)
- [Limites](#limites)
- [Tests](#tests)

## Résumé

| Domaine | Ce qui change |
|---|---|
| `--leanui` | `/skills` ouvre un menu des skills de `skills/INDEX.md` ; choisir un skill écrit une instruction dans le champ de saisie |
| Menu de commandes | `/skills` est ajouté à `/help` et aux suggestions ; un préfixe comme `/ne` suivi d'Entrée complète en `/new ` au lieu d'être envoyé comme commande inconnue |
| Serveur web | Nouvelle route `GET /api/skills`, en lecture seule |
| Tests | 197 tests (5 de plus) |

Aucune option, variable d'environnement ou fichier de configuration n'est ajouté ni modifié.

## Notes de mise à jour

1. **Une nouvelle route existe dans les trois modes web** (`--ui`, `--leanui`, `--api`) : `GET /api/skills`. Elle ne fait que lire `skills/INDEX.md` ; voir [Nouvelle route](#nouvelle-route).
2. **Une commande tapée en entier est toujours envoyée telle quelle** (`/new`, `/clear`, `/help`, `/skills`). Seul un préfixe est complété par le menu.

Les fichiers de mémoire, les arguments de la ligne de commande, le dossier `LLMRoutingRules` et le dossier `skills/` ne changent pas.

## 1. LeanUI : un menu `/skills`

Les skills de CsAgent sont des fichiers Markdown du dossier `skills/`, listés dans `skills/INDEX.md`. L'agent lit cet index et charge lui-même le skill qui correspond à ta demande. Le menu `/skills` te permet de voir ce qui existe et d'en désigner un toi-même.

| Geste | Effet |
|---|---|
| `/skills` puis Entrée | Ouvre le menu : une ligne par skill avec son nom, sa version et le début de sa description |
| `/skills fail` | Filtre la liste sur le nom ou la description |
| ↑ / ↓ | Déplace la sélection (retour au début en fin de liste) |
| Entrée ou Tab | Choisit le skill |
| Échap | Ferme le menu |
| Souris | Le survol sélectionne, le clic choisit, un clic ailleurs ferme le menu ; l'infobulle d'une ligne montre la description complète |

- **Choisir un skill n'envoie rien.** Le champ de saisie reçoit une instruction, par exemple `Use the skill "Failure Recovery" (skills/failure-recovery.md): `. Tu la complètes avec ta demande, puis Entrée.
- Le menu ne montre que les skills de l'index, comme l'agent : un fichier de `skills/` qui n'y figure pas n'est pas proposé.
- Sans `skills/INDEX.md`, ou si rien ne correspond au filtre, le menu affiche une seule ligne non sélectionnable (« no skills » ou « no match »).
- `/skills` apparaît aussi dans la liste de `/help` et dans les suggestions quand on tape `/s`.
- Le dossier lu est le **dossier de travail** du processus (celui où tu as lancé `csagent`). La liste est relue à chaque `/skills` tapé en entier.

Ajouter un skill ne change pas : un fichier dans `skills/` et une ligne dans `skills/INDEX.md`. Le menu le montre au prochain `/skills`, sans recompiler.

## 2. Correction : compléter une commande avec un préfixe

Dans la 0.9.1, `/ne` suivi d'Entrée était envoyé comme une commande et donnait « commande inconnue », au lieu de laisser le menu proposer `/new`. Maintenant :

- un **préfixe** (`/ne`, `/s`) est complété par le menu : Entrée choisit la ligne sélectionnée ;
- une commande **tapée en entier** est envoyée telle quelle. Pour `/new`, cela crée une session sans nom, comme avant.

## Nouvelle route

`GET /api/skills` renvoie le contenu de `skills/INDEX.md` du dossier de travail, en JSON :

```json
[{ "name": "Failure Recovery", "version": "1.0", "description": "A tool call or command has failed twice…", "file": "skills/failure-recovery.md" }]
```

- La liste est vide si l'index n'existe pas ou ne peut pas être lu ; la route ne renvoie jamais le contenu des fichiers de skills eux-mêmes.
- Avec `--api` et une clé (`--api-key`), la route demande la même clé que les autres.

## Limites

- Le menu n'existe que dans `--leanui`, pas dans `--ui` (la route, elle, est disponible dans les deux).
- Il a été testé avec Chromium. Firefox et Safari ne sont pas testés.
- Le menu liste les skills mais ne les exécute pas : c'est l'agent qui décide de lire le fichier du skill, d'après l'instruction que tu envoies.
- La description d'un skill est tronquée à la largeur de la fenêtre (le texte complet est dans l'infobulle).

## Tests

197 tests (`dotnet run --project Tests -c Release`), dont les 5 nouveaux :

- le tableau de l'index est lu : l'en-tête et la ligne de séparation sont ignorés, quatre champs par ligne ;
- un `|` à l'intérieur d'une description reste dans la description ;
- un texte sans tableau, ou vide, ne donne aucun skill ;
- `Load` lit `<dossier>/skills/INDEX.md`, et un dossier ou un fichier absent donne une liste vide ;
- le JSON est un tableau d'objets `name`, `version`, `description`, `file`.

Le menu a été vérifié à la main dans Chromium contre le vrai serveur (build de l'application) : liste des 10 skills, filtre, flèches, souris, Échap, focus conservé, complétion d'un préfixe.
