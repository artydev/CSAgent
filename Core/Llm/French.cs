namespace CsAgent.Core.Llm;

/// <summary>
/// French translations of the routing messages. The key is the English text exactly as written in the
/// code (an interpolated message uses its composite format: {0}, {1}...; a literal brace is written {{ }}).
/// </summary>
internal static class French
{
    internal static readonly Dictionary<string, string> Messages = new()
    {
        // ── reasons shown after [model: ...] and by --explain-routing ──
        ["default"] = "par défaut",
        ["image in the conversation"] = "image dans la conversation",
        ["follows a web search"] = "fait suite à une recherche web",
        ["follows a turn that used tools"] = "fait suite à un tour qui a utilisé des outils",
        ["general question"] = "question générale",
        ["code in the message"] = "code dans le message",
        ["attached file"] = "fichier joint",
        ["file path"] = "chemin de fichier",
        ["file name"] = "nom de fichier",
        ["mentions code"] = "parle de code",
        ["action on your computer ('{0}')"] = "action sur votre ordinateur (« {0} »)",
        ["web search ('{0}')"] = "recherche web (« {0} »)",
        ["rule '{0}'"] = "règle « {0} »",
        [" (no chat model on this endpoint)"] = " (pas de modèle chat sur ce serveur)",
        ["chat model '{0}' {1}"] = "le modèle chat « {0} » est écarté : {1}",
        ["not in the endpoint's model list"] = "absent de la liste des modèles du serveur",
        ["reported unavailable"] = "signalé indisponible",
        ["type '{0}' cannot chat"] = "le type « {0} » ne sait pas discuter",

        ["Usage: csagent --prompt \"<text>\"   (runs one request, prints the answer and exits)"] =
            "Usage : csagent --prompt \"<texte>\"   (exécute une demande, affiche la réponse puis quitte)",

        // ── --init-routing / --explain-routing ──
        ["  created    {0}"] = "  créé       {0}",
        ["  kept       {0} (already exists)"] = "  conservé   {0} (existe déjà)",
        ["  {0,-10} {1}"] = "  {0,-10} {1}",
        ["refreshed"] = "régénéré",
        ["Error: cannot write the routing rules in '{0}': {1}"] = "Erreur : impossible d'écrire les règles de routage dans « {0} » : {1}",
        ["Routing rules folder: {0}"] = "Dossier des règles de routage : {0}",
        ["Edit models.json, keywords.json and rules.json; changes apply to the next message."] =
            "Modifiez models.json, keywords.json et rules.json ; les changements s'appliquent au message suivant.",
        ["Try a message without calling any model:  csagent --explain-routing \"open in Edge browser\""] =
            "Testez un message sans appeler de modèle :  csagent --explain-routing \"ouvre Edge\"",
        ["Usage: csagent --explain-routing \"<message>\""] = "Usage : csagent --explain-routing \"<message>\"",
        ["Message : {0}"] = "Message : {0}",
        ["Rules   : {0}"] = "Règles  : {0}",
        ["built-in (no LLMRoutingRules folder found)"] = "intégrées (aucun dossier LLMRoutingRules trouvé)",
        ["Model   : {0}"] = "Modèle  : {0}",
        ["Profile : {0}"] = "Profil  : {0}",
        ["Reason  : {0}"] = "Raison  : {0}",
        ["Warning : {0}"] = "Alerte  : {0}",
        ["Note    : automatic routing is off (--no-route or CSAGENT_ROUTING=off): the code model is always used."] =
            "Note    : le routage automatique est désactivé (--no-route ou CSAGENT_ROUTING=off) : le modèle code est toujours utilisé.",
        ["(A chat choice is also checked against the endpoint's model list when a run starts; a message that"] =
            "(Un choix chat est aussi vérifié dans la liste des modèles du serveur au lancement ; un message qui",
        [" follows a tool-using turn or contains an image can be routed differently.)"] =
            " suit un tour avec outils ou contient une image peut être routé autrement.)",

        // ── warnings about the files ──
        ["--rules: the folder '{0}' does not exist; the built-in rules are used."] =
            "--rules : le dossier « {0} » n'existe pas ; les règles intégrées sont utilisées.",
        ["{0}: cannot be read ({1}); the built-in rules are used."] =
            "{0} : lecture impossible ({1}) ; les règles intégrées sont utilisées.",
        ["{0}: cannot be read now ({1}); the built-in values are used."] =
            "{0} : lecture impossible pour le moment ({1}) ; les valeurs intégrées sont utilisées.",
        ["{0}: the file is larger than {1} KB; the built-in values are used."] =
            "{0} : le fichier dépasse {1} Ko ; les valeurs intégrées sont utilisées.",
        ["{0}: the file must contain a JSON object ({{ ... }}); the built-in values are used."] =
            "{0} : le fichier doit contenir un objet JSON ({{ ... }}) ; les valeurs intégrées sont utilisées.",
        ["{0}: invalid JSON ({1}); the built-in values are used."] =
            "{0} : JSON invalide ({1}) ; les valeurs intégrées sont utilisées.",
        ["{0}: unknown key '{1}' (expected code, chat, vision)."] =
            "{0} : clé inconnue « {1} » (attendu : code, chat, vision).",
        ["{0}: '{1}' must be a model name (text)."] = "{0} : « {1} » doit être un nom de modèle (texte).",
        ["{0}: '{1}' is not a valid model name: '{2}'."] = "{0} : « {1} » n'est pas un nom de modèle valide : « {2} ».",
        ["{0}: 'follow_up_max_words' must be a number between 1 and 50."] =
            "{0} : « follow_up_max_words » doit être un nombre entre 1 et 50.",
        ["{0}: unknown key '{1}'."] = "{0} : clé inconnue « {1} ».",
        ["{0}: '{1}.{2}' is not understood (expected \"add\" and \"remove\" lists)."] =
            "{0} : « {1}.{2} » n'est pas compris (attendu : des listes « add » et « remove »).",
        ["{0}: '{1}' must be a list, or {{ \"add\": [...], \"remove\": [...] }}."] =
            "{0} : « {1} » doit être une liste, ou {{ \"add\": [...], \"remove\": [...] }}.",
        ["{0}: '{1}.{2}' must contain text only."] = "{0} : « {1}.{2} » ne doit contenir que du texte.",
        ["{0}: '{1}' takes single words; '{2}' has a space."] =
            "{0} : « {1} » contient des mots simples ; « {2} » contient une espace.",
        ["{0}: '{1}' takes single words; '{2}' has a space (phrases go in web_phrases or in a rule)."] =
            "{0} : « {1} » contient des mots simples ; « {2} » contient une espace (les expressions vont dans web_phrases ou dans une règle).",
        ["{0}: 'rules' must be a list."] = "{0} : « rules » doit être une liste.",
        ["{0}: unknown key '{1}' (expected \"rules\")."] = "{0} : clé inconnue « {1} » (attendu : « rules »).",
        ["{0}: only the first {1} rules are used."] = "{0} : seules les {1} premières règles sont utilisées.",
        ["{0}: rule {1} must be an object ({{ ... }}); skipped."] =
            "{0} : la règle {1} doit être un objet ({{ ... }}) ; ignorée.",
        ["{0}: 'enabled' must be true or false."] = "{0} : « enabled » doit valoir true ou false.",
        ["{0}: 'use' must be \"code\", \"chat\", \"vision\" or a model name."] =
            "{0} : « use » doit valoir \"code\", \"chat\", \"vision\" ou un nom de modèle.",
        ["{0}: 'min_words' must be a number."] = "{0} : « min_words » doit être un nombre.",
        ["{0}: 'max_words' must be a number."] = "{0} : « max_words » doit être un nombre.",
        ["{0}: unknown key '{1}' (expected name, use, contains, contains_all, not_contains, starts_with, min_words, max_words, enabled)."] =
            "{0} : clé inconnue « {1} » (attendu : name, use, contains, contains_all, not_contains, starts_with, min_words, max_words, enabled).",
        ["{0}: 'use' is missing; rule skipped."] = "{0} : « use » est absent ; règle ignorée.",
        ["{0}: no condition (contains, starts_with...); rule skipped."] =
            "{0} : aucune condition (contains, starts_with...) ; règle ignorée.",
        ["{0}: its conditions are empty; rule skipped."] = "{0} : ses conditions sont vides ; règle ignorée.",
        ["{0}: '{1}' must contain text only."] = "{0} : « {1} » ne doit contenir que du texte.",
        ["{0}: '{1}' must be a text or a list of texts."] = "{0} : « {1} » doit être un texte ou une liste de textes.",

        // ── comments written into the generated files ──
        ["Which model plays each profile. null = the built-in value ({0} / {1} / {2}); the chat model is only used on the default endpoint. --code-model / --chat-model / --vision-model (and CSAGENT_MODEL_*) win over this file."] =
            "Quel modèle joue chaque profil. null = la valeur intégrée ({0} / {1} / {2}) ; le modèle chat n'est utilisé que sur le serveur par défaut. --code-model / --chat-model / --vision-model (et CSAGENT_MODEL_*) l'emportent sur ce fichier.",
        ["Add words to the built-in lists, or remove some. Words are compared without case or accents. See keywords.defaults.json for the built-in lists. A list can also be a plain array (words to add)."] =
            "Ajoutez des mots aux listes intégrées, ou retirez-en. Les mots sont comparés sans casse ni accents. Voir keywords.defaults.json pour les listes intégrées. Une liste peut aussi être un simple tableau (mots à ajouter).",
        ["The built-in lists, for reference. This file is not read: change keywords.json instead (\"add\" words here that are missing, \"remove\" the ones you do not want). Regenerated by --init-routing."] =
            "Les listes intégrées, pour information. Ce fichier n'est pas lu : modifiez keywords.json à la place (« add » pour les mots qui manquent, « remove » pour ceux que vous ne voulez pas). Régénéré par --init-routing.",
        ["Your own rules, checked in order before the built-in logic: the first rule that matches decides. Conditions (all that are present must hold): contains (any of), contains_all, not_contains, starts_with, min_words, max_words. Words are compared without case or accents; \"recherch*\" matches any word that starts with it; a phrase is several words (\"sur le web\"). use: \"code\", \"chat\", \"vision\", or a model name. \"enabled\": false switches a rule off. --model and an image in the conversation win over these rules; --no-route turns them off."] =
            "Vos propres règles, testées dans l'ordre avant la logique intégrée : la première qui correspond décide. Conditions (toutes celles présentes doivent être vraies) : contains (l'un des mots), contains_all, not_contains, starts_with, min_words, max_words. Les mots sont comparés sans casse ni accents ; \"recherch*\" correspond à tout mot qui commence ainsi ; une expression est composée de plusieurs mots (\"sur le web\"). use : \"code\", \"chat\", \"vision\" ou un nom de modèle. \"enabled\": false désactive une règle. --model et une image dans la conversation l'emportent sur ces règles ; --no-route les désactive.",
        ["tickets go to the code model"] = "les tickets vont au modèle code",
        ["translations to the chat model"] = "les traductions vont au modèle chat",
        ["a specific model for long documents"] = "un modèle précis pour les longs documents",
    };
}
