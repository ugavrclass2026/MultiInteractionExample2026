using System;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Microphone -> visemes (mouth shapes). On Quest, Meta's Oculus Lipsync library (Plugins/Android/libOVRLipSync.so)
/// turns each chunk of microphone audio into 15 viseme weights, 0..1:
///   sil PP FF TH DD kk CH SS nn RR aa E ih oh ou
/// (silence, then lips together "p/b/m", lip on teeth "f/v", tongue on teeth "th", ... and five vowels).
/// In the editor the library isn't loaded, so loudness stands in: louder = more "aa".
/// Avatars read the visemes, or three summaries of them (Open, Wide, Round), through VisemeMouth.
/// </summary>
[DefaultExecutionOrder(-45)]
public class LipSync : MonoBehaviour
{
    public const int Count = 15;
    public static readonly string[] Names = { "sil", "PP", "FF", "TH", "DD", "kk", "CH", "SS", "nn", "RR", "aa", "E", "ih", "oh", "ou" };

    public bool listen = true;
    [Range(1, 100)] public int smoothing = 50;          // Meta's own smoothing of the viseme weights
    [Tooltip("Loudness stand-in only: RMS that counts as fully open")]
    public float fullOpenRms = 0.08f;

    public readonly float[] visemes = new float[Count];
    public float Loudness { get; private set; }         // RMS of the last chunk
    public float Open { get; private set; }             // jaw open, 0..1
    public float Wide { get; private set; }             // lips spread (E, ih, SS), 0..1
    public float Round { get; private set; }            // lips rounded (oh, ou), 0..1
    public bool UsingMeta { get; private set; }
    public string Status { get; private set; } = "starting";

    // How much each viseme opens the jaw, spreads the lips and rounds them (hand-tuned, same order as Names).
    static readonly float[] openOf  = { 0, 0, .1f, .3f, .3f, .35f, .35f, .15f, .2f, .3f, 1f, .6f, .4f, .8f, .45f };
    static readonly float[] wideOf  = { 0, 0, .2f, .2f, .2f, .2f, .3f, .6f, .2f, 0, .3f, 1f, .8f, 0, 0 };
    static readonly float[] roundOf = { 0, .2f, 0, 0, 0, 0, .3f, 0, 0, .4f, 0, 0, 0, .7f, 1f };

    AudioClip mic;
    string device;
    int readPos, rate;
    readonly float[] chunk = new float[1024];

#if UNITY_ANDROID && !UNITY_EDITOR
    // Meta's C API (the same calls the Oculus Lipsync package's OVRLipSync.cs makes)
    const string Lib = "OVRLipSync";
    [DllImport(Lib)] static extern int ovrLipSyncDll_Initialize(int sampleRate, int bufferSize);
    [DllImport(Lib)] static extern int ovrLipSyncDll_CreateContextEx(ref uint context, int provider, int sampleRate, bool enableAcceleration);
    [DllImport(Lib)] static extern int ovrLipSyncDll_DestroyContext(uint context);
    [DllImport(Lib)] static extern int ovrLipSyncDll_SendSignal(uint context, int signal, int arg1, int arg2);
    [DllImport(Lib)] static extern int ovrLipSyncDll_ProcessFrameEx(uint context, IntPtr audio, uint samples, int dataType,
        ref int frameNumber, ref int frameDelay, float[] visemes, int visemeCount, ref float laughter, float[] laughterCategories, int laughterCount);
    const int Enhanced = 1, F32Mono = 2, VisemeSmoothing = 3;
    uint context;
    int frameNumber, frameDelay; float laughter;
#endif

    void Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
#endif
    }

    bool StartMic()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone)) { Status = "no microphone permission"; return false; }
#endif
        if (Microphone.devices.Length == 0) { Status = "no microphone"; return false; }
        device = Microphone.devices[0];
        Microphone.GetDeviceCaps(device, out int min, out int max);
        rate = max == 0 ? 48000 : Mathf.Clamp(48000, min, max);
        mic = Microphone.Start(device, true, 1, rate);
        readPos = 0;
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            int r = ovrLipSyncDll_Initialize(rate, chunk.Length);
            if (r == 0) r = ovrLipSyncDll_CreateContextEx(ref context, Enhanced, rate, false);
            if (r == 0) { ovrLipSyncDll_SendSignal(context, VisemeSmoothing, smoothing, 0); UsingMeta = true; }
            else Debug.Log("[AvatarDemo] Meta Lipsync failed: " + r);
        }
        catch (Exception e) { Debug.Log("[AvatarDemo] Meta Lipsync not loaded: " + e.Message); }
#endif
        Debug.Log($"[AvatarDemo] lip sync: mic '{device}' at {rate} Hz, {(UsingMeta ? "Meta Lipsync" : "loudness")}");
        return true;
    }

    void Update()
    {
        if (!listen) { Array.Clear(visemes, 0, Count); Summarize(); Status = "off"; return; }
        if (mic == null && !StartMic()) return;
        int pos = Microphone.GetPosition(device);
        int available = (pos - readPos + mic.samples) % mic.samples;
        while (available >= chunk.Length)
        {
            mic.GetData(chunk, readPos);                      // wraps around the looping clip
            readPos = (readPos + chunk.Length) % mic.samples;
            available -= chunk.Length;
            Process();
        }
        Summarize();
        int top = 0; for (int i = 1; i < Count; i++) if (visemes[i] > visemes[top]) top = i;
        Status = $"{(UsingMeta ? "Meta Lipsync" : "loudness only")}, level {Loudness:F2}, top viseme {Names[top]}";
    }

    void Process()
    {
        float sum = 0f; foreach (var s in chunk) sum += s * s;
        Loudness = Mathf.Sqrt(sum / chunk.Length);
#if UNITY_ANDROID && !UNITY_EDITOR
        if (UsingMeta)
        {
            var h = GCHandle.Alloc(chunk, GCHandleType.Pinned);
            try { ovrLipSyncDll_ProcessFrameEx(context, h.AddrOfPinnedObject(), (uint)chunk.Length, F32Mono, ref frameNumber, ref frameDelay, visemes, Count, ref laughter, null, 0); }
            finally { h.Free(); }
            return;
        }
#endif
        float aa = Mathf.Clamp01(Loudness / fullOpenRms);
        Array.Clear(visemes, 0, Count);
        visemes[10] = aa; visemes[0] = 1f - aa;
    }

    void Summarize()
    {
        float o = 0, w = 0, r = 0;
        for (int i = 0; i < Count; i++) { o += visemes[i] * openOf[i]; w += visemes[i] * wideOf[i]; r += visemes[i] * roundOf[i]; }
        Open = Mathf.Clamp01(o); Wide = Mathf.Clamp01(w); Round = Mathf.Clamp01(r);
    }

    void OnDestroy()
    {
        if (mic != null) Microphone.End(device);
#if UNITY_ANDROID && !UNITY_EDITOR
        if (UsingMeta) ovrLipSyncDll_DestroyContext(context);
#endif
    }
}
