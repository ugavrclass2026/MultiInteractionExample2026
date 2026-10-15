using UnityEngine;

/// <summary>
/// Meta face tracking (through MetaBodyFeature): every frame, 70 expression weights and 15 visemes, 0..1.
///   - expressions: Meta's FACS-style set (XrFaceExpression2FB), e.g. JawDrop, EyesClosedL, LipCornerPullerR (smile)
///   - visemes: mouth shapes for speech: sil PP FF TH DD kk CH SS nn RR aa E ih oh ou
/// Quest Pro measures the face with its face cameras. Quest 2, 3 and 3S have none, so the runtime estimates the
/// whole face (mouth, cheeks, eyelids, brows) from the microphone ("Audio To Expression").
/// AvatarFace puts these on each avatar.
/// </summary>
[DefaultExecutionOrder(-45)]
public class FaceSource : MonoBehaviour
{
    public static readonly string[] VisemeNames = { "sil", "PP", "FF", "TH", "DD", "kk", "CH", "SS", "nn", "RR", "aa", "E", "ih", "oh", "ou" };

    // expression indices used here (XrFaceExpression2FB; L/R = the person's left/right)
    public const int BrowLowererL = 0, BrowLowererR = 1, CheekPuffL = 2, CheekPuffR = 3, CheekRaiserL = 4, CheekRaiserR = 5,
        EyesClosedL = 12, EyesClosedR = 13, InnerBrowRaiserL = 22, InnerBrowRaiserR = 23, JawDrop = 24,
        LipCornerDepressorL = 30, LipCornerDepressorR = 31, LipCornerPullerL = 32, LipCornerPullerR = 33,
        LipFunnelerLB = 34, LipFunnelerLT = 35, LipFunnelerRB = 36, LipFunnelerRT = 37, LipPuckerL = 40, LipPuckerR = 41,
        LipStretcherL = 42, LipStretcherR = 43, LowerLipDepressorL = 51, LowerLipDepressorR = 52,
        NoseWrinklerL = 55, NoseWrinklerR = 56, OuterBrowRaiserL = 57, OuterBrowRaiserR = 58,
        UpperLidRaiserL = 59, UpperLidRaiserR = 60, UpperLipRaiserL = 61, UpperLipRaiserR = 62;

    // Audio-only face tracking (Quest 3, 2026-10-14): at rest jaw drop ~0.1 and lip corner puller ~0.25; while talking
    // jaw drop only reaches ~0.2 but the visemes imply ~0.8 opening. So: a dead zone, then a gain (blinks reach 1 already).
    [Tooltip("Expression weights below this count as zero (blinks excepted)")]
    public float deadZone = 0.15f;
    [Tooltip("Expression weights above the dead zone are multiplied by this, capped at 1 (blinks excepted)")]
    public float gain = 2.5f;

    public readonly float[] expressions = new float[MetaBodyFeature.ExpressionCount];
    public readonly float[] visemes = new float[MetaBodyFeature.VisemeCount];
    public bool Tracking { get; private set; }
    public string Status { get; private set; } = "starting";

    /// <summary>An expression weight after the dead zone and gain (blinks unchanged), 0..1.</summary>
    public float this[int expression] => expression == EyesClosedL || expression == EyesClosedR
        ? expressions[expression] : Mathf.Clamp01((expressions[expression] - deadZone) * gain);
    public float Pair(int l, int r) => (this[l] + this[r]) * 0.5f;

    /// <summary>Jaw opening, 0..1: the larger of what the visemes imply and the jaw-drop expression.</summary>
    public float Open { get; private set; }
    /// <summary>Lips spread (stretch, smile, E/ih/SS) minus lips rounded (pucker, funnel, oh/ou): -1..1.</summary>
    public float Spread { get; private set; }

    // How much each viseme opens the jaw, spreads the lips and rounds them (hand-tuned, same order as VisemeNames).
    static readonly float[] openOf  = { 0, 0, .1f, .3f, .3f, .35f, .35f, .15f, .2f, .3f, 1f, .6f, .4f, .8f, .45f };
    static readonly float[] wideOf  = { 0, 0, .2f, .2f, .2f, .2f, .3f, .6f, .2f, 0, .3f, 1f, .8f, 0, 0 };
    static readonly float[] roundOf = { 0, .2f, 0, 0, 0, 0, .3f, 0, 0, .4f, 0, 0, 0, .7f, 1f };
    float peakJaw, peakVisOpen, peakBrow, peakSmile, peakBlink, nextLog;

    const string FacePermission = "com.oculus.permission.FACE_TRACKING";
    bool asked;
    bool visemesValid;

    void Update()
    {
        Tracking = false;
        var f = MetaBodyFeature.Instance;
        if (f == null) { Status = "unavailable (needs the OpenXR feature, on a Quest)"; return; }
#if UNITY_ANDROID && !UNITY_EDITOR
        bool face = UnityEngine.Android.Permission.HasUserAuthorizedPermission(FacePermission);
        bool mic = UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone);
        if (!face || !mic)
        {
            if (!asked) { asked = true; UnityEngine.Android.Permission.RequestUserPermissions(new[] { FacePermission, UnityEngine.Android.Permission.Microphone }); }
            Status = "waiting for face tracking / microphone permission";
            return;
        }
#endif
        if (!f.FaceTrackingOn && !f.SetFaceTracking(true)) { Status = f.FaceStatus; return; }
        Tracking = f.TryGetFace(expressions, visemes, out visemesValid);
        if (!visemesValid) System.Array.Clear(visemes, 0, visemes.Length);
        if (!Tracking) System.Array.Clear(expressions, 0, expressions.Length);
        float vo = 0, vw = 0, vr = 0;
        for (int i = 0; i < visemes.Length; i++) { vo += visemes[i] * openOf[i]; vw += visemes[i] * wideOf[i]; vr += visemes[i] * roundOf[i]; }
        Open = Mathf.Clamp01(Mathf.Max(this[JawDrop], vo));
        float spread = Pair(LipStretcherL, LipStretcherR) + 0.5f * Pair(LipCornerPullerL, LipCornerPullerR)
                     - Pair(LipPuckerL, LipPuckerR) - 0.5f * (Pair(LipFunnelerLB, LipFunnelerRB) + Pair(LipFunnelerLT, LipFunnelerRT));
        Spread = Mathf.Clamp(spread + vw - vr, -1f, 1f);

        // tuning aid: the largest raw values over each 3 s
        peakJaw = Mathf.Max(peakJaw, expressions[JawDrop]); peakVisOpen = Mathf.Max(peakVisOpen, vo);
        peakBrow = Mathf.Max(peakBrow, Mathf.Max(expressions[InnerBrowRaiserL], expressions[OuterBrowRaiserL]));
        peakSmile = Mathf.Max(peakSmile, expressions[LipCornerPullerL]); peakBlink = Mathf.Max(peakBlink, expressions[EyesClosedL]);
        if (Time.time > nextLog)
        {
            nextLog = Time.time + 3f;
            if (Tracking) Debug.Log($"[AvatarDemo] face peaks (raw): jaw {peakJaw:F2}, viseme open {peakVisOpen:F2}, brow {peakBrow:F2}, smile {peakSmile:F2}, blink {peakBlink:F2}");
            peakJaw = peakVisOpen = peakBrow = peakSmile = peakBlink = 0f;
        }
        int top = 0; for (int i = 1; i < visemes.Length; i++) if (visemes[i] > visemes[top]) top = i;
        Status = $"{f.FaceStatus}, open {Open:F2}, blink {Pair(EyesClosedL, EyesClosedR):F2}" + (visemesValid ? $", viseme {VisemeNames[top]}" : "");
    }
}
