using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

public class SpeechRecognitionController : MonoBehaviour
{
    [Header("Whisper (référence à ton composant RunWhisper)")]
    [SerializeField] private RunWhisper whisper;

    [Header("Micro")]
    [Tooltip("Laisse vide pour prendre le premier micro disponible")]
    [SerializeField] private string microphoneDevice = null;
    [SerializeField, Range(5, 30)] private int maxRecordSeconds = 30;
    [SerializeField] private int targetSampleRate = 16000; // mono 16 kHz recommandé
    public bool IsRecording { get; private set; }

    [Header("Push-To-Talk")]
    [SerializeField] private bool enablePushToTalk = true;
    [SerializeField] private KeyCode pushToTalkKey = KeyCode.V;
    [Tooltip("Empêche le spam Start/Stop si la touche rebondit sur certains claviers")]
    [SerializeField] private float minHoldMs = 70f;

    private AudioClip _recordingClip;
    private string _resolvedDevice;
    private float _holdStartedTime = -1f;

    // Évènement déclenché quand la transcription est prête
    public event Action<string> OnTranscriptionReady;

    void Awake()
    {
        _resolvedDevice = !string.IsNullOrEmpty(microphoneDevice)
            ? microphoneDevice
            : Microphone.devices.FirstOrDefault();

        if (string.IsNullOrEmpty(_resolvedDevice))
            Debug.LogWarning("[SpeechRecognition] Aucun micro détecté.");
    }

    void Update()
    {
        if (!enablePushToTalk || string.IsNullOrEmpty(_resolvedDevice)) return;

        // Appui
        if (Input.GetKeyDown(pushToTalkKey) && !IsRecording)
        {
            _holdStartedTime = Time.unscaledTime * 1000f;
            StartRecording();
        }

        // Relâchement
        if (Input.GetKeyUp(pushToTalkKey) && IsRecording)
        {
            // Petitte sécurité anti-clic involontaire très court
            float heldMs = (Time.unscaledTime * 1000f) - _holdStartedTime;
            if (heldMs < minHoldMs)
            {
                // Trop court : on annule proprement (rien n'est envoyé)
                CancelRecordingSilently();
            }
            else
            {
                StopRecording();
            }
            _holdStartedTime = -1f;
        }
    }

    /// <summary>Démarre l’enregistrement (tu peux aussi l’appeler depuis un bouton UI).</summary>
    public void StartRecording()
    {
        if (IsRecording) return;
        if (string.IsNullOrEmpty(_resolvedDevice))
        {
            Debug.LogError("[SpeechRecognition] Pas de micro disponible.");
            return;
        }

        _recordingClip = Microphone.Start(_resolvedDevice, false, maxRecordSeconds, targetSampleRate);
        if (_recordingClip == null)
        {
            Debug.LogError("[SpeechRecognition] Échec Microphone.Start.");
            return;
        }

        IsRecording = true;
        Debug.Log("[SpeechRecognition] Recording (PTT on)…");
    }

    /// <summary>
    /// Stoppe et envoie la prise au moteur Whisper via SendRecording().
    /// (Binder sur un bouton UI si tu veux un mode non-PTT.)
    /// </summary>
    public async void StopRecording()
    {
        if (!IsRecording) return;
        if (_recordingClip == null) { IsRecording = false; return; }

        int position = Microphone.GetPosition(_resolvedDevice);
        Microphone.End(_resolvedDevice);
        IsRecording = false;

        if (position <= 0)
        {
            Debug.LogWarning("[SpeechRecognition] Enregistrement trop court.");
            return;
        }

        var prepared = PrepareClipForWhisper(_recordingClip, position, targetSampleRate);
        await SendRecording(prepared);
    }

    /// <summary>
    /// Annule une prise trop courte (PTT tap trop bref) sans lancer la transcription.
    /// </summary>
    private void CancelRecordingSilently()
    {
        if (!IsRecording) return;
        Microphone.End(_resolvedDevice);
        IsRecording = false;
        _recordingClip = null;
        Debug.Log("[SpeechRecognition] PTT: appui trop court, prise annulée.");
    }

    /// <summary>Envoie l'audio au modèle Whisper et relaye le texte.</summary>
    public async Task SendRecording(AudioClip clip)
    {
        if (whisper == null)
        {
            Debug.LogError("[SpeechRecognition] Référence RunWhisper manquante.");
            return;
        }

        try
        {
            string text = await whisper.TranscribeClipAsync(clip, SystemLanguage.French);
            Debug.Log("[SpeechRecognition] Transcription: " + text);
            OnTranscriptionReady?.Invoke(text);
        }
        catch (Exception e)
        {
            Debug.LogError("[SpeechRecognition] Erreur transcription: " + e);
        }
    }

    // ---------- Helpers audio ----------

    private static AudioClip PrepareClipForWhisper(AudioClip source, int usedSamples, int targetHz)
    {
        int channels = source.channels;
        int sourceHz = source.frequency;
        usedSamples = Mathf.Min(usedSamples, source.samples);

        float[] buffer = new float[usedSamples * channels];
        source.GetData(buffer, 0);

        float[] mono = (channels == 1) ? buffer : DownmixToMono(buffer, channels);
        float[] mono16k = (sourceHz == targetHz) ? mono : ResampleLinear(mono, sourceHz, targetHz);

        int maxSamples = targetHz * 30; // 30 s
        if (mono16k.Length > maxSamples) Array.Resize(ref mono16k, maxSamples);

        var clip = AudioClip.Create("Recording_16k_mono", mono16k.Length, 1, targetHz, false);
        clip.SetData(mono16k, 0);
        return clip;
    }

    private static float[] DownmixToMono(float[] interleaved, int channels)
    {
        int frames = interleaved.Length / channels;
        float[] mono = new float[frames];
        int idx = 0;
        for (int i = 0; i < frames; i++)
        {
            float sum = 0f;
            for (int c = 0; c < channels; c++) sum += interleaved[idx++];
            mono[i] = sum / channels;
        }
        return mono;
    }

    private static float[] ResampleLinear(float[] input, int srcHz, int dstHz)
    {
        if (srcHz == dstHz) return input;
        double ratio = (double)dstHz / srcHz;
        int outLen = (int)Math.Round(input.Length * ratio);
        float[] output = new float[outLen];

        for (int i = 0; i < outLen; i++)
        {
            double t = i / ratio;
            int t0 = (int)Math.Floor(t);
            int t1 = Math.Min(t0 + 1, input.Length - 1);
            double frac = t - t0;
            output[i] = (float)((1.0 - frac) * input[t0] + frac * input[t1]);
        }
        return output;
    }
}
