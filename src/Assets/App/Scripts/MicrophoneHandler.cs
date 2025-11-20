using System;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Events;
using Whisper.Utils;
using Debug = UnityEngine.Debug;

namespace Whisper.Samples
{
    // 1. Définition de l'Enum pour faciliter la sélection dans l'inspecteur
    public enum LanguageOption
    {
        Auto,
        English,
        French,
        Spanish,
        German,
        Italian,
        Japanese,
        Chinese
        // Tu peux ajouter d'autres langues ici si nécessaire
    }

    /// <summary>
    /// Gère l'enregistrement microphone et la transcription Whisper via Push-To-Talk.
    /// </summary>
    public class MicrophoneHandler : MonoBehaviour
    {
        [Header("Components")]
        public WhisperManager whisper;
        public MicrophoneRecord microphoneRecord;

        [Header("Options")]
        public bool translateToEnglish = false;
        public LanguageOption language = LanguageOption.Auto;
        public bool printResultInConsole = true;

        [Header("Push-To-Talk")]
        [SerializeField] private bool enablePushToTalk = true;
        [SerializeField] private KeyCode pushToTalkKey = KeyCode.V;

        [Tooltip("Empêche le spam Start/Stop si la touche rebondit sur certains claviers")]
        [SerializeField] private float minHoldMs = 70f;

        [Header("Events")]
        [Tooltip("S'active quand une transcription est terminée. Renvoie le texte.")]
        public UnityEvent<string> OnTranscriptionResult;

        private float _pressTime;
        private bool _isRecordingByKey;

        private void Awake()
        {
            // Abonnement à l'événement de fin d'enregistrement du package Whisper
            microphoneRecord.OnRecordStop += OnRecordStop;
        }

        private void Start()
        {
            // Configuration initiale forcée
            UpdateWhisperSettings();

            // Désactive le VAD du script de base pour éviter les conflits avec le PTT
            microphoneRecord.vadStop = false;
        }

        private void Update()
        {
            if (!enablePushToTalk) return;

            // 1. Appui sur la touche
            if (Input.GetKeyDown(pushToTalkKey))
            {
                if (!microphoneRecord.IsRecording)
                {
                    _pressTime = Time.time;
                    _isRecordingByKey = true;

                    // On met à jour les settings juste avant d'enregistrer au cas où ils ont changé dans l'inspecteur
                    UpdateWhisperSettings();
                    microphoneRecord.StartRecord();

                    if (printResultInConsole) Debug.Log("Recording started...");
                }
            }

            // 2. Relâchement de la touche
            if (Input.GetKeyUp(pushToTalkKey) && _isRecordingByKey)
            {
                float durationMs = (Time.time - _pressTime) * 1000f;

                // Vérification du temps minimum pour éviter les micro-clics (rebonds)
                if (durationMs >= minHoldMs)
                {
                    microphoneRecord.StopRecord();
                    _isRecordingByKey = false;
                    if (printResultInConsole) Debug.Log($"Recording stopped. Duration: {durationMs:F0}ms");
                }
                else
                {
                    // Si c'était trop court, on force l'arrêt mais on peut ignorer le résultat si on veut
                    // Ici on arrête simplement proprement.
                    Debug.LogWarning($"Input ignored (Too short: {durationMs:F0}ms < {minHoldMs}ms)");
                    microphoneRecord.StopRecord();
                    _isRecordingByKey = false;
                }
            }
        }

        // Convertit l'Enum en string compréhensible par Whisper
        private void UpdateWhisperSettings()
        {
            whisper.translateToEnglish = translateToEnglish;
            whisper.language = LanguageOptionToCode(language);
        }

        private string LanguageOptionToCode(LanguageOption option)
        {
            return option switch
            {
                LanguageOption.Auto => "auto",
                LanguageOption.English => "en",
                LanguageOption.French => "fr",
                LanguageOption.Spanish => "es",
                LanguageOption.German => "de",
                LanguageOption.Italian => "it",
                LanguageOption.Japanese => "ja",
                LanguageOption.Chinese => "zh",
                _ => "auto"
            };
        }

        private async void OnRecordStop(AudioChunk recordedAudio)
        {
            // Protection : si l'audio est vide ou trop court
            if (recordedAudio.Data == null || recordedAudio.Data.Length == 0)
                return;

            var stopwatch = new Stopwatch();
            stopwatch.Start();

            var res = await whisper.GetTextAsync(recordedAudio.Data, recordedAudio.Frequency, recordedAudio.Channels);

            stopwatch.Stop();

            if (res == null)
                return;

            var text = res.Result;

            // Affichage Console (Debug)
            if (printResultInConsole)
            {
                var rate = recordedAudio.Length / (stopwatch.ElapsedMilliseconds * 0.001f);
                Debug.Log($"[Whisper] '{text}' \n(Language: {res.Language}, Rate: {rate:F1}x)");
            }

            // Envoi du résultat aux autres scripts via l'Event
            OnTranscriptionResult?.Invoke(text);
        }
    }
}