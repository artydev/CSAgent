# CsAgent 0.9.6 — notes de version

La version 0.9.6 rend `csagent --prompt "…"` utilisable dans un script : seule la réponse finale est écrite sur la sortie standard, de sorte qu'elle peut être redirigée vers une autre commande. Elle fait suite à la 0.9.5.

- [Résumé](#résumé)
- [Notes de mise à jour](#notes-de-mise-à-jour)
- [Le mode script de `--prompt`](#le-mode-script-de---prompt)
- [Limites](#limites)
- [Tests](#tests)

## Résumé

| Domaine | Ce qui change |
|---|---|
| `--prompt` | Seule la réponse finale est écrite sur la sortie standard : plus de bannière, de ligne de version, d'étapes, d'appels d'outils, de résultats, de modèle ni de jetons |
| Erreurs | Avertissements et erreurs vont sur la sortie d'erreur ; code de sortie 1 si l'agent signale une erreur |
| Confirmations | Si l'entrée est redirigée, un outil qui demande confirmation est refusé (utilisez `--yes`) |
| Réglages | Nouvel argument `--verbose` : avec `--prompt`, rétablit l'affichage complet |
| Documentation | README, `--help`, `docs/index.html`, `docs/guide-csagent.html` et `docs/slides.html` mis à jour |
| Tests | 231 tests (7 de plus) |

Aucune variable d'environnement ou fichier de configuration n'est ajouté.

## Notes de mise à jour

1. **La sortie de `--prompt` change.** Un script qui lisait la bannière, les étapes ou la ligne du modèle ne les trouvera plus. `--verbose` rétablit l'ancien affichage.
2. **`--quiet` n'a plus d'effet par défaut avec `--prompt`**, puisque le mode est déjà réduit à la réponse ; il agit de nouveau avec `--verbose`.
3. **Une confirmation est refusée si l'entrée est redirigée** (`… | csagent --prompt …`, `< fichier`). Sans clavier, personne ne peut répondre : l'action est refusée et un message sur la sortie d'erreur indique `--yes`.

Les modes interactif, `--ui`, `--leanui` et `--api` ne changent pas. Les fichiers de mémoire, `LLMRoutingRules` et `skills/` non plus.

## Le mode script de `--prompt`

```
csagent --prompt "liste les fichiers ici" --yes | clip
```

- **Sortie standard** : la réponse finale de l'assistant, en texte brut. Les messages intermédiaires (« Je regarde… ») ne sont pas écrits.
- **Sortie d'erreur** : les erreurs, les avertissements, les alertes de danger, les appels d'outils en échec (une ligne), les notes de routage et la clé API manquante. Les avis de routine « Session summary updated / unchanged / loaded » ne sont plus affichés ; un échec de la mise à jour du résumé reste signalé.
- **Code de sortie** : 1 si l'agent signale une erreur (par exemple le nombre maximal d'étapes atteint) ou si la demande échoue, sinon 0. Sans texte, `--prompt` affiche l'usage (code 2), comme avant.
- **Avec un clavier**, la question de confirmation est posée sur la sortie d'erreur.
- La conversation est enregistrée dans le dossier de mémoire comme d'habitude.

## Limites

- Seule la réponse finale est écrite : si l'agent produit plusieurs messages de réponse au fil des étapes, les premiers ne sont pas conservés.
- Le mode script n'a pas été essayé avec le vrai serveur Albert, seulement avec un faux serveur.
- La publication native (AOT) de cette version n'a pas été essayée dans l'environnement de développement.

## Tests

231 tests (`dotnet run --project Tests -c Release`), dont les 7 nouveaux :

- `--verbose` est lu, désactivé par défaut, et n'est pas pris pour un nom de mémoire ;
- la sortie standard ne contient que la réponse finale ;
- un appel d'outil en échec va sur la sortie d'erreur, jamais sur la sortie standard ;
- une erreur va sur la sortie d'erreur et demande le code de sortie 1 ;
- les échecs de mémoire sont signalés, les avis de routine non ;
- la confirmation est refusée si l'entrée est redirigée ;
- la confirmation est posée sur la sortie d'erreur quand un clavier est là.
