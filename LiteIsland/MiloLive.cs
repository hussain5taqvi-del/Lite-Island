using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LiteIsland;

/// <summary>
/// Gemini Live API over a plain WebSocket (the user's own API key, so the usage cost is theirs).
/// Audio in: 16 kHz PCM16 mono. Audio out: 24 kHz PCM16 mono. Tools are called by the model and answered here.
/// </summary>
internal sealed class MiloLive
{
    private const string Endpoint = "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key=";

    private ClientWebSocket? _ws;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sendLock = new();
    private readonly Queue<string> _audioQ = new();
    private readonly SemaphoreSlim _audioSig = new(0);
    private volatile bool _closing, _ready;
    private int _dropped;

    private readonly string _apiKey, _model, _voice, _prompt;
    private readonly int _endSilenceMs;
    private readonly bool _sensitive;

    public bool EverReady { get; private set; }

    public Action? OnReady;
    public Action<byte[]>? OnAudio;
    public Action? OnInterrupted;
    public Action<string>? OnHeard;
    public Action<string>? OnSaid;
    public Action? OnTurnComplete;
    public Action<string>? OnClosed;
    public Func<string, JsonObject?, string>? ToolRunner;
    public Action<string>? OnTool;

    public MiloLive(string apiKey, string model, string voice, string prompt, int endSilenceMs, bool sensitive)
    {
        _apiKey = apiKey; _model = model; _voice = voice; _prompt = prompt; _endSilenceMs = endSilenceMs; _sensitive = sensitive;
    }

    public void Start()
    {
        new Thread(AudioSender) { IsBackground = true, Name = "MiloSend" }.Start();
        Task.Run(RunAsync);
    }

    public void Close()
    {
        _closing = true;
        try { _audioSig.Release(); } catch { }
        try { _cts.Cancel(); } catch { }
        try { _ws?.Abort(); } catch { }
    }

    private async Task RunAsync()
    {
        string? err = null;
        try
        {
            _ws = new ClientWebSocket();
            await _ws.ConnectAsync(new Uri(Endpoint + Uri.EscapeDataString(_apiKey)), _cts.Token).ConfigureAwait(false);
            MiloLog.W("connected, sending setup (model=" + _model + ", voice=" + _voice + ")");
            SendJson(BuildSetup());
            err = await ReceiveLoop().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            err = ex.Message;
        }
        MiloLog.W("session ended: " + (err ?? "(normal)"));
        if (!_closing) OnClosed?.Invoke(err ?? "Session ended");
    }

    private JsonObject BuildSetup()
    {
        var setup = new JsonObject
        {
            ["model"] = "models/" + _model,
            ["generationConfig"] = new JsonObject
            {
                ["responseModalities"] = new JsonArray("AUDIO"),
                ["temperature"] = 0.5, // steadier: fewer wrong tool picks and wrong words
                ["speechConfig"] = new JsonObject { ["voiceConfig"] = new JsonObject { ["prebuiltVoiceConfig"] = new JsonObject { ["voiceName"] = _voice } } }
            },
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = _prompt }) },
            ["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = MiloTools.Declarations(!_model.Contains("3.1")) }),
            // Turn taking: do not jump in while the user is still talking
            ["realtimeInputConfig"] = new JsonObject
            {
                ["automaticActivityDetection"] = new JsonObject
                {
                    ["startOfSpeechSensitivity"] = _sensitive ? "START_SENSITIVITY_HIGH" : "START_SENSITIVITY_LOW",
                    ["endOfSpeechSensitivity"] = "END_SENSITIVITY_HIGH",
                    ["prefixPaddingMs"] = 200,
                    ["silenceDurationMs"] = _endSilenceMs
                }
            },
            ["inputAudioTranscription"] = new JsonObject(),
            ["outputAudioTranscription"] = new JsonObject()
        };
        return new JsonObject { ["setup"] = setup };
    }

    private void SendJson(JsonNode node) => SendRaw(node.ToJsonString());

    private void SendRaw(string json)
    {
        ClientWebSocket? w = _ws;
        if (w == null || w.State != WebSocketState.Open) return;
        byte[] b = Encoding.UTF8.GetBytes(json);
        lock (_sendLock)
        {
            try { w.SendAsync(new ArraySegment<byte>(b), WebSocketMessageType.Text, true, _cts.Token).Wait(); }
            catch (Exception ex) { MiloLog.W("send: " + ex.Message); }
        }
    }

    // The microphone never waits for the network: audio goes through a queue and its own thread
    private void AudioSender()
    {
        while (!_closing)
        {
            string? j = null;
            lock (_audioQ) { if (_audioQ.Count > 0) j = _audioQ.Dequeue(); }
            if (j == null) { _audioSig.Wait(200); continue; }
            SendRaw(j);
        }
    }

    public void SendAudio(byte[] pcm16k)
    {
        if (!_ready) return;
        string j = new JsonObject
        {
            ["realtimeInput"] = new JsonObject
            {
                ["audio"] = new JsonObject { ["data"] = Convert.ToBase64String(pcm16k), ["mimeType"] = "audio/pcm;rate=16000" }
            }
        }.ToJsonString();
        lock (_audioQ)
        {
            _audioQ.Enqueue(j);
            while (_audioQ.Count > 75) { _audioQ.Dequeue(); _dropped++; } // never lag more than about 3 s behind
        }
        try { _audioSig.Release(); } catch { }
        if (_dropped > 0 && _dropped % 25 == 1) MiloLog.W("network too slow: dropped " + _dropped + " old mic chunks");
    }

    public void SendText(string text)
    {
        if (!_ready) return;
        SendJson(new JsonObject { ["realtimeInput"] = new JsonObject { ["text"] = text } });
    }

    private async Task<string?> ReceiveLoop()
    {
        var buf = new byte[65536];
        while (_ws != null && _ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) return "Closed by server: " + r.CloseStatusDescription;
                ms.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);

            try { Handle(Encoding.UTF8.GetString(ms.ToArray())); }
            catch (Exception ex) { MiloLog.W("handle: " + ex.Message); }
        }
        return null;
    }

    private static string? Str(JsonNode? n) { try { return n?.GetValue<string>(); } catch { return null; } }
    private static bool IsTrue(JsonNode? n) { try { return n != null && n.GetValue<bool>(); } catch { return false; } }

    private void Handle(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject m) return;

        if (m.ContainsKey("setupComplete"))
        {
            MiloLog.W("setupComplete: session is ready");
            _ready = true;
            EverReady = true;
            OnReady?.Invoke();
            return;
        }

        if (m["serverContent"] is JsonObject sc)
        {
            if (IsTrue(sc["interrupted"])) { MiloLog.W("server: interrupted"); OnInterrupted?.Invoke(); }
            if (IsTrue(sc["turnComplete"])) { MiloLog.W("server: turnComplete"); OnTurnComplete?.Invoke(); }

            string? heard = Str(sc["inputTranscription"]?["text"]);
            if (!string.IsNullOrEmpty(heard)) { MiloLog.W("heard: " + heard); OnHeard?.Invoke(heard); }

            string? said = Str(sc["outputTranscription"]?["text"]);
            if (!string.IsNullOrEmpty(said)) { MiloLog.W("said: " + said); OnSaid?.Invoke(said); }

            if (sc["modelTurn"]?["parts"] is JsonArray parts)
            {
                foreach (JsonNode? part in parts)
                {
                    string? data = Str(part?["inlineData"]?["data"]);
                    if (data != null) OnAudio?.Invoke(Convert.FromBase64String(data));
                }
            }
        }

        if (m["toolCall"] is JsonObject tc) Task.Run(() => RunTools(tc)); // never block the receive loop
        if (m.ContainsKey("goAway")) MiloLog.W("server sent goAway (session will end soon)");
    }

    private void RunTools(JsonObject tc)
    {
        var replies = new JsonArray();
        if (tc["functionCalls"] is JsonArray calls)
        {
            foreach (JsonNode? fc in calls)
            {
                string id = Str(fc?["id"]) ?? "";
                string name = Str(fc?["name"]) ?? "";
                var args = fc?["args"] as JsonObject;
                MiloLog.W("tool call: " + name + " " + (args?.ToJsonString() ?? "{}"));
                string? label = MiloTools.Label(name, args);
                if (label != null) OnTool?.Invoke(label);

                string result;
                try { result = ToolRunner?.Invoke(name, args) ?? "No tool runner."; }
                catch (Exception ex) { result = "Action failed: " + ex.Message; MiloLog.W("tool " + name + " crashed: " + ex); }
                MiloLog.W("tool " + name + " -> " + (result.Length > 300 ? result.Substring(0, 300) + "..." : result));

                replies.Add(new JsonObject
                {
                    ["id"] = id,
                    ["name"] = name,
                    ["response"] = new JsonObject { ["result"] = result }
                });
            }
        }
        if (replies.Count > 0)
            SendJson(new JsonObject { ["toolResponse"] = new JsonObject { ["functionResponses"] = replies } });
    }
}
