using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Build;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using VelNet.Voice;

/// <summary>
/// Builds Assets/VirtualPerson/VirtualPersonDemo.unity: the whole avatar demo (your avatar, mirror, hands and
/// controllers, locomotion, pinch, the tracking panel) plus Sam, a virtual person you talk to through VelChat.
/// Sam is the Rocketbox human driven by Rocketbox motion-capture clips (idle, listen, talk, gestures) on a Humanoid
/// Animator, his face by his voice. Menu: Avatar Demo ▸ Build Virtual Person Scene.
/// Batch: -executeMethod AvatarDemoBuilder.BuildPersonApk
/// </summary>
public static partial class AvatarDemoBuilder
{
    const string PersonScene = "Assets/VirtualPerson/VirtualPersonDemo.unity";
    const string PersonApk = "Builds/VirtualPerson.apk";
    const string ClipDir = "Assets/VirtualPerson/Animations/";

    [MenuItem("Avatar Demo/Build Virtual Person Scene")]
    public static void BuildPersonScene()
    {
        SetupHumanImport();
        SetupClipImport();
        BuildSceneAt(PersonScene, true);
    }

    // Rocketbox clips share the avatar's skeleton (Bip01), so they import as Humanoid using the avatar's own rig.
    static void SetupClipImport()
    {
        var avatar = AssetDatabase.LoadAllAssetsAtPath(HumanFbx).OfType<Avatar>().FirstOrDefault();
        foreach (var name in AgentGestures.Clips)
        {
            var imp = (ModelImporter)AssetImporter.GetAtPath(ClipDir + name + ".fbx");
            if (imp == null) throw new System.Exception("missing clip " + name);
            var cur = imp.clipAnimations;
            if (imp.animationType == ModelImporterAnimationType.Human && cur.Length == 1 && cur[0].name == name) continue;
            imp.animationType = ModelImporterAnimationType.Human;
            imp.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
            imp.sourceAvatar = avatar;
            imp.materialImportMode = ModelImporterMaterialImportMode.None;
            imp.importBlendShapes = false;
            var c = imp.defaultClipAnimations[0];
            c.name = name;
            c.loopTime = AgentGestures.Loops(name);
            c.lockRootRotation = c.lockRootHeightY = c.lockRootPositionXZ = true;      // in place: he stands where he is put
            c.keepOriginalOrientation = c.keepOriginalPositionY = c.keepOriginalPositionXZ = true;
            imp.clipAnimations = new[] { c };
            imp.SaveAndReimport();
        }
    }

    static AnimatorController BuildPersonController()
    {
        const string path = "Assets/VirtualPerson/Sam.controller";
        AssetDatabase.DeleteAsset(path);
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(path);
        var layers = ctrl.layers; layers[0].iKPass = true; ctrl.layers = layers;     // look-at IK
        var sm = ctrl.layers[0].stateMachine;
        foreach (var name in AgentGestures.Clips)
        {
            var clip = AssetDatabase.LoadAllAssetsAtPath(ClipDir + name + ".fbx").OfType<AnimationClip>().First(c => !c.name.StartsWith("__preview"));
            var st = sm.AddState(name); st.motion = clip;
            if (name == "m_idle_neutral_01") sm.defaultState = st;
        }
        EditorUtility.SetDirty(ctrl);
        return ctrl;
    }

    static void AddPerson(Transform demo, Camera cam, List<UIRay> rays)
    {
        var root = new GameObject("Sam (virtual person)").transform; root.SetParent(demo);
        var spot = new Vector3(-1.15f, 0f, 1.35f);
        root.SetPositionAndRotation(spot, Quaternion.LookRotation(new Vector3(-spot.x, 0f, -spot.z).normalized, Vector3.up));

        // the Rocketbox human, animated by motion capture
        var model = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(HumanFbx), root);
        var anim = model.GetComponent<Animator>();
        Transform B(HumanBodyBones b) => anim.GetBoneTransform(b);
        Vector3 across = B(HumanBodyBones.RightUpperArm).position - B(HumanBodyBones.LeftUpperArm).position;
        Vector3 fwd = Vector3.Cross(across, Vector3.up); fwd.y = 0f;
        model.transform.rotation = Quaternion.FromToRotation(fwd.normalized, root.forward) * model.transform.rotation;
        model.transform.localPosition = Vector3.zero;
        var tex = Root + "/Avatars/Rocketbox_Male_Adult_01/Textures/";
        Material body = TexMat("Rocketbox Body", tex + "m002_body_color.png", false), headM = TexMat("Rocketbox Head", tex + "m002_head_color.png", false),
                 opac = TexMat("Rocketbox Opacity", tex + "m002_opacity_color.png", true);
        foreach (var r in model.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++) { string n = mats[i] ? mats[i].name.ToLower() : ""; mats[i] = n.Contains("opacity") ? opac : n.Contains("head") ? headM : body; }
            r.sharedMaterials = mats;
            r.updateWhenOffscreen = true;
        }
        anim.runtimeAnimatorController = BuildPersonController();
        anim.applyRootMotion = false;
        anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        var head = B(HumanBodyBones.Head);
        var voice = new GameObject("Voice").AddComponent<AudioSource>();
        voice.transform.SetParent(head, false);
        voice.playOnAwake = false; voice.spatialBlend = 0.7f; voice.minDistance = 1.5f;
        var face = new GameObject("Sam Face").AddComponent<FaceSource>(); face.transform.SetParent(root); face.voice = voice;
        var af = root.gameObject.AddComponent<AvatarFace>(); af.face = face;
        af.boneRoot = model.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Bip01 Head");

        var g = model.AddComponent<AgentGestures>();
        g.listener = cam.transform; g.voice = voice; g.turnRoot = root;

        var mic = new GameObject("Microphone (VelVoice)").AddComponent<VelVoice>(); mic.transform.SetParent(root);
        mic.autostartMicrophone = false;
        var client = new GameObject("VelChat Client").AddComponent<VelChatClient>(); client.transform.SetParent(root);
        client.mic = mic; client.voice = voice; client.gestures = g;

        var panel = BuildPersonPanel(root, client, cam);
        foreach (var r in rays) r.panels = r.panels.Concat(new[] { panel }).ToArray();
    }

    static RectTransform BuildPersonPanel(Transform root, VelChatClient client, Camera cam)
    {
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var go = new GameObject("Conversation Panel", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(TrackedDeviceRaycaster));
        go.transform.SetParent(root, false);
        var canvas = go.GetComponent<Canvas>(); canvas.renderMode = RenderMode.WorldSpace; canvas.worldCamera = cam;
        go.GetComponent<CanvasScaler>().dynamicPixelsPerUnit = 3f;
        var rt = (RectTransform)go.transform;
        rt.sizeDelta = new Vector2(820f, 560f); rt.localScale = Vector3.one * 0.0009f;
        var where = root.position + root.right * 0.8f + Vector3.up * 1.45f;         // beside Sam
        rt.SetPositionAndRotation(where, Quaternion.LookRotation(new Vector3(where.x, 0f, where.z).normalized, Vector3.up));

        var bg = new GameObject("Background", typeof(RectTransform), typeof(Image)); bg.transform.SetParent(rt, false);
        var br = (RectTransform)bg.transform; br.anchorMin = Vector2.zero; br.anchorMax = Vector2.one; br.offsetMin = br.offsetMax = Vector2.zero;
        bg.GetComponent<Image>().color = new Color(0.11f, 0.12f, 0.14f, 0.94f);

        Text T(string name, float yTop, float height, int size, FontStyle style, Color c)
        {
            var o = new GameObject(name, typeof(RectTransform), typeof(Text)); o.transform.SetParent(rt, false);
            var r = (RectTransform)o.transform; r.anchorMin = new Vector2(0f, 1f); r.anchorMax = new Vector2(1f, 1f); r.pivot = new Vector2(0.5f, 1f);
            r.offsetMin = new Vector2(30f, -yTop - height); r.offsetMax = new Vector2(-30f, -yTop);
            var t = o.GetComponent<Text>(); t.font = font; t.fontSize = size; t.fontStyle = style; t.color = c; t.supportRichText = true;
            t.alignment = TextAnchor.UpperLeft; t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Truncate;
            return t;
        }
        Color ink = new Color(0.92f, 0.94f, 0.96f), soft = new Color(0.7f, 0.75f, 0.8f), accent = new Color(0.35f, 0.85f, 1f);
        T("Title", 20f, 50f, 38, FontStyle.Bold, ink).text = "Talk to Sam";
        client.status = T("Status", 74f, 40f, 26, FontStyle.Normal, accent);
        client.status.text = "Hold A or X and talk.";
        client.transcript = T("Transcript", 120f, 280f, 27, FontStyle.Normal, ink);
        client.timing = T("Timing", 405f, 60f, 20, FontStyle.Normal, soft);

        // hands-free toggle (ray + trigger, or ray + pinch)
        var res = new DefaultControls.Resources
        {
            standard = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd"),
            background = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd"),
            checkmark = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Checkmark.psd"),
        };
        var tg = DefaultControls.CreateToggle(res); tg.transform.SetParent(rt, false); tg.name = "Toggle: hands-free";
        var trt = (RectTransform)tg.transform; trt.anchorMin = trt.anchorMax = new Vector2(0f, 0f); trt.pivot = new Vector2(0f, 0f);
        trt.anchoredPosition = new Vector2(30f, 22f); trt.sizeDelta = new Vector2(700f, 56f);
        var box = (RectTransform)tg.transform.Find("Background");
        box.anchorMin = box.anchorMax = new Vector2(0f, 0.5f); box.pivot = new Vector2(0f, 0.5f); box.sizeDelta = new Vector2(44f, 44f); box.anchoredPosition = Vector2.zero;
        var check = (RectTransform)box.Find("Checkmark"); check.anchorMin = Vector2.zero; check.anchorMax = Vector2.one; check.offsetMin = check.offsetMax = Vector2.zero;
        var label = tg.GetComponentInChildren<Text>(); var lr = label.rectTransform;
        lr.anchorMin = Vector2.zero; lr.anchorMax = Vector2.one; lr.offsetMin = new Vector2(60f, 0f); lr.offsetMax = Vector2.zero;
        label.text = "Hands-free: talk, then pause (no button)"; label.font = font; label.fontSize = 26; label.color = ink; label.alignment = TextAnchor.MiddleLeft;
        var toggle = tg.GetComponent<Toggle>(); toggle.isOn = false;
        UnityEventTools.AddPersistentListener(toggle.onValueChanged, client.SetHandsFree);
        return rt;
    }

    public static void BuildPersonApk()
    {
        BuildPersonScene();
        var t = NamedBuildTarget.Android;
        string id = PlayerSettings.GetApplicationIdentifier(t), product = PlayerSettings.productName;
        try
        {
            PlayerSettings.SetApplicationIdentifier(t, "com.ugavr.virtualperson");
            PlayerSettings.productName = "Virtual Person";
            var r = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            { scenes = new[] { PersonScene }, locationPathName = PersonApk, target = BuildTarget.Android, options = BuildOptions.None });
            Debug.Log("[VirtualPerson] build " + r.summary.result + " " + r.summary.totalSize + " bytes, " + r.summary.totalErrors + " errors");
            if (r.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded) EditorApplication.Exit(1);
        }
        finally
        {
            PlayerSettings.SetApplicationIdentifier(t, id);
            PlayerSettings.productName = product;
            AssetDatabase.SaveAssets();
        }
    }
}
