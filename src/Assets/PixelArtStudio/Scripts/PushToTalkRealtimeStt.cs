using UnityEngine;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;

public class PushToTalkRealtimeStt : MonoBehaviour
{
    [Header("Server (Speaches/OpenAI Realtime-compatible)")]
    public string serverUrl =
        "ws://localhost:8000/v1/realtime?model=Systran/faster-whisper-medium";

    [Header("Push-To-Talk")]
    public KeyCode pushToTalkKey = KeyCode.V;

    [Header("Audio")]
    public int inputSampleRate = 48000;      // Micro Unity (48k)
    const int targetRate = 24000;            // Realtime API (24k mono PCM16)
    const int chunkMs = 40;                  // ~40ms
    const int channelsOut = 1;               // mono
    int samplesPerChunk => targetRate * chunkMs / 1000; // 960

    [Range(0f, 0.02f)] public float silenceRms = 0.003f; // (optionnel)

    // --- internes ---
    ClientWebSocket ws;
    CancellationTokenSource cts;
    Task sendTask, recvTask;
    readonly ConcurrentQueue<string> outQueue = new ConcurrentQueue<string>();
    readonly SemaphoreSlim outSignal = new SemaphoreSlim(0);

    AudioClip micClip;
    string micDevice;
    bool pttActive = false;

    List<short> resampleRing = new List<short>(8192);

    async void Start()
    {
        await EnsureConnectedAsync(); // connecte le WS et démarre les boucles

        // Micro
        micDevice = Microphone.devices.Length > 0 ? Microphone.devices[0] : null;
        if (micDevice == null) { Debug.LogError("Aucun micro détecté"); return; }

        micClip = Microphone.Start(micDevice, true, 1, inputSampleRate);
        var src = gameObject.AddComponent<AudioSource>();
        src.hideFlags = HideFlags.HideInInspector;
        src.clip = micClip;
        src.loop = true;
        while (Microphone.GetPosition(micDevice) <= 0) { } // attend le buffer
        src.Play();
    }

    void Update()
    {
        if (Input.GetKeyDown(pushToTalkKey))
        {
            _ = EnsureConnectedAsync(); // si le WS a été fermé, on le rouvre
            pttActive = true;
            Debug.Log("PTT ON");
        }
        if (Input.GetKeyUp(pushToTalkKey))
        {
            pttActive = false;
            Debug.Log("PTT OFF (commit)");
            EnqueueJson("{\"type\":\"input_audio_buffer.commit\"}");
        }
    }

    void OnDestroy()
    {
        try { cts?.Cancel(); } catch { }
        try { ws?.Abort(); ws?.Dispose(); } catch { }
        if (micDevice != null) Microphone.End(micDevice);
    }

    async Task EnsureConnectedAsync()
    {
        if (ws != null && ws.State == WebSocketState.Open) return;

        // stop anciennes boucles
        try { cts?.Cancel(); } catch { }
        cts = new CancellationTokenSource();

        ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            await ws.ConnectAsync(new Uri(serverUrl), cts.Token);
            Debug.Log("WS connected");
        }
        catch (Exception e)
        {
            Debug.LogError("WS connect failed: " + e.Message);
            return;
        }

        // démarre les boucles une seule fois par connexion
        sendTask = Task.Run(() => SendLoop(), cts.Token);
        recvTask = Task.Run(() => ReceiveLoop(), cts.Token);
    }

    // ==== AUDIO ====
    void OnAudioFilterRead(float[] data, int chIn)
    {
        // Pas de WS ou PTT OFF -> on ignore
        if (ws == null || ws.State != WebSocketState.Open || !pttActive) return;

        // (facultatif) VAD léger
        // float rms = 0f; for (int i = 0; i < data.Length; i += chIn) rms += data[i]*data[i];
        // rms = Mathf.Sqrt(rms / (data.Length / chIn));

        // 1) downsample → 24k mono + float->int16
        DownsampleToPcm16(data, chIn, inputSampleRate, targetRate, resampleRing);

        // 2) envoi par tranches ~40ms (via queue -> envoi séquentiel)
        while (resampleRing.Count >= samplesPerChunk)
        {
            var chunk = new short[samplesPerChunk];
            resampleRing.CopyTo(0, chunk, 0, samplesPerChunk);
            resampleRing.RemoveRange(0, samplesPerChunk);

            byte[] bytes = new byte[chunk.Length * sizeof(short)];
            Buffer.BlockCopy(chunk, 0, bytes, 0, bytes.Length);
            string b64 = Convert.ToBase64String(bytes);

            EnqueueJson("{\"type\":\"input_audio_buffer.append\",\"audio\":\"" + b64 + "\"}");
        }
    }

    static void DownsampleToPcm16(float[] src, int chIn, int inRate, int outRate, List<short> dst)
    {
        float ratio = (float)outRate / inRate;
        int framesIn = src.Length / chIn;
        int framesOut = Mathf.FloorToInt(framesIn * ratio);

        for (int i = 0; i < framesOut; i++)
        {
            float pos = i / ratio;
            int i0 = (int)pos;
            int i1 = Mathf.Min(i0 + 1, framesIn - 1);
            float t = pos - i0;

            float s0 = src[i0 * chIn]; // canal 0 (mono)
            float s1 = src[i1 * chIn];
            float s = Mathf.Lerp(s0, s1, t);

            int val = Mathf.Clamp((int)(s * 32767f), -32768, 32767);
            dst.Add((short)val);
        }
    }

    // ==== ENVOI SEQUENTIEL ====
    void EnqueueJson(string json)
    {
        if (ws == null || ws.State != WebSocketState.Open) return;
        outQueue.Enqueue(json);
        outSignal.Release();
    }

    async Task SendLoop()
    {
        try
        {
            while (!cts.IsCancellationRequested && ws != null && ws.State == WebSocketState.Open)
            {
                await outSignal.WaitAsync(cts.Token); // attend qu'il y ait quelque chose à envoyer

                while (outQueue.TryDequeue(out var json))
                {
                    var seg = new ArraySegment<byte>(Encoding.UTF8.GetBytes(json));
                    try
                    {
                        await ws.SendAsync(seg, WebSocketMessageType.Text, true, cts.Token);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("WS Send failed: " + e.Message);
                        return; // sort -> Update() reconnectera au prochain PTT
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ==== RECEPTION ====
    async Task ReceiveLoop()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!cts.IsCancellationRequested && ws != null && ws.State == WebSocketState.Open)
            {
                var ms = new System.IO.MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    var seg = new ArraySegment<byte>(buffer);
                    result = await ws.ReceiveAsync(seg, cts.Token);
                    if (result.Count > 0) ms.Write(seg.Array, seg.Offset, result.Count);
                }
                while (!result.EndOfMessage && !cts.IsCancellationRequested);

                if (result.MessageType == WebSocketMessageType.Close) break;

                var msg = Encoding.UTF8.GetString(ms.ToArray());
                if (!string.IsNullOrEmpty(msg))
                {
                    // Affiche le type d'event pour debug
                    string type = ExtractField(msg, "\"type\"");
                    string transcript = ExtractField(msg, "\"transcript\"");
                    if (!string.IsNullOrEmpty(transcript))
                        Debug.Log($"[STT transcript] {transcript}");
                    else
                        Debug.Log($"[STT <-] type={type} raw={msg}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Debug.LogWarning("WS Receive failed: " + e.Message);
        }
    }

    // Extraction ultra simple: cherche "field":"value"
    static string ExtractField(string json, string fieldWithQuotes)
    {
        int i = json.IndexOf(fieldWithQuotes, StringComparison.Ordinal);
        if (i < 0) return null;
        i = json.IndexOf(':', i); if (i < 0) return null;
        int start = json.IndexOf('"', i + 1) + 1; if (start <= 0) return null;
        int end = json.IndexOf('"', start); if (end < 0) return null;
        return json.Substring(start, end - start);
    }
}
