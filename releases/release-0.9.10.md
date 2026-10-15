# CsAgent 0.9.10 — notes de version

La version 0.9.10 ajoute l'agenda Outlook : l'agent lit les rendez-vous d'une période et en crée, sans inscription Azure ni jeton. Elle fait suite à la 0.9.9.

- [Résumé](#résumé)
- [Notes de mise à jour](#notes-de-mise-à-jour)
- [Les deux outils](#les-deux-outils)
- [Limites](#limites)
- [Tests](#tests)

## Résumé

| Domaine | Ce qui change |
|---|---|
| Nouveaux outils | `read_calendar` (lecture seule) et `create_appointment` (demande confirmation) : 37 outils au total |
| Groupe d'outils | `calendar`, allumé par agenda, calendrier, rendez-vous, réunion, créneau, meeting… (voir 0.9.8) |
| Prompt système | Nouvelle section 19 (agenda) |
| Tests | 274 tests (10 de plus) |

Aucun argument, variable d'environnement ni fichier de configuration n'est ajouté.

## Notes de mise à jour

1. **Windows seulement**, avec Outlook classique installé et configuré, comme `send_email`. Sous Linux et macOS, les deux outils répondent par une erreur claire.
2. **Rien n'est créé sans relecture.** `create_appointment` demande confirmation, puis **affiche** le rendez-vous dans Outlook sans l'enregistrer. `save: true` l'enregistre ; `send: true` l'enregistre et envoie les invitations (irréversible, exige des participants).
3. **Aucune invitation par défaut.** Donner des `attendees` fait du rendez-vous une réunion, mais rien ne part sans `send: true`.
4. **L'agent connaît la date du jour par `read_calendar`** : la première ligne du résultat la donne avec le jour de la semaine. Le prompt système ne la contient pas (il est construit une fois par session et serait périmé le lendemain).
5. Le test `--no-compact` supposait une taille exacte du prompt système : il accepte maintenant 6 à 8 résultats conservés.

## Les deux outils

`read_calendar` — `start` (aujourd'hui par défaut), `end` (7 jours après `start`, 366 au plus ; une date seule inclut tout ce jour), `query` (objet ou lieu), `max_items` (50, 200 au plus). Les rendez-vous récurrents sont développés. Une ligne par rendez-vous :

```
2026-10-16 14:00-15:00 | Point d'équipe | Salle 3 | organizer: Marie Durand [meeting]
```

`create_appointment` — `subject` et `start` obligatoires ; `end` ou `duration_minutes` (60 par défaut) ; `all_day` ; `location`, `body`, `attendees`, `reminder_minutes` (0 : pas de rappel) ; `save`, `send`. Les chevauchements avec l'agenda sont signalés (« Warning: overlaps with … »), sans bloquer.

Les dates sont des heures locales ISO-8601 sans fuseau (`2026-10-16T14:00`). Elles sont renvoyées à PowerShell dans un format fixe : la langue de la machine ne change pas leur sens. Le texte (objet, lieu, notes) est transmis entre apostrophes, donc `$(...)` n'est jamais interprété. Le contenu de l'agenda est marqué comme donnée non fiable.

## Limites

- **Non essayé avec un vrai Outlook** : l'environnement de développement est sous Linux. Les tests couvrent les arguments, les dates et le PowerShell généré, pas l'appel COM lui-même. À vérifier sur Windows avant de s'y fier : le filtre de période (`Restrict` avec le format de date de la machine), les réunions avec participants, les journées entières.
- Seul l'agenda par défaut est lu : ni calendriers partagés, ni boîtes de collègues, ni autres agendas du profil.
- Pas de modification ni de suppression de rendez-vous, pas de réponse à une invitation.
- Pas de gestion des fuseaux horaires : les heures sont celles de la machine.
- Le résultat de `read_calendar` se limite à 200 rendez-vous.

## Tests

274 tests (`dotnet run --project Tests -c Release`), dont les 10 nouveaux (`CalendarTests.cs`) :

- les deux outils existent ; `create_appointment` demande confirmation, pas `read_calendar` ;
- groupe `calendar` : mots déclencheurs en français et en anglais, pas de faux positif, reste actif une fois utilisé ;
- hors Windows : erreur claire ;
- dates ISO lues, fuseaux et textes libres refusés ;
- période invalide, rendez-vous incohérent (fin avant début, durée, rappel, `send` sans participants, journée entière) refusés avant d'appeler Outlook ;
- PowerShell de lecture : dates à format fixe, requête entre apostrophes, limites, marqueurs de donnée non fiable, aucune écriture ;
- PowerShell de création : affichage par défaut, `save`, `send`, réunion sans invitation, journée entière, objet qui contient `$(calc)` ou une apostrophe, chevauchements non bloquants ;
- section 19 du prompt système.
