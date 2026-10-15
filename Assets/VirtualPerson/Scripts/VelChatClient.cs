using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Concentus.Structs;
using Newtonsoft.Json;
using SocketIOClient;
using SocketIOClient.Newtonsoft.Json;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using VelNet.Voice;

/// <summary>
/// Talk to a virtual person through VelChat (the lab's AI service), over Socket.IO:
///   1. hold A or X (or Space in the editor) and talk: VelVoice encodes the microphone as Opus packets. Or turn on
///      hands-free: VelVoice's voice gate passes speech only, and a pause of 0.8 s ends the turn;
///   2. then one "chat_message" with the packets. VelChat transcribes them (Whisper), answers (Gemma, streamed,
///      with this conversation's history) and speaks the answer one sentence at a time (Kokoro, Opus at 24 kHz);
///   3. when the text is done, a second, silent request asks the model for one gesture per sentence (JSON);
///   4. each sentence's audio plays from the person's mouth, and its gesture starts with it.
/// The panel shows what was heard, the answer, and how long each step took.
/// </summary>
public class VelChatClient : MonoBehaviour
{
    [Tooltip("VelChat's address. Empty: read from Resources/velchat_host.txt (kept out of git; ask the instructor)")]
    public string host = "";
    [TextArea(4, 12)]
    public string persona =
        "You are Sam, a friendly virtual person standing in the University of Georgia's virtual reality lab. " +
        "You are talking out loud with a student wearing a Meta Quest 3. Answer in one or two short spoken sentences. " +
        "No lists, no markdown, no emoji. If you don't know something, say so.";
    public string voiceName = "am_fenrir";      // VelChat has af_heart and am_fenrir; this model is a man

    public VelVoice mic;
    public AudioSource voice;
    public AgentGestures gestures;
    public Text status, transcript, timing;
    [Tooltip("Listen without a button: speech passes VelVoice's voice gate, and a pause ends the turn")]
    public bool handsFree;
    public float pauseToSend = 0.8f;

    SocketIOUnity socket;
    OpusDecoder decoder;
    readonly float[] pcm = new float[480];
    readonly List<string> packets = new List<string>();
    readonly List<AudioClip> clips = new List<AudioClip>();
    AudioClip filling;
    string contextId;
    bool recording, waiting, wantGestures, micStarted;
    // VelVoice may hand frames over from its own thread: guard the packet list and use thread-safe time
    readonly object packetLock = new object();
    volatile bool gateOpen;                 // hands-free: listening right now (not while he talks)
    long lastPacketTicks;
    bool handsFreeTurn;
    float lastSpoke = -10f;
    string heard = "", answer = "";
    List<string> sentences = new List<string>();
    List<string> sentenceGestures = new List<string>();
    int sentencePlaying = -1;
    float tRelease, tHeard, tFirstWords, tTextDone, tFirstAudio, tSpeaking;

    InputAction talk;

    class AudioFrame { public byte[] frame; public int frame_number; public int total_frames; public string context_id; }
    class Thinking { public string prompt; public string context_id; }
    class Partial { public string text; public string context_id; }
    class Done { public string prompt; public string final_text; public string context_id; }

    void Start()
    {
        decoder = new OpusDecoder(24000, 1);
        contextId = Guid.NewGuid().ToString();
        talk = new InputAction("Talk", InputActionType.Button);
        talk.AddBinding("<XRController>/primaryButton");      // A on the right controller, X on the left
        talk.AddBinding("<Keyboard>/space");
        talk.Enable();
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
#endif
        mic.encodedFrameAvailable += frame =>
        {
            if (!recording && !gateOpen) return;
            var copy = new byte[frame.count];
            Array.Copy(frame.array, copy, frame.count);
            lock (packetLock) { packets.Add(Convert.ToBase64String(copy)); lastPacketTicks = DateTime.UtcNow.Ticks; }
        };
        if (string.IsNullOrWhiteSpace(host)) { var t = Resources.Load<TextAsset>("velchat_host"); host = t ? t.text.Trim() : ""; }
        if (string.IsNullOrWhiteSpace(host)) { Show("No VelChat address: put it in Assets/VirtualPerson/Resources/velchat_host.txt"); return; }
        Connect();
        Show("Connecting to VelChat...");
    }

    void Connect()
    {
        socket = new SocketIOUnity(host, new SocketIOOptions
        {
            Query = new Dictionary<string, string> { { "token", "QUEST" } },
            EIO = EngineIO.V4,
            Transport = SocketIOClient.Transport.TransportProtocol.WebSocket,
        });
        socket.JsonSerializer = new NewtonsoftJsonSerializer();
        socket.OnConnected += (s, e) => UnityThread.executeInUpdate(() => { Debug.Log("[VirtualPerson] connected to " + host); Show(Prompt); });
        socket.OnDisconnected += (s, e) => UnityThread.executeInUpdate(() => { Debug.Log("[VirtualPerson] disconnected: " + e); Show("Disconnected from VelChat: " + e); });
        socket.OnError += (s, e) => UnityThread.executeInUpdate(() => { Debug.Log("[VirtualPerson] socket error: " + e); Show("VelChat error: " + e); });

        // ai_thinking: the transcript is ready (conversation turn) or the gesture request started
        socket.OnUnityThread("ai_thinking", r =>
        {
            var m = r.GetValue<Thinking>();
            if (string.IsNullOrEmpty(m.context_id)) return;              // the gesture request has no context id
            heard = m.prompt; tHeard = Time.realtimeSinceStartup;
            Debug.Log("[VirtualPerson] heard: " + heard);
            gestures.Think();
            Refresh();
        });
        socket.OnUnityThread("ai_partial_response", r =>
        {
            var m = r.GetValue<Partial>();
            if (string.IsNullOrEmpty(m.context_id)) return;
            if (tFirstWords == 0f) tFirstWords = Time.realtimeSinceStartup;
            answer += m.text;
            Refresh();
        });
        socket.OnUnityThread("ai_full_response_done", r =>
        {
            var m = r.GetValue<Done>();
            if (string.IsNullOrEmpty(m.context_id)) { if (wantGestures) OnGestures(m.final_text); return; }
            tTextDone = Time.realtimeSinceStartup;
            answer = m.final_text;
            Debug.Log("[VirtualPerson] answer: " + answer);
            sentences = SplitSentences(answer);
            sentenceGestures = new List<string>();
            foreach (var s in sentences) sentenceGestures.Add(AgentGestures.FromWords(s));   // until the model's choice arrives
            AskForGestures(sentences);
            waiting = false;
            Refresh();
        });
        // audio_frame: Opus, 24 kHz, 480-sample frames; frame 0 starts a new sentence
        socket.OnUnityThread("audio_frame", r =>
        {
            var m = r.GetValue<AudioFrame>();
            if (m.frame_number == 0)
            {
                if (tFirstAudio == 0f) tFirstAudio = Time.realtimeSinceStartup;
                filling = AudioClip.Create("sentence", m.total_frames * 480, 1, 24000, false);
                clips.Add(filling);
            }
            int n = decoder.Decode(m.frame, 0, m.frame.Length, pcm, 0, 480);
            if (n > 0 && filling != null) filling.SetData(pcm, m.frame_number * 480);
        });
        socket.OnUnityThread("error_response", r => { Debug.Log("[VirtualPerson] error_response: " + r); Show("VelChat: " + r.ToString()); });
        socket.Connect();
    }

    // A second, one-shot request in command mode: no history, no speech. Its events carry no context id.
    void AskForGestures(List<string> said)
    {
        wantGestures = true;
        var numbered = new System.Text.StringBuilder();
        for (int i = 0; i < said.Count; i++) numbered.Append(i + 1).Append(". ").Append(said[i]).Append('\n');
        socket.Emit("chat_message", new Dictionary<string, object>
        {
            { "prompt", numbered.ToString() },
            { "command_mode", true },
            { "command_system_prompt",
              "You choose hand and head gestures for a virtual person. The user message is what the person is about to say, " +
              "as numbered sentences. Reply with ONLY a JSON array of strings, exactly one per numbered sentence, " +
              "each one of: wave, shrug, think, nod, shake, explain, excited, laugh, beat. " +
              "wave = greeting or goodbye; shrug = unsure; think = considering; nod = agreeing; shake = disagreeing; " +
              "explain = presenting or describing something; excited = enthusiasm; laugh = something funny; beat = anything else. " +
              "Use beat for most sentences; at most one wave." },
            { "voice_mode", "" },
        });
    }

    void OnGestures(string json)
    {
        wantGestures = false;
        try
        {
            var m = Regex.Match(json, @"\[[^\]]*\]");
            var list = JsonConvert.DeserializeObject<List<string>>(m.Success ? m.Value : json);
            for (int i = 0; i < list.Count && i < sentenceGestures.Count; i++) sentenceGestures[i] = list[i];
            Debug.Log("[VirtualPerson] gestures: " + string.Join(", ", list));
            // the sentence already playing switches to the model's choice
            if (sentencePlaying >= 0 && sentencePlaying < sentenceGestures.Count) gestures.Play(sentenceGestures[sentencePlaying], voice.clip ? voice.clip.length : 2f);
        }
        catch (Exception e) { Debug.Log("[VirtualPerson] gesture JSON not understood: " + json + " (" + e.Message + ")"); }
    }

    static List<string> SplitSentences(string text)
    {
        var list = new List<string>();
        foreach (var s in Regex.Split(text.Trim(), @"(?<=[.!?])\s+")) if (s.Trim().Length > 0) list.Add(s.Trim());
        if (list.Count == 0) list.Add(text);
        return list;
    }

    void Update()
    {
        // push to talk
        bool down = talk.IsPressed();
        if (down && !recording && !waiting && socket != null && socket.Connected) BeginTalking();
        else if (!down && recording) EndTalking();

        // hands-free: open the gate while nobody is talking to him, send after a pause
        if (voice.isPlaying) lastSpoke = Time.time;
        bool canListen = handsFree && !recording && !waiting && !voice.isPlaying && clips.Count == 0 && Time.time - lastSpoke > 0.5f
                         && socket != null && socket.Connected;
        if (canListen && !micStarted) { mic.StartMicrophone(); micStarted = true; }
        if (canListen && !gateOpen) { lock (packetLock) packets.Clear(); }
        gateOpen = canListen;
        if (canListen)
        {
            int n; long last;
            lock (packetLock) { n = packets.Count; last = lastPacketTicks; }
            if (n > 0 && !handsFreeTurn) { handsFreeTurn = true; gestures.Listen(); Show("Listening... pause to send."); }
            if (handsFreeTurn && (DateTime.UtcNow.Ticks - last) / 1e7 > pauseToSend)
            {
                handsFreeTurn = false;
                if (n >= 12) Send(); else { lock (packetLock) packets.Clear(); gestures.Idle(); Show(Prompt); }
            }
        }

        // play the sentences in order; each one starts its gesture
        if (!voice.isPlaying && clips.Count > 0)
        {
            voice.clip = clips[0]; clips.RemoveAt(0);
            voice.Play();
            sentencePlaying++;
            if (tSpeaking == 0f) { tSpeaking = Time.realtimeSinceStartup; Refresh(); }
            string g = sentencePlaying < sentenceGestures.Count ? sentenceGestures[sentencePlaying] : "beat";
            gestures.Play(g, voice.clip.length);
        }
        else if (!voice.isPlaying && clips.Count == 0 && sentencePlaying >= 0 && !waiting)
        {
            sentencePlaying = -1;
            gestures.Idle();
        }
    }

    void BeginTalking()
    {
        if (!mic.enabled) mic.enabled = true;
        if (!micStarted) { mic.StartMicrophone(); micStarted = true; }
        voice.Stop(); clips.Clear(); sentencePlaying = -1;
        lock (packetLock) packets.Clear();
        mic.bypassSilenceGate = true;
        recording = true;
        gestures.Listen();
        Debug.Log("[VirtualPerson] listening");
        Show("Listening... release to send.");
    }
    void EndTalking()
    {
        recording = false;
        mic.bypassSilenceGate = false;
        int n; lock (packetLock) n = packets.Count;
        if (n < 12) { Debug.Log($"[VirtualPerson] too short: {n} packets"); Show("Too short. Hold the button while you talk."); gestures.Idle(); return; }
        Send();
    }

    public void SetHandsFree(bool on) { handsFree = on; Show(Prompt); }
    string Prompt => handsFree ? "Hands-free: just talk, then pause." : "Hold A or X and talk.";

    void Send()
    {
        List<string> toSend; lock (packetLock) { toSend = new List<string>(packets); packets.Clear(); }
        gateOpen = false;
        waiting = true;
        heard = "..."; answer = "";
        tRelease = Time.realtimeSinceStartup; tHeard = tFirstWords = tTextDone = tFirstAudio = tSpeaking = 0f;
        socket.Emit("chat_message", new Dictionary<string, object>
        {
            { "prompt", "" },
            { "opusAudioPackets", toSend },
            { "context_id", contextId },
            { "agent_system_prompt", persona },
            { "force_response_type", "text" },
            { "voice_mode", voiceName },
        });
        gestures.Think();
        Debug.Log($"[VirtualPerson] sent {toSend.Count} audio packets");
        Show("Thinking...");
    }

    void Show(string s) { if (status) status.text = s; }

    void Refresh()
    {
        if (transcript) transcript.text = $"<b>You:</b> {heard}\n<b>Sam:</b> {answer}";
        if (timing && tRelease > 0f)
        {
            string T(float t) => t > 0f ? $"{t - tRelease:F2} s" : "...";
            timing.text = $"after you let go:  heard {T(tHeard)}   first words {T(tFirstWords)}   all text {T(tTextDone)}   first audio {T(tFirstAudio)}   speaking {T(tSpeaking)}";
            if (tSpeaking > 0f) Debug.Log("[VirtualPerson] timing: " + timing.text);
        }
        if (!waiting && tSpeaking > 0f) Show(Prompt);
    }

    void OnDestroy() { socket?.Disconnect(); socket?.Dispose(); talk?.Disable(); }
}
