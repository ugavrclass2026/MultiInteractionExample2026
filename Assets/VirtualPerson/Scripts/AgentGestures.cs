using UnityEngine;

/// <summary>
/// The virtual person's body language, from motion capture: Rocketbox clips (Microsoft, MIT) played on a Humanoid
/// Animator and crossfaded. Loops: idle, listen (while you talk), talk (while he speaks). One-shot gestures, chosen
/// per sentence (McNeill's types: emblems like wave and shrug, head nods and shakes, a presenting arm, ...); when one
/// ends he goes back to talking or idling. Look-at IK keeps his head and eyes on the listener, and the whole body
/// turns to face them.
/// Sits on the model, next to its Animator (OnAnimatorIK needs that).
/// </summary>
[RequireComponent(typeof(Animator))]
public class AgentGestures : MonoBehaviour
{
    public Transform listener;      // the user's headset camera
    public AudioSource voice;
    public Transform turnRoot;      // turned (yaw only) to face the listener
    public float turnSpeed = 90f;   // degrees per second
    public float fade = 0.35f;      // crossfade time (s)

    // gesture name -> Animator states (clip names). Several states = pick one at random.
    static readonly string[] Idle_ = { "m_idle_neutral_01", "m_idle_breathe_02" };
    static readonly string[] Listen_ = { "m_gestic_listen_neutral_02", "m_gestic_listen_relaxed_01" };
    static readonly string[] Talk_ = { "m_gestic_talk_neutral_01", "m_gestic_talk_relaxed_01", "m_gestic_talk_relaxed_02" };
    static string[] OneShot(string g) => g switch
    {
        "wave" => new[] { "m_wave_01" },
        "shrug" => new[] { "m_gestic_shrug_01" },
        "think" => new[] { "m_gestic_thoughtful_01" },
        "nod" => new[] { "m_gestic_listen_accept_01", "m_gestic_listen_accept_03" },
        "shake" => new[] { "m_gestic_listen_deny_01" },
        "explain" or "point" => new[] { "m_gestic_presentation_right_01" },
        "big" or "excited" => new[] { "m_gestic_talk_excited_01" },
        "laugh" => new[] { "m_gestic_laugh_low" },
        _ => null,                  // beat, small, anything else: the talking loop
    };
    /// <summary>The clip (Animator state) names this needs; the builder makes one state per name.</summary>
    public static readonly string[] Clips =
    {
        "m_idle_neutral_01", "m_idle_breathe_02", "m_gestic_listen_neutral_02", "m_gestic_listen_relaxed_01",
        "m_gestic_talk_neutral_01", "m_gestic_talk_relaxed_01", "m_gestic_talk_relaxed_02", "m_wave_01", "m_gestic_shrug_01",
        "m_gestic_thoughtful_01", "m_gestic_listen_accept_01", "m_gestic_listen_accept_03", "m_gestic_listen_deny_01",
        "m_gestic_presentation_right_01", "m_gestic_talk_excited_01", "m_gestic_laugh_low",
    };
    public static bool Loops(string clip) => clip.Contains("idle") || clip.Contains("listen_neutral") || clip.Contains("listen_relaxed") || clip.Contains("talk_neutral") || clip.Contains("talk_relaxed");

    Animator anim;
    enum Loop { Idle, Listen, Talk, Think }
    Loop loop = Loop.Idle;
    string current, oneShot;
    float lookWeight;

    void Awake() { anim = GetComponent<Animator>(); }
    void Start() { Cross(Pick(Idle_)); }

    // ---- what VelChatClient calls ----
    public void Idle() { loop = Loop.Idle; oneShot = null; Cross(Pick(Idle_)); }
    public void Listen() { loop = Loop.Listen; oneShot = null; Cross(Pick(Listen_)); }
    public void Think() { if (loop == Loop.Think) return; loop = Loop.Think; oneShot = null; Cross("m_gestic_thoughtful_01"); }
    public void Play(string g, float seconds)
    {
        loop = Loop.Talk;
        var s = OneShot(string.IsNullOrEmpty(g) ? "" : g.Trim().ToLowerInvariant());
        oneShot = s != null ? Pick(s) : null;
        Cross(oneShot ?? Pick(Talk_), oneShot != null);
    }

    /// <summary>A gesture from the words alone (the rule-based way, as in BEAT, Cassell et al. 2001).</summary>
    public static string FromWords(string s)
    {
        string t = " " + s.ToLowerInvariant() + " ";
        bool Has(params string[] w) { foreach (var x in w) if (t.Contains(x)) return true; return false; }
        if (Has(" hello", " hi ", " hi,", " hey", "welcome", "goodbye", " bye")) return "wave";
        if (Has("not sure", "don't know", "do not know", "maybe", "perhaps", "depends")) return "shrug";
        if (Has(" haha", "funny", " joke")) return "laugh";
        if (Has(" great", " amazing", " awesome", " exciting", " love ")) return "excited";
        if (Has(" yes", " sure", " absolutely", " exactly", " right ")) return "nod";
        if (Has(" no ", " no,", " no.", " not really")) return "shake";
        if (Has(" you ", " you,", " you.", " your ", " you're", " this ", " here ")) return "explain";
        return "beat";
    }

    static string Pick(string[] a) => a[Random.Range(0, a.Length)];

    void Cross(string state, bool restart = false)
    {
        if (state == current && !restart) return;
        current = state;
        anim.CrossFadeInFixedTime(state, fade, 0, 0f);
    }

    void Update()
    {
        // a one-shot gesture or the thinking clip ends: back to the loop that fits
        var info = anim.GetCurrentAnimatorStateInfo(0);
        bool ending = !anim.IsInTransition(0) && info.normalizedTime > 0.92f && !info.loop;
        if (ending && (oneShot != null || loop == Loop.Think))
        {
            oneShot = null;
            if (loop == Loop.Talk && voice != null && voice.isPlaying) Cross(Pick(Talk_));
            else if (loop == Loop.Listen || loop == Loop.Think) Cross(Pick(Listen_));   // still waiting: attentive
            else { loop = Loop.Idle; Cross(Pick(Idle_)); }
        }
        if (loop == Loop.Talk && oneShot == null && voice != null && !voice.isPlaying) { loop = Loop.Idle; Cross(Pick(Idle_)); }

        // face the listener (yaw only)
        if (turnRoot && listener)
        {
            Vector3 to = listener.position - turnRoot.position; to.y = 0f;
            if (to.sqrMagnitude > 0.01f)
                turnRoot.rotation = Quaternion.RotateTowards(turnRoot.rotation, Quaternion.LookRotation(to), turnSpeed * Time.deltaTime);
        }
    }

    // head and eyes on the listener (Humanoid look-at IK; the Animator layer has IK Pass on)
    void OnAnimatorIK(int layer)
    {
        if (!listener) return;
        lookWeight = Mathf.MoveTowards(lookWeight, 1f, Time.deltaTime);
        anim.SetLookAtWeight(0.85f * lookWeight, 0.1f, 0.8f, 1f, 0.6f);
        anim.SetLookAtPosition(listener.position);
    }
}
