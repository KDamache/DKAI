using UnityEngine;
using LLMUnity;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System;
using System.Text;

namespace LLMUnitySamples
{
    [AttributeUsage(AttributeTargets.Method)]
    public class GameActionAttribute : Attribute
    {
        public string Description;
        public GameActionAttribute(string description) { Description = description; }
    }

    [Serializable]
    public class AICommand
    {
        public string method;
        public string[] args;
    }

    public class FunctionCallingModifiedNew : MonoBehaviour
    {
        public LLMCharacter llmCharacter;

        private Dictionary<string, MethodInfo> availableMethods = new Dictionary<string, MethodInfo>();

        void Start()
        {
            ScanForMethods();

            llmCharacter.grammarJSONString = GenerateJSONSchema();

            Debug.Log("Schéma JSON généré : " + llmCharacter.grammarJSONString);
        }

        void ScanForMethods()
        {
            var methods = typeof(GameActions).GetMethods(BindingFlags.Public | BindingFlags.Static);
            foreach (var method in methods)
            {
                if (method.GetCustomAttribute<GameActionAttribute>() != null)
                {
                    availableMethods.Add(method.Name, method);
                }
            }
        }

        // --- NOUVEAU : GÉNÉRATION DU JSON SCHEMA ---
        // On construit un schéma qui utilise "oneOf" pour obliger le LLM à choisir
        // une signature valide parmi celles disponibles.
        string GenerateJSONSchema()
        {
            StringBuilder sb = new StringBuilder();

            // Début du schéma racine
            sb.Append("{ \"type\": \"object\", \"properties\": { \"method\": { \"type\": \"string\" }, \"args\": { \"type\": \"array\" } }, \"oneOf\": [");

            int methodCount = 0;
            foreach (var kvp in availableMethods)
            {
                if (methodCount > 0) sb.Append(","); // Virgule entre les options

                string methodName = kvp.Key;
                ParameterInfo[] pars = kvp.Value.GetParameters();

                // Construction de l'objet pour UNE méthode spécifique
                sb.Append("{");
                sb.Append("\"type\": \"object\",");
                sb.Append("\"properties\": {");

                // 1. Contrainte stricte sur le nom de la méthode (const)
                sb.Append($"\"method\": {{ \"const\": \"{methodName}\" }},");

                // 2. Contrainte stricte sur les arguments (tuple validation)
                sb.Append("\"args\": {");
                sb.Append("\"type\": \"array\",");
                sb.Append($"\"minItems\": {pars.Length}, \"maxItems\": {pars.Length},");

                sb.Append("\"items\": [");
                for (int i = 0; i < pars.Length; i++)
                {
                    if (i > 0) sb.Append(",");
                    // NOTE IMPORTANTE : 
                    // Votre classe AICommand attend un string[] args.
                    // Pour que JsonUtility fonctionne, le JSON doit contenir des chaînes, même pour les int/float.
                    // On force donc le type "string" dans le schéma, mais on ajoute une description pour guider le LLM.
                    string typeName = pars[i].ParameterType.Name;
                    sb.Append($"{{ \"type\": \"string\", \"description\": \"Argument '{pars[i].Name}' of type {typeName}\" }}");
                }
                sb.Append("]"); // Fin items
                sb.Append("}"); // Fin args

                sb.Append("},");
                sb.Append("\"required\": [\"method\", \"args\"]");
                sb.Append("}"); // Fin de l'objet méthode

                methodCount++;
            }

            sb.Append("] }"); // Fin du oneOf et du root
            return sb.ToString();
        }

        string ConstructPrompt(string input)
        {
            StringBuilder prompt = new StringBuilder();
            // On simplifie le prompt car la grammaire fait le gros du travail de structure
            prompt.AppendLine("Tu es un personnage non joueur (PNJ). Choisis l'action en JSON la plus approprié à la demande du joueur. fournis aussi les paramètres qui seront les plus adapté à la réponse du joueur");
            prompt.AppendLine("Actions disponibles :");

            foreach (var kvp in availableMethods)
            {
                var attr = kvp.Value.GetCustomAttribute<GameActionAttribute>();
                string paramsDoc = string.Join(", ", kvp.Value.GetParameters().Select(p => p.Name + " (" + p.ParameterType.Name + ")"));
                prompt.AppendLine($"- {kvp.Key}({paramsDoc}) : {attr.Description}");
            }

            prompt.AppendLine($"\nDemande joueur : \"{input}\"");
            return prompt.ToString();
        }

        public async void OnAudioTranscripted(string message)
        {
            string prompt = ConstructPrompt(message);

            // Le LLM est maintenant contraint par le grammarJSONString
            string jsonResult = await llmCharacter.Chat(prompt);

            Debug.Log($"LLM Raw JSON : {jsonResult}");

            try
            {
                AICommand cmd = JsonUtility.FromJson<AICommand>(jsonResult);
                ExecuteCommand(cmd);
            }
            catch (Exception e)
            {
                Debug.LogError($"Erreur Parsing/Exec : {e.Message}");
            }
        }

        void ExecuteCommand(AICommand cmd)
        {
            if (availableMethods.ContainsKey(cmd.method))
            {
                MethodInfo method = availableMethods[cmd.method];
                ParameterInfo[] definedParams = method.GetParameters();

                if (cmd.args == null || cmd.args.Length != definedParams.Length)
                {
                    Debug.LogWarning($"Args mismatch pour {cmd.method}");
                    return;
                }

                object[] finalArgs = new object[definedParams.Length];
                for (int i = 0; i < definedParams.Length; i++)
                {
                    // La conversion magique String -> Int/Float se fait ici
                    finalArgs[i] = Convert.ChangeType(cmd.args[i], definedParams[i].ParameterType);
                }

                method.Invoke(null, finalArgs);
            }
        }
    }

    // --- VOS ACTIONS (Inchangées) ---
    public static class GameActions
    {
        [GameAction("Dire une phrase à haute voix.")]
        public static void Talk(string phrase)
        {
            Debug.Log($"[Action] Talk: {phrase}");
        }

        [GameAction("Attaquer une cible spécifique.")]
        public static void Attack(string enemyName)
        {
            Debug.Log($"[Action] Attack sur: {enemyName}");
        }

        [GameAction("Se soigner avec un item.")]
        public static void Heal(string item, int amount)
        {
            Debug.Log($"[Action] Heal avec {item} pour {amount} PV.");
        }

    }
}