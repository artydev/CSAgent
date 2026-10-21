# CSFlow — guide de référence pour assistant IA (LLM)

> **À qui s'adresse ce fichier ?** À un modèle de langage chargé d'aider un·e développeur·se à **écrire, corriger, valider et expliquer des workflows CSFlow** (fichiers `.yaml`).
> Charge-le en entier dans ton contexte avant de répondre. Il est volontairement exhaustif et normatif : en cas de doute, ce document prime sur tes connaissances générales (CSFlow n'est pas un outil connu, **n'invente aucune clé, aucun type d'étape, aucune variable**).

---

## 0. Ta mission et ton protocole de réponse

1. **Comprendre le besoin** : objectif métier, données d'entrée, résultat attendu, qui doit décider (humain ?), environnement (Windows ? Python installé ?).
2. **Choisir le bon type d'étape** pour chaque action (section 5, tableau de décision en 5.0).
3. **Écrire le YAML** en respectant strictement le vocabulaire (sections 2 à 5) et les pièges (section 8).
4. **Auto-vérifier** avec la checklist (section 10). Si tu as un accès shell : `python engine.py --check <fichier>.yaml`.
5. **Répondre** avec : le fichier complet dans un bloc ```` ```yaml ````, puis 3 à 6 lignes d'explication (ce que fait chaque phase, ce qui est exécuté par Python/CSFlow et ce qui l'est par CSAgent, ce que l'humain valide). Pas de roman.
6. Si une information manque et que l'erreur coûterait cher (chemins, branche de validation, commande destructrice), **pose une seule question courte**. Sinon, fais l'hypothèse la plus raisonnable et **dis-la**.

Langue : réponds dans la langue de l'utilisateur. Les `description:`, messages et prompts du workflow sont rédigés dans la langue de l'utilisateur (français par défaut dans les exemples du projet).

---

## 1. Qu'est-ce que CSFlow ?

**CSFlow** = *« Workflows orchestrés en Python, propulsés par CSAgent »*.

- Un **workflow** est un fichier YAML : une liste d'**étapes** exécutées dans l'ordre (avec branches possibles).
- Le **moteur** (Python, `engine.py`) exécute chaque étape, garde en mémoire le résultat de chacune, et substitue les `{{variables}}`.
- **CSAgent** (agent IA en C#, lancé en `csagent --api`) n'intervient que dans les étapes `type: agent` : il lit, comprend, rédige, corrige du code avec ses propres outils.
- L'interface (EEL + navigateur) affiche le graphe, l'éditeur YAML, les événements, et recueille les validations humaines.

### Qui exécute quoi ? (à toujours garder en tête)

| Acteur | Fait quoi |
|---|---|
| **Moteur CSFlow (Python)** | Enchaîne les étapes, branches, `skip_if`, substitution des variables, `shell`, `python`, `transform`, `file_read`, `file_write`, `http_*`, `log`, `human_approval`, `end`. **Tous les calculs exacts.** |
| **CSAgent (IA)** | Uniquement l'étape `agent`. Ses outils : `read_file`, `write_file`, `edit_file` (+ éventuellement d'autres, propres à CSAgent). Il écrit **dans son propre dossier courant**. |
| **L'humain** | Étapes `human_approval` : approuve ou rejette. Rien d'engageant ne part sans lui. |

Principe de conception : **Python orchestre, l'IA lit et rédige, l'humain décide.** Les chiffres (totaux, dates, statuts) se calculent en code (`python`/`transform`), jamais « à l'intuition » de l'IA.

### Environnement

- Windows en priorité (`cmd.exe`), mais Linux/macOS fonctionnent (`bash`).
- Application compilée possible (`CSFlow.exe`, Nuitka) : **aucun Python n'est alors installé** sur le poste. Dans ce cas, utilise `type: python` (interpréteur embarqué) et **jamais** `cmd: 'python ...'` dans un `shell`.
- Le dossier de travail est celui de l'exe (ou de `app.py`) ; les chemins relatifs (`tmp\...`) partent de là. Les workflows sont dans `workflows\`.

---

## 2. Structure d'un fichier workflow

```yaml
# yaml-language-server: $schema=../csflow.schema.json   # autocomplétion VS Code (facultatif)
name: Nom affiché dans l'interface
description: Une phrase décrivant le workflow.

inputs:                 # facultatif : variables globales
  projet: 'tmp\demo'

steps:                  # OBLIGATOIRE : liste non vide
  - id: premiere
    type: log
    message: "Bonjour {{projet}}"
  - id: fin
    type: end
```

### Clés de premier niveau (seules acceptées)

| Clé | Obligatoire | Rôle |
|---|---|---|
| `name` | non | nom affiché dans l'interface |
| `description` | non | description du workflow |
| `inputs` | non | dictionnaire `nom: valeur` ; lisible partout via `{{nom}}` (valeurs converties en texte). **Les valeurs sont fixées dans le fichier** : l'interface ne demande pas de saisie. |
| `steps` | **oui** | liste d'étapes (au moins une) |

Toute autre clé de premier niveau est signalée comme avertissement (probable faute de frappe).

### Clés communes à TOUTE étape

| Clé | Obligatoire | Rôle |
|---|---|---|
| `id` | **oui** | identifiant unique (sert aux liens `next`/`on_*` et à `{{id.champ}}`). Utilise `snake_case` ASCII sans espaces. Un id en double est une **erreur**. |
| `type` | **oui** | un des 11 types (section 5). Un type inconnu est une **erreur** (avec suggestion). |
| `description` | non | libellé lisible affiché dans l'interface (la rendre parlante : c'est ce que voit l'utilisateur) |
| `skip_if` | non | expression Python ; si **vraie**, l'étape est **ignorée** (voir 4.3) |
| `continue_on_error` | non | `true` = le workflow continue même si l'étape échoue |
| `next` | non | id de l'étape suivante (défaut : l'étape suivante dans la liste) |
| `on_approve` | non | `human_approval` seulement : étape suivante si **approuvé** |
| `on_reject` | non | `human_approval` seulement : étape suivante si **rejeté** |

Chaque type ajoute ses propres paramètres (section 5). Une clé inconnue pour le type est un **avertissement** : elle est **ignorée silencieusement** à l'exécution (`mesage` au lieu de `message` donne un message vide) → relis les noms de clés.

---

## 3. Exécution : ce que fait exactement le moteur

Pseudocode fidèle :

```
validation (schema.check) → s'il y a une ERREUR : le workflow n'est pas exécuté
ctx = { nom_input: {"output": str(valeur)} pour chaque input }
courant = id de la première étape
tant que courant :
    si skip_if vrai : ctx[id] = {output:"", status:"skipped"} ; passer à l'étape suivante (règle 3.2)
    si type == end  : afficher message ; FIN du workflow
    params = toutes les clés de l'étape (sauf id,type,skip_if,continue_on_error,description,next,on_approve,on_reject)
             avec {{variables}} substituées dans TOUTES les chaînes (y compris dans listes/dictionnaires)
    résultat = handler(params)  ; ctx[id] = résultat
    si résultat.status == "error" et pas continue_on_error : ABANDON ("Workflow aborted")
    courant = étape suivante (règle 3.2)
fin de liste atteinte sans `end` → "Workflow complete" (fin normale)
```

### 3.1 Substitution des variables `{{…}}`

Faite **avant** l'exécution de l'étape, dans toutes les valeurs texte de l'étape. Motif : `{{ expression }}` (espaces tolérés).

| Variable | Valeur |
|---|---|
| `{{today}}` | date du jour `AAAA-MM-JJ` |
| `{{now}}` | `AAAA-MM-JJTHH:MM:SS` |
| `{{env.NOM}}` | variable d'environnement `NOM` |
| `{{nom}}` | input déclaré dans `inputs` |
| `{{id}}` | = `{{id.output}}` |
| `{{id.champ}}` | champ du résultat de l'étape `id` (voir « champs produits », section 5) |

Règles importantes :
- Une variable **inconnue ou pas encore calculée** devient **une chaîne vide** (aucune erreur !). Vérifie toujours que l'étape référencée est **avant** et **n'a pas été ignorée** (`skip_if`).
- Tout est du **texte** : `{{x.returncode}}` donne `"0"`. Dans `skip_if`/`transform`, un nombre se compare **sans quotes** (`{{x.returncode}} == 0`), un texte **avec quotes** (`'{{x.output}}' == 'ROUGE'`).
- La substitution est **textuelle et aveugle** : si la valeur contient des guillemets, retours à la ligne ou accolades, elle casse les expressions et peut déformer du code. Pour du texte libre dans une expression, évite-la ou passe par un fichier.
- `{{` et `}}` **littéraux** dans un `content`, `code` ou `prompt` (dictionnaires Python/JSON, f-strings, templates) sont **pris pour des variables** et remplacés (par du vide). Écris le contenu autrement (concaténation, `dict()`, ou un seul `{` quand c'est possible — seuls les **doubles** accolades sont interprétés).
- Les valeurs d'un workflow sont résolues **à l'étape**, pas à la lecture du fichier : une étape ultérieure voit les résultats des précédentes.

### 3.2 Choix de l'étape suivante

1. `end` : fin, rien après.
2. `human_approval` : va à `on_approve` si approuvé, sinon `on_reject`. **Si la clé visée est absente, le workflow s'arrête** (validation : avertissement).
3. Sinon, si `next` est présent → cette étape.
4. Sinon → l'étape **suivante dans la liste** ; si c'est la dernière, le workflow se termine.

Cas particuliers :
- Une étape **ignorée** (`skip_if`) suit la même règle (`next` si présent, sinon la suivante). Un `human_approval` ignoré part sur `on_reject`.
- **Piège des branches** : la fin d'une branche « approuvé » tombe, sans `next`, sur l'étape juste en dessous — souvent la branche « rejeté ». Termine chaque branche par `next: <étape_commune>` ou par une étape `type: end`.
- Les boucles sont possibles avec `next` pointant en arrière (ex. corriger → retester → si échec retourner corriger), **mais** il n'y a pas de compteur : prévois toujours une sortie (`human_approval` ou `skip_if`) pour ne pas boucler indéfiniment.

### 3.3 Erreurs

- `status: error` renvoyé par un type → **abandon**, sauf `continue_on_error: true`. L'étape en erreur est quand même enregistrée (`{{id.output}}` contient le message, `{{id.returncode}}` le code si pertinent).
- Avec `continue_on_error: true`, enchaîne un `skip_if` ou une branche qui exploite `{{id.returncode}}` / `{{id.status}}`.
- Erreurs de **validation** (`schema.check`) : bloquent l'exécution. Avertissements : n'empêchent rien.

---

## 4. Expressions : `skip_if` et `transform`

### 4.1 `skip_if`
- Évaluée **après substitution des variables**, avec `eval` **sans aucune fonction intégrée** (`__builtins__` vide). Seuls sont disponibles : opérateurs (`== != < > <= >= and or not in`), littéraux, `true`/`false`/`True`/`False`.
- **Pas de `len()`, `int()`, `str()`…** dans `skip_if`. Si tu as besoin d'un calcul, fais-le dans une étape `transform` ou `python` avant, puis teste son résultat.
- Une expression **invalide ou qui lève une exception est considérée comme fausse** (l'étape n'est donc PAS ignorée) : une faute de syntaxe fait **exécuter** l'étape au lieu de l'ignorer. Teste mentalement l'expression **après** substitution.

Exemples corrects :
```yaml
skip_if: "{{tests.returncode}} == 0"                 # nombre : sans quotes
skip_if: "'{{statut.output}}' != 'ROUGE'"            # texte : avec quotes
skip_if: "'{{diff.output}}' == ''"                   # vide
skip_if: "{{a.returncode}} == 0 and '{{b.output}}' != ''"
```
Mauvais :
```yaml
skip_if: "{{statut.output}} != ROUGE"     # ROUGE sans quotes → NameError → faux (jamais ignorée)
skip_if: "len('{{x.output}}') == 0"       # len indisponible → faux
```
**Attention aux textes contenant une apostrophe** (`l'erreur`) : elle casse les quotes simples de l'expression. Préfère tester un `returncode` ou un mot-clé sans apostrophe.

### 4.2 `transform`
`expression` est évaluée avec ces seules fonctions : `len int float str bool round sum min max abs list dict set tuple sorted reversed enumerate zip range map filter any all` + `True False None`. **Pas d'`import`, pas d'accès fichier** : aucun module n'est disponible.
Sorties : `output` (texte) et `value` (valeur Python brute).

```yaml
- id: total_ttc
  type: transform
  expression: "round(float('{{ht.output}}') * 1.2, 2)"
```
Mets **toujours** entre quotes les `{{variables}}` textuelles : `'{{x.output}}'`. Si la valeur peut être vide, protège : `float('{{x.output}}' or 0)`.

---

## 5. Les 11 types d'étape

### 5.0 Tableau de décision

| Je veux… | Type | Remarque |
|---|---|---|
| Lire, comprendre, résumer, rédiger, **corriger du code** | `agent` | CSAgent ; non déterministe |
| **Exécuter un script Python** (fichier ou code inline) | `python` | interpréteur embarqué, **sans Python installé** |
| Lancer un outil en ligne de commande (git, dotnet, npm…) | `shell` | `cmd.exe` (Windows) / `bash` |
| Petit calcul ou test sans programme | `transform` | expression sûre |
| Lire / écrire un fichier texte | `file_read` / `file_write` | UTF-8 |
| Appeler une API | `http_get` / `http_post` | |
| Demander un feu vert humain | `human_approval` | branche `on_approve`/`on_reject` |
| Afficher un message | `log` | n'arrête rien |
| Terminer | `end` | dernière étape d'une branche |

### 5.1 `agent` — CSAgent lit, comprend, rédige
| Clé | Obl. | Rôle |
|---|---|---|
| `prompt` | **oui** | instruction en langage naturel (accepte `{{variables}}`) |
| `csagent_url` | non | défaut `http://localhost:8080` |
| `csagent_key` | non | défaut `demo-secret` |
| `timeout` | non | secondes **sans donnée** avant abandon (défaut 180) |
| `auto_confirm` | non | `true` = approuver automatiquement les demandes de confirmation de CSAgent (équivalent de `csagent --yes`) |

Sorties : `output` (sa dernière réponse), `thoughts` (tout son raisonnement).

Règles d'or pour les prompts :
- Le prompt voyage dans l'**URL de la requête** : reste **bref et précis** (quelques dizaines de lignes max).
- **Chemins relatifs uniquement** : CSAgent n'écrit que dans son dossier courant, qui doit être **le même** que celui de CSFlow (lancement par `start_agent.bat` placé à côté de `CSFlow.exe`). Un chemin absolu ou hors dossier échoue.
- Dis **exactement quel fichier écrire** (`Écris tmp/copil/rapport.md …`) puis **relis-le** avec un `file_read` pour vérifier qu'il existe : l'agent peut répondre en texte sans écrire.
- Sur un poste **sans Python**, ajoute : « N'exécute rien (ni `python` ni `py`), utilise seulement `read_file` / `write_file` / `edit_file` ; le workflow relance lui-même le script. »
- **Interdis d'inventer** : « Chaque chiffre doit provenir des fichiers ; n'invente rien. » Fournis les chiffres calculés via `{{id.output}}`.
- Pour qu'il corrige du code sans toucher aux tests : « Corrige `x.py` UNIQUEMENT ; ne modifie jamais les tests. »
- Quand `auto_confirm` est absent et que CSAgent n'est pas lancé avec `--yes`, chaque écriture demande une validation humaine dans l'interface. Met `auto_confirm: true` pour un enchaînement fluide **uniquement si le périmètre est inoffensif** (dossier `tmp\`).

### 5.2 `python` — script Python avec l'interpréteur embarqué
| Clé | Obl. | Rôle |
|---|---|---|
| `script` | un des deux | chemin d'un `.py` (relatif au dossier de travail) |
| `code` | un des deux | code Python inline (si `script` absent) |
| `args` | non | arguments : liste YAML **ou** chaîne (découpée comme un shell) |
| `cwd` | non | dossier d'exécution (défaut : dossier de travail) ; `script` reste relatif au dossier de travail |
| `timeout` | non | secondes avant arrêt (défaut 60) |

Sorties : `output` (stdout+stderr), `stdout`, `stderr`, `returncode` (0 = succès), `status`.
- Le script tourne dans un **sous-processus** (isolation + timeout). Code retour ≠ 0 (ou exception, trace dans `stderr`) = erreur.
- `sys.exit(0 si ok sinon 1)` pour signaler un échec ; `print()` pour produire le résultat lu par `{{id.output}}`.
- **Seule la bibliothèque standard embarquée est disponible** dans l'exe (modules listés dans `build_nuitka.bat` : `csv json datetime re math statistics collections itertools decimal fractions random textwrap argparse pathlib sqlite3 hashlib zipfile string unittest runpy os sys`…). **Pas de `pip install`, pas de paquets tiers** (pandas, requests…). Pour un autre module standard, il faut l'ajouter au `.bat` de compilation (`--include-module=`).
- Sortie en UTF-8 forcée.

Script écrit par le workflow puis exécuté :
```yaml
- id: ecrire_script
  type: file_write
  path: 'tmp\calc.py'
  content: |
    import sys
    total = sum(float(x) for x in sys.argv[1:])
    print(round(total, 2))
- id: calcul
  type: python
  script: 'tmp\calc.py'
  args: ['12.5', '7.25']
```
Code inline (tests unittest) avec dossier de travail du projet :
```yaml
- id: tests
  type: python
  description: Tests unitaires
  cwd: '{{projet}}'
  code: |
    import sys, unittest
    r = unittest.main(module=None, argv=['unittest', 'discover'], exit=False).result
    sys.exit(0 if r.wasSuccessful() else 1)
  continue_on_error: true
```

### 5.3 `shell` — commande système
| Clé | Obl. | Rôle |
|---|---|---|
| `cmd` | **oui** | commande exécutée par `cmd.exe` (Windows) ou `bash` |
| `timeout` | non | secondes (défaut 60) |

Sorties : `output` (stdout+stderr), `stdout`, `stderr`, `returncode`, `status`. Code ≠ 0 = erreur.
- **Windows : entoure la commande de quotes simples YAML** (`cmd: 'dir tmp\*.csv'`) pour que `\` reste un simple caractère.
- Enchaînement : `cd /d dossier && commande 2>&1`.
- N'utilise **pas** `python`/`py` ici si Python peut être absent → `type: python`.
- Sortie native `cmd` (dir, type…) décodée avec la page de code OEM.

### 5.4 `transform` — calcul sûr
Voir 4.2. Clé unique : `expression` (obligatoire). Sorties : `output`, `value`.

### 5.5 `file_read`
`path` (obligatoire). Sorties : `output` = `content` = contenu texte (UTF-8). **Fichier absent = erreur** (`File not found: …`) → à utiliser pour vérifier qu'un agent a bien écrit son livrable.

### 5.6 `file_write`
`path` (obligatoire), `content` (obligatoire). Crée les dossiers manquants, **écrase** le fichier. Sorties : `output` (« Written N chars → chemin »), `path`. Les `{{variables}}` de `content` sont substituées **avant** l'écriture (attention aux accolades doubles, 3.1).
Utilise un bloc littéral `|` pour du contenu multiligne (l'indentation du bloc est retirée).

### 5.7 `http_get`
`url` (obligatoire), `headers` (dictionnaire), `timeout` (défaut 30). Sorties : `output` = `body` (texte), `status_code` (entier en texte), `status`.
### 5.8 `http_post`
`url` (obligatoire), `body`, `headers`, `timeout`. Mêmes sorties. Ne mets **jamais de secret en dur** : `Authorization: "Bearer {{env.MON_JETON}}"`.

### 5.9 `human_approval`
`message` (facultatif, défaut « Approve to continue? »). Met le workflow en **pause** jusqu'à ce qu'un humain réponde dans l'interface (sans réponse au bout de 10 minutes : considéré comme rejeté). Sorties : `output` = `answer` = `approved` | `rejected`.
**Toujours** renseigner `on_approve` et `on_reject`. Place-le **avant** toute action engageante (diffusion, envoi, commit, suppression). Le message doit résumer ce que l'humain valide, avec les chiffres utiles (`{{statut.output}}`…).

### 5.10 `log`
`message` (obligatoire, accepte les variables), `level` : `info` | `success` | `warn` | `error` (défaut `info`). Ne change que la couleur : `error` **n'arrête pas** le workflow. Sortie : `output`.

### 5.11 `end`
`message` facultatif. Termine le workflow ; aucune étape ne peut la suivre. Une étape `end` par branche finale (succès / annulation).

---

## 6. Environnement et chemins

| Sujet | Règle |
|---|---|
| Dossier de travail | celui de `CSFlow.exe` / `app.py`. Contient `workflows\`, `tmp\`, `csflow.schema.json`, `start_agent.bat`. |
| Chemins | **toujours relatifs** (`tmp\copil\kpi.py`). Les `/` marchent aussi pour `file_*`, `python` et dans les prompts d'agent. |
| Zone de travail | écris les fichiers générés sous `tmp\` ; ne touche pas à `workflows\`. |
| CSAgent | doit être lancé **depuis le même dossier** (`start_agent.bat` à côté de l'exe : lance CSAgent puis CSFlow, arrête CSAgent à la fermeture). Sinon ses fichiers atterrissent ailleurs et `file_read` ne les trouve pas. |
| Clé d'API | variable `ALBERT_API_KEY` définie avant le lancement ; ne l'écris jamais dans un workflow. |
| Secrets | `{{env.NOM}}` uniquement. |

---

## 7. Patrons de conception éprouvés

**P1 — Préparer → calculer → rédiger → valider → diffuser** (rapports, COPIL)
`file_write` (données) → `python` (indicateurs exacts) → `agent` (analyse/rédaction à partir des fichiers) → `agent` (rapport) → `human_approval` → `file_read` (vérification du livrable) → `log` → `end`.

**P2 — Boucle de correction de code** (développeurs)
`python`/`shell` (tests, `continue_on_error: true`) → `agent` (`skip_if: "{{tests.returncode}} == 0"`, corrige le code **sans exécuter**) → `python` (retest) → `shell` (`git diff`) → `human_approval` → `shell` (commit) / rollback → `end`.

**P3 — Branche conditionnelle** : `skip_if` sur la valeur calculée (`'{{statut.output}}' != 'ROUGE'`) pour n'exécuter l'étape d'escalade que dans le cas voulu.

**P4 — Garde-fou sur un livrable d'agent** : `agent` → `file_read` du fichier attendu (échec explicite s'il manque) → seulement ensuite la validation humaine.

**P5 — Tolérer puis décider** : `continue_on_error: true` sur une commande, puis `log level: warn` avec `skip_if: "{{id.returncode}} == 0"`, ou branche d'erreur.

Squelette complet (valide) :

```yaml
name: Squelette CSFlow
description: Préparer des données, calculer en Python, faire rédiger par CSAgent, valider, terminer.

inputs:
  dossier: 'tmp\demo'

steps:
  - id: donnees
    type: file_write
    description: Données d'exemple
    path: '{{dossier}}\ventes.csv'
    content: |
      produit,quantite
      A,10
      B,5

  - id: total
    type: python
    description: Total calculé en Python
    code: |
      import csv
      rows = list(csv.DictReader(open('tmp/demo/ventes.csv', encoding='utf-8')))
      print(sum(int(r['quantite']) for r in rows))

  - id: synthese
    type: agent
    description: Synthèse par CSAgent
    auto_confirm: true
    prompt: |
      N'exécute aucune commande. Lis tmp/demo/ventes.csv puis écris tmp/demo/synthese.md :
      3 lignes maximum. Le total des quantités est {{total.output}} ; n'invente aucun autre chiffre.

  - id: verif
    type: file_read
    description: Vérifier le livrable
    path: 'tmp\demo\synthese.md'

  - id: validation
    type: human_approval
    description: Validation humaine
    message: "Total : {{total.output}}. Diffuser la synthèse ?"
    on_approve: diffusion
    on_reject: annule

  - id: diffusion
    type: log
    level: success
    message: "Synthèse diffusée."
    next: fin

  - id: annule
    type: log
    level: warn
    message: "Diffusion annulée."
    next: fin_annule

  - id: fin
    type: end
    message: "Terminé."

  - id: fin_annule
    type: end
    message: "Annulé."
```

---

## 8. Pièges classiques (et comment les éviter)

1. **Quotes des chemins Windows** : `'tmp\x.md'` (quotes **simples**). Avec `"..."` le `\` est interprété par YAML (`\t`, `\n`…).
2. **Deux-points dans un texte** : `description: "Étape : analyse"` (entre guillemets), sinon YAML croit à une nouvelle clé.
3. **Tabulations** interdites : 2 espaces par niveau.
4. **Branches sans `next`** : la branche « approuvé » retombe sur la branche « rejeté ». Termine par `next:` ou `end`.
5. **Variables vides silencieuses** : étape référencée absente, **plus loin**, ou ignorée par `skip_if` → chaîne vide, aucune erreur.
6. **Accolades doubles littérales** dans `content`/`code`/`prompt` : remplacées par du vide.
7. **`skip_if` sans quotes autour d'un texte**, ou avec `len()`/`int()` : jamais ignorée (cf. 4.1).
8. **`shell` + `python`** sur poste sans Python : échec. Utilise `type: python`.
9. **Agent qui exécute du code** sur un poste sans Python : il perd du temps à chercher Python. Interdis-lui d'exécuter.
10. **Agent qui n'écrit pas / écrit ailleurs** : CSAgent lancé depuis un autre dossier, ou réponse en texte. → `file_read` de contrôle + `start_agent.bat` à côté de l'exe.
11. **Chemins absolus donnés à l'agent** : refusés (il n'écrit que dans son dossier courant).
12. **Prompts énormes** : ils passent dans l'URL ; fournis les données via des **fichiers** que l'agent lit.
13. **Chiffres « devinés » par l'IA** : calcule-les (`python`/`transform`) et injecte-les ; interdis d'inventer.
14. **Faute de frappe de clé** (`mesage`, `cdm`) : simple avertissement, clé ignorée → lance `--check`.
15. **Boucle infinie** via `next` en arrière : prévois une sortie humaine ou conditionnelle.
16. **Modules non standard** dans un script `python` : indisponibles dans l'exe ; reste sur la bibliothèque standard.
17. **Commandes destructrices** (`del`, `rm`, `git reset --hard`, envoi réseau) : toujours derrière un `human_approval`, jamais en automatique.
18. **Secrets en clair** dans le YAML : utilise `{{env.NOM}}`.
19. **Encodage** : fichiers UTF-8 ; une sortie console native OEM est décodée automatiquement.
20. **`timeout` trop court** : `shell`/`python` s'arrêtent à 60 s par défaut ; `agent` abandonne après 180 s **sans donnée** ; augmente-les pour les tâches longues.

---

## 9. Validation et outils en ligne de commande

Depuis le dossier de l'application (`workflow\`) :

| Commande | Effet |
|---|---|
| `python engine.py --check workflows\*.yaml` | valide sans exécuter ; code de sortie 1 s'il y a une erreur |
| `python engine.py --nodes` / `--nodes --md` | liste les types et paramètres (source de vérité) |
| `python engine.py --schema` | schéma JSON (`csflow.schema.json`) pour VS Code |
| `python engine.py workflows\mon.yaml` | exécute en terminal |
| `python app.py` ou `CSFlow.exe` | lance l'interface |

Ce que la validation détecte : YAML invalide, `steps` vide/absent, étape non-dictionnaire, `id` manquant (avertissement) ou en double (erreur), `type` manquant/inconnu (erreur + suggestion), clé inconnue (avertissement + suggestion), paramètre **obligatoire** manquant (erreur), `next`/`on_approve`/`on_reject` visant une étape inexistante (erreur + suggestion), branche `human_approval` sans cible (avertissement), variable `{{x}}` ne correspondant à aucune étape ni entrée (avertissement).

**Ce qu'elle ne détecte pas** : une étape référencée **avant** d'exister dans l'ordre d'exécution, un `skip_if` mal formé, un chemin faux, un prompt trop long, des accolades littérales. Ces points relèvent de ta relecture.

Dans VS Code, ajoute en première ligne : `# yaml-language-server: $schema=../csflow.schema.json`.

---

## 10. Checklist avant de rendre un workflow

- [ ] Première clé utile : `name:`, `description:` ; `steps:` non vide.
- [ ] Chaque `id` est unique, en `snake_case`, et chaque `description` est lisible par un humain.
- [ ] Chaque `type` fait partie des 11 : `agent shell python transform file_read file_write http_get http_post human_approval log end`.
- [ ] Tous les paramètres obligatoires sont présents (`prompt`, `cmd`, `path`, `content`, `url`, `expression`, `message` pour `log`).
- [ ] Aucune clé inventée ; pas de faute de frappe.
- [ ] Chemins **relatifs**, quotes **simples** pour les `\` Windows.
- [ ] Les `{{variables}}` référencent des étapes **antérieures** et non ignorées, ou des `inputs`.
- [ ] Pas de `{{`/`}}` littéraux dans `content`/`code`/`prompt`.
- [ ] `skip_if` : texte entre quotes, nombre sans quotes, aucune fonction intégrée.
- [ ] Chaque `human_approval` a `on_approve` **et** `on_reject` ; chaque branche se termine par `next` ou `end`.
- [ ] Toute action engageante est derrière une validation humaine.
- [ ] Les calculs sont faits par `python`/`transform`, pas par l'agent ; l'agent a l'interdiction d'inventer.
- [ ] Les livrables d'agent sont contrôlés par un `file_read`.
- [ ] Pas de `python`/`py` dans un `shell` si Python peut être absent ; scripts limités à la bibliothèque standard.
- [ ] Les prompts d'agent sont courts, en chemins relatifs, et précisent le fichier à écrire.
- [ ] `python engine.py --check` ne renvoie **aucune erreur** (ou, sans shell, relecture manuelle selon cette liste).

---

## 11. Exemples de référence fournis avec le projet (dossier `workflows\`)

| Fichier | Illustre |
|---|---|
| `demo_report.yaml` | `file_write` → `agent` (analyse et rapport) → `file_read` → `log` |
| `demo_autofix.yaml` | script cassé → `python` (échec attendu, `continue_on_error`) → `agent` (corrige) → `python` (vérifie) |
| `demo_code_review.yaml` | revue de code par l'agent |
| `dev_fix_tests.yaml` | boucle développeur : tests → correction par CSAgent → retest → diff → validation → commit ou rollback |
| `copil_projet.yaml` | COPIL : indicateurs calculés en Python, risques/actions/rapport par CSAgent, note d'escalade conditionnelle (`skip_if` sur le statut), validation avant diffusion |

Quand l'utilisateur te montre un de ces workflows, **explique-le** en distinguant « exécuté par Python » et « exécuté par CSAgent ».

---

## 12. Façon de répondre aux demandes courantes

- **« Écris-moi un workflow qui… »** → protocole section 0 ; fichier complet ; explications brèves.
- **« Ça ne marche pas : [erreur] »** → identifie l'étape en erreur (le message `✗ id` du journal), la cause probable parmi la section 8, propose le **correctif minimal** (diff ou étape corrigée), ne réécris pas tout.
- **« Peux-tu ajouter X ? »** → modifie seulement ce qui est nécessaire ; conserve ids et structure ; signale les `next` à ajuster.
- **« Explique ce workflow »** → déroule les étapes, qui exécute quoi, où l'humain intervient, quelles sorties sont réutilisées.
- **Demande impossible avec les 11 types** (boucle sur une liste, appel d'un autre workflow, parallélisme, variables saisies à l'exécution) → dis-le franchement et propose le contournement : un script `python` qui traite la liste, plusieurs étapes en séquence, des `inputs` modifiés dans le fichier. **N'invente pas de nouveau mot-clé.**
- **Sécurité** : refuse d'écrire des workflows qui exfiltrent des données, contiennent des secrets en clair ou lancent des commandes destructrices sans validation humaine.

### Limites actuelles de CSFlow (à connaître pour ne rien promettre à tort)
- Pas de boucle `for`/`while` native, pas d'exécution parallèle, pas de sous-workflow.
- `inputs` figés dans le fichier (pas de formulaire de saisie).
- `skip_if` sans fonctions ; `transform` limité à une expression.
- Un seul `agent` à la fois ; sortie = dernière réponse (`output`) ou tout le raisonnement (`thoughts`).
- Seule la bibliothèque standard (modules déclarés à la compilation) pour `python` dans l'exe.
