# CsAgent 0.9.4 — notes de version

La version 0.9.4 ajoute une bascule automatique vers le modèle de code quand le quota de jetons du jour d'un modèle est épuisé. Elle fait suite à la 0.9.3, qui arrêtait l'agent dans ce cas.

- [Résumé](#résumé)
- [Notes de mise à jour](#notes-de-mise-à-jour)
- [1. Bascule quand le quota du jour est épuisé](#1-bascule-quand-le-quota-du-jour-est-épuisé)
- [2. Le modèle épuisé est mémorisé](#2-le-modèle-épuisé-est-mémorisé)
- [3. Quand la bascule n'a pas lieu](#3-quand-la-bascule-na-pas-lieu)
- [Variable d'environnement](#variable-denvironnement)
- [Limites](#limites)
- [Tests](#tests)

## Résumé

| Domaine | Ce qui change |
|---|---|
| Quota du jour épuisé | Au lieu de s'arrêter, CsAgent continue la **même requête** avec un autre modèle (par défaut `deepseek-v4-flash`) et prévient par un avertissement |
| Mémoire de la bascule | Un modèle dont le quota du jour est épuisé est mis de côté pendant 1 heure : les tours suivants partent directement sur l'autre modèle |
| Réglage | Nouvelle variable `CSAGENT_FALLBACK_MODEL` : choisir le modèle de remplacement, ou la mettre à `off` pour garder l'arrêt de la 0.9.3 |
| Tests | 217 tests (10 de plus) |

Aucune option de ligne de commande n'est ajoutée. Les limites **par minute** gardent leur comportement (nouveaux essais, jamais de bascule).

## Notes de mise à jour

1. **Le modèle qui répond peut changer en cours de session.** Après un 429 « par jour », la réponse vient du modèle de remplacement, sans que tu aies à relancer. Un avertissement indique le changement.
2. **Le comportement de la 0.9.3 reste disponible** : `CSAGENT_FALLBACK_MODEL=off` (ou `none`) rétablit l'arrêt immédiat avec l'explication du quota.
3. **Le modèle de remplacement consomme son propre quota.** Si lui aussi est épuisé, l'agent s'arrête avec le message de quota du jour, comme avant.

Les fichiers de mémoire, les arguments de la ligne de commande, `LLMRoutingRules` et `skills/` ne changent pas.

## 1. Bascule quand le quota du jour est épuisé

Quand l'API répond par un 429 « par jour » (voir 0.9.3), CsAgent :

1. note que le modèle courant est épuisé ;
2. cherche un modèle de remplacement ;
3. s'il en trouve un, affiche un avertissement et renvoie **la même étape** au nouveau modèle, sans perdre le travail du tour :

```
Le quota du jour de « openweight-large » est épuisé : bascule sur « deepseek-v4-flash » et poursuite.
```

Le modèle de remplacement est choisi ainsi :

| Situation | Modèle de remplacement |
|---|---|
| `CSAGENT_FALLBACK_MODEL` défini | Ce modèle, sur n'importe quel serveur |
| Variable absente, serveur Albert par défaut | Le modèle de code (`deepseek-v4-flash`) |
| Variable absente, autre serveur (`--endpoint`) | Aucun : ce nom de modèle n'aurait pas de sens ailleurs, l'agent s'arrête comme en 0.9.3 |

Il y a au plus **2 bascules par exécution**.

## 2. Le modèle épuisé est mémorisé

Un modèle dont le quota du jour est épuisé est retenu pendant **1 heure** (par processus `csagent`). Pendant ce temps, un nouveau tour qui choisirait ce modèle part directement sur l'autre, avec l'avertissement :

```
Le quota du jour de « openweight-large » a été épuisé plus tôt : utilisation de « deepseek-v4-flash ».
```

Cela évite d'envoyer une requête qu'on sait refusée, qui coûterait un aller-retour et du quota. Après 1 heure, le modèle est de nouveau essayé. La mémoire est perdue quand `csagent` s'arrête.

## 3. Quand la bascule n'a pas lieu

- **Limite par minute** : réessayée comme avant, jamais de bascule.
- **Image dans la conversation** : pas de bascule, car le modèle de remplacement peut ne pas savoir lire l'image. L'agent s'arrête avec le message de quota du jour.
- **Remplacement identique au modèle courant, ou lui-même épuisé** : pas de bascule.
- **`CSAGENT_FALLBACK_MODEL=off` ou `none`** : pas de bascule.
- **Plus de 2 bascules dans la même exécution** : l'agent s'arrête.

Dans tous ces cas, le message affiché est celui de la 0.9.3 : attendre la remise à zéro, ou utiliser `--model` / `--endpoint`.

## Variable d'environnement

| Variable | Effet |
|---|---|
| `CSAGENT_FALLBACK_MODEL=<modèle>` | Modèle utilisé quand le quota du jour est épuisé, sur tout serveur |
| `CSAGENT_FALLBACK_MODEL=off` (ou `none`) | Désactive la bascule |
| *(absente)* | Modèle de code sur le serveur Albert par défaut, rien ailleurs |

## Limites

- Le quota du jour est reconnu à la formulation du message de l'API (voir 0.9.3). Un fournisseur qui l'écrit autrement est traité comme une limite passagère : pas de bascule.
- La mémoire d'une heure est une durée fixe : elle ne suit pas l'heure réelle de remise à zéro du quota.
- Le modèle de remplacement peut répondre autrement que le modèle d'origine (style, qualité) ; la conversation en cours lui est transmise telle quelle.
- La consommation de jetons n'est pas réduite : l'historique envoyé à chaque étape garde la même taille (jusqu'à 96 000 caractères).
- Vérifié avec de faux serveurs, pas avec le vrai endpoint Albert. La publication native (AOT) n'a pas été essayée dans l'environnement de développement.

## Tests

217 tests (`dotnet run --project Tests -c Release`), dont les 10 nouveaux :

- choix du modèle de remplacement : serveur par défaut, autre serveur, même modèle ;
- `CSAGENT_FALLBACK_MODEL` nomme le modèle sur tout serveur ; `off` et `none` désactivent ;
- un modèle épuisé est retenu une heure, puis réessayé ;
- l'agent change de modèle sur un 429 du jour et termine la requête ;
- le tour suivant part directement sur l'autre modèle, sans nouvelle requête refusée ;
- si l'autre modèle est épuisé aussi, l'agent s'arrête avec le message de quota ;
- avec `off`, l'agent s'arrête au premier 429 ;
- pas de bascule avec une image dans la conversation ;
- une limite par minute ne déclenche jamais la bascule ;
- les avertissements de bascule sont traduits en français.

Les tests de limite 429 existants désactivent la bascule pendant leur exécution : ils vérifient l'arrêt, pas le changement de modèle.
