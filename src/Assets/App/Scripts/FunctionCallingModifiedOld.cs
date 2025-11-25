using UnityEngine;
using LLMUnity;
using UnityEngine.UI;
using System.Collections.Generic;
using System.Reflection;

namespace LLMUnitySamples
{
    public static class FunctionsModifiedOld
    {
        public static string Talk()
        {
            return "JE PARLE";
        }

        public static string Attack()
        {
            return "JE LUI TIRE DESSUS AVEC MON BAZOUZOU3000";
        }

        public static string Heal()
        {
            return "J'ENVOIE UNE POTION DE SOIN";
        }


    }

    public class FunctionCallingModifiedOld : MonoBehaviour
    {
        public LLMCharacter llmCharacter;

        void Start()
        {
            llmCharacter.grammarString = MultipleChoiceGrammar();
        }

        string[] GetFunctionNames()
        {
            List<string> functionNames = new List<string>();
            foreach (var function in typeof(FunctionsModifiedOld).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)) functionNames.Add(function.Name);
            return functionNames.ToArray();
        }

        string MultipleChoiceGrammar()
        {
            return "root ::= (\"" + string.Join("\" | \"", GetFunctionNames()) + "\")";
        }

        string ConstructPrompt(string message)
        {
            string prompt = "Which of the following choices matches best the input?\n\n";
            prompt += "Input:" + message + "\n\n";
            prompt += "Choices:\n";
            foreach(string functionName in GetFunctionNames()) prompt += $"- {functionName}\n";
            prompt += "\nAnswer directly with the choice";
            return prompt;
        }

        string CallFunction(string functionName)
        {
            return (string) typeof(FunctionsModifiedOld).GetMethod(functionName).Invoke(null, null);
        }

        public async void OnAudioTranscripted(string message)
        {
            string functionName = await llmCharacter.Chat(ConstructPrompt(message));
            string result = CallFunction(functionName);
            Debug.Log($"Calling {functionName}\n{result}");
        }

        public void CancelRequests()
        {
            llmCharacter.CancelRequests();
        }

        bool onValidateWarning = true;
        void OnValidate()
        {
            if (onValidateWarning && !llmCharacter.remote && llmCharacter.llm != null && llmCharacter.llm.model == "")
            {
                Debug.LogWarning($"Please select a model in the {llmCharacter.llm.gameObject.name} GameObject!");
                onValidateWarning = false;
            }
        }
    }
}
