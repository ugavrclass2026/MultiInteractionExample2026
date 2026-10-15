using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.OpenXR;

/// <summary>
/// Builds Assets/AvatarDemo/AvatarDemo.unity from InteractionDemo: a stereo mirror, three avatars
/// (head + hands, an IK robot, a Rocketbox human), hand tracking or controllers, and poke buttons.
/// Menu: Avatar Demo ▸ Build Scene. Batch: -executeMethod AvatarDemoBuilder.BuildApk
/// </summary>
public static class AvatarDemoBuilder
{
    const string Root = "Assets/AvatarDemo";
    const string ScenePath = Root + "/AvatarDemo.unity";
    const string HumanFbx = Root + "/Avatars/Rocketbox_Male_Adult_01/Male_Adult_01.fbx";
    const string ApkPath = "Builds/AvatarDemo.apk";

    static int L(string name) => LayerMask.NameToLayer(name);

    [MenuItem("Avatar Demo/Build Scene")]
    public static void BuildScene()
    {
        EnsureLayers("MirrorSurface", "MirrorOnly", "FirstPersonOnly");
        EnableHandTracking();
        SetupHumanImport();

        AssetDatabase.DeleteAsset(ScenePath);
        if (!AssetDatabase.CopyAsset("Assets/Scenes/InteractionDemo.unity", ScenePath)) throw new System.Exception("could not copy InteractionDemo");
        var scene = EditorSceneManager.OpenScene(ScenePath);

        var origin = GameObject.Find("XR Origin").transform;
        var cam = GameObject.Find("Main Camera").GetComponent<Camera>();
        var handL = GameObject.Find("Left Hand").GetComponent<Hand>();
        var handR = GameObject.Find("Right Hand").GetComponent<Hand>();
        var floor = GameObject.Find("Floor");
        cam.cullingMask &= ~(1 << L("MirrorOnly"));

        var demo = new GameObject("Avatar Demo").transform;

        // hands: hand tracking or controllers
        var hs = new GameObject("Hand Source").AddComponent<HandSource>();
        hs.transform.SetParent(demo);
        hs.trackingSpace = origin; hs.leftController = handL.transform; hs.rightController = handR.transform;

        var accent = Mat("Accent", new Color(0.01f, 0.54f, 0.65f));
        var orange = Mat("Orange", new Color(0.71f, 0.40f, 0.11f));
        var gray = Mat("Robot", new Color(0.78f, 0.80f, 0.83f));
        var dark = Mat("Visor", new Color(0.08f, 0.09f, 0.11f));

        var bs = new GameObject("Body Source").AddComponent<BodySource>();
        bs.transform.SetParent(demo); bs.trackingSpace = origin;
        hs.body = bs;

        var jd = new GameObject("Hand Joints").AddComponent<JointDots>();
        jd.transform.SetParent(demo); jd.hands = hs; jd.material = orange;

        // the mirror, ahead of the table
        var mirror = BuildMirror(demo, cam.transform);

        // avatars
        var avatars = new GameObject("Avatars").transform; avatars.SetParent(demo);
        var abs = BuildAbstract(avatars, cam.transform, hs, accent, dark);
        var robot = BuildRobot(avatars, cam.transform, hs, gray, accent, dark);
        var human = BuildHuman(avatars, cam.transform, hs);
        foreach (var hb in avatars.GetComponentsInChildren<HalfBodyAvatar>(true)) hb.body = bs;

        // lip sync: microphone -> visemes -> each avatar's mouth (a shape, or the human's jaw and mouth-corner bones)
        var lip = new GameObject("Lip Sync").AddComponent<LipSync>(); lip.transform.SetParent(demo);
        foreach (var av in new[] { abs, robot, human })
        {
            var vm = av.AddComponent<VisemeMouth>(); vm.lipSync = lip;
            vm.mouth = av.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Mouth");
            vm.jaw = av.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Bip01 MJaw");
            vm.leftCorner = av.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Bip01 LMouthCorner");
            vm.rightCorner = av.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Bip01 RMouthCorner");
        }

        var sw = new GameObject("Avatar Switcher").AddComponent<AvatarSwitcher>();
        sw.transform.SetParent(demo);
        sw.avatars = new[]
        {
            new AvatarSwitcher.Entry { label = "Head + hands (nothing guessed)", root = abs, showJoints = true },
            new AvatarSwitcher.Entry { label = "Robot, half body (IK arms)", root = robot, showJoints = true },
            new AvatarSwitcher.Entry { label = "Human, half body (IK arms, tracked fingers)", root = human, showJoints = false },
        };
        sw.hands = hs; sw.body = bs; sw.lipSync = lip; sw.joints = jd; sw.mirror = mirror; sw.controllerHands = new[] { handL, handR };
        sw.hmd = cam.transform; sw.floor = floor ? floor.transform : null;

        BuildPanel(demo, sw, accent, orange);
        foreach (var (h, left) in new[] { (handL, true), (handR, false) })
        {
            var p = new GameObject(left ? "Poker L" : "Poker R");
            p.transform.SetParent(demo);
            p.AddComponent<SphereCollider>(); p.AddComponent<Rigidbody>();
            var pk = p.AddComponent<Poker>(); pk.hands = hs; pk.isLeft = left; pk.controllerTip = h.grabPoint;
        }

        foreach (var left in new[] { true, false })
        {
            var g = new GameObject(left ? "Pinch Grabber L" : "Pinch Grabber R").AddComponent<PinchGrabber>();
            g.transform.SetParent(demo); g.hands = hs; g.isLeft = left; g.rig = origin;
            g.cursor = Prim(PrimitiveType.Sphere, g.transform, Vector3.zero, Vector3.one * 0.015f, accent).transform;
        }

        // Quest controller models, in the hand or put down (detached)
        var cm = new GameObject("Controller Models").AddComponent<ControllerModels>();
        cm.transform.SetParent(demo); cm.hands = hs; cm.trackingSpace = origin;
        cm.leftModel = ControllerModel(cm.transform, "left"); cm.rightModel = ControllerModel(cm.transform, "right");

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("[AvatarDemo] scene built: " + ScenePath);
    }

    // ---------- pieces ----------

    static GameObject BuildMirror(Transform parent, Transform head)
    {
        var size = new Vector2(1.6f, 1.8f);
        var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
        q.name = "Stereo Mirror";
        Object.DestroyImmediate(q.GetComponent<Collider>());
        q.transform.SetParent(parent);
        q.transform.SetPositionAndRotation(new Vector3(0f, 1.25f, 1.75f), Quaternion.identity);
        q.transform.localScale = new Vector3(size.x, size.y, 1f);
        q.layer = L("MirrorSurface");
        var m = new Material(Shader.Find("AvatarDemo/StereoMirror")); AssetDatabase.CreateAsset(m, Root + "/Materials/StereoMirror.mat");
        q.GetComponent<MeshRenderer>().sharedMaterial = m;
        q.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
        var sm = q.AddComponent<StereoMirror>();
        sm.head = head; sm.size = size;
        sm.reflect = ~((1 << L("MirrorSurface")) | (1 << L("FirstPersonOnly")));
        // a frame, outside the reflecting rectangle
        var frame = Mat("Frame", new Color(0.2f, 0.22f, 0.25f));
        foreach (var (pos, scl) in new[] {
            (new Vector3(0, size.y / 2 + 0.03f, 0.01f), new Vector3(size.x + 0.12f, 0.06f, 0.04f)),
            (new Vector3(0, -size.y / 2 - 0.03f, 0.01f), new Vector3(size.x + 0.12f, 0.06f, 0.04f)),
            (new Vector3(size.x / 2 + 0.03f, 0, 0.01f), new Vector3(0.06f, size.y, 0.04f)),
            (new Vector3(-size.x / 2 - 0.03f, 0, 0.01f), new Vector3(0.06f, size.y, 0.04f)) })
        {
            var b = GameObject.CreatePrimitive(PrimitiveType.Cube); b.name = "Frame";
            b.transform.SetParent(parent);
            b.transform.position = q.transform.position + pos; b.transform.localScale = scl;
            b.GetComponent<MeshRenderer>().sharedMaterial = frame;
        }
        return q;
    }

    static GameObject BuildAbstract(Transform parent, Transform hmd, HandSource hs, Material body, Material dark)
    {
        var root = new GameObject("Head + Hands").transform; root.SetParent(parent);
        var head = new GameObject("Head").transform; head.SetParent(root);
        var ball = Prim(PrimitiveType.Sphere, head, Vector3.zero, new Vector3(0.19f, 0.22f, 0.21f), body);
        var visor = Prim(PrimitiveType.Cube, head, new Vector3(0f, 0.01f, 0.08f), new Vector3(0.17f, 0.06f, 0.06f), dark);
        Prim(PrimitiveType.Cube, head, new Vector3(0f, -0.06f, 0.088f), new Vector3(0.05f, 0.006f, 0.02f), dark).name = "Mouth";
        SetLayer(head.gameObject, L("MirrorOnly"));
        var hl = new GameObject("Left Mitten").transform; hl.SetParent(root);
        Prim(PrimitiveType.Cube, hl, Vector3.zero, new Vector3(0.085f, 0.03f, 0.12f), body);
        var hr = new GameObject("Right Mitten").transform; hr.SetParent(root);
        Prim(PrimitiveType.Cube, hr, Vector3.zero, new Vector3(0.085f, 0.03f, 0.12f), body);
        var a = root.gameObject.AddComponent<AbstractAvatar>();
        a.hmd = hmd; a.head = head; a.leftHand = hl; a.rightHand = hr; a.hands = hs;
        return root.gameObject;
    }

    static GameObject BuildRobot(Transform parent, Transform hmd, HandSource hs, Material body, Material joint, Material dark)
    {
        var root = new GameObject("Robot").transform; root.SetParent(parent);
        Transform Bone(string n, Transform p, Vector3 worldPos, Quaternion rot)
        { var t = new GameObject(n).transform; t.SetParent(p); t.SetPositionAndRotation(worldPos, rot); return t; }
        var id = Quaternion.identity;
        var hips = Bone("Hips", root, new Vector3(0, 0.95f, 0), id);
        var chest = Bone("Chest", hips, new Vector3(0, 1.32f, 0), id);
        var neck = Bone("Neck", chest, new Vector3(0, 1.50f, 0), id);
        var head = Bone("Head", neck, new Vector3(0, 1.58f, 0), id);
        Transform lu = Bone("L Upper Arm", chest, new Vector3(-0.19f, 1.44f, 0), id), ll = Bone("L Forearm", lu, new Vector3(-0.48f, 1.44f, 0), id),
                  lh = Bone("L Hand", ll, new Vector3(-0.74f, 1.44f, 0), Quaternion.LookRotation(Vector3.left, Vector3.up));
        Transform ru = Bone("R Upper Arm", chest, new Vector3(0.19f, 1.44f, 0), id), rl = Bone("R Forearm", ru, new Vector3(0.48f, 1.44f, 0), id),
                  rh = Bone("R Hand", rl, new Vector3(0.74f, 1.44f, 0), Quaternion.LookRotation(Vector3.right, Vector3.up));
        // visuals: capsules between joints, spheres at joints
        Limb(chest, hips.position, chest.position + Vector3.up * 0.1f, 0.26f, body);
        Limb(neck, neck.position, head.position, 0.07f, body);
        // arm segments are stretched between their joints every frame, so lengthened arms show
        foreach (var (a0, b0, th) in new[] { (lu, ll, 0.085f), (ll, lh, 0.075f), (ru, rl, 0.085f), (rl, rh, 0.075f) })
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Capsule); g.name = a0.name + " Visual";
            Object.DestroyImmediate(g.GetComponent<Collider>());
            g.transform.SetParent(root, false);
            g.GetComponent<MeshRenderer>().sharedMaterial = body;
            var lv = g.AddComponent<LimbVisual>(); lv.from = a0; lv.to = b0; lv.thickness = th;
        }
        foreach (var j in new[] { lu, ll, ru, rl }) Prim(PrimitiveType.Sphere, j, Vector3.zero, Vector3.one * 0.1f, joint);
        Prim(PrimitiveType.Cube, lh, new Vector3(0, 0, 0.06f), new Vector3(0.08f, 0.03f, 0.12f), joint);
        Prim(PrimitiveType.Cube, rh, new Vector3(0, 0, 0.06f), new Vector3(0.08f, 0.03f, 0.12f), joint);
        var hv = new GameObject("Head Visual").transform; hv.SetParent(head); hv.localPosition = new Vector3(0, 0.1f, 0.01f);
        Prim(PrimitiveType.Sphere, hv, Vector3.zero, new Vector3(0.2f, 0.23f, 0.22f), body);
        Prim(PrimitiveType.Cube, hv, new Vector3(0, 0.01f, 0.085f), new Vector3(0.17f, 0.06f, 0.06f), dark);
        Prim(PrimitiveType.Cube, hv, new Vector3(0, -0.065f, 0.093f), new Vector3(0.05f, 0.006f, 0.02f), dark).name = "Mouth";
        SetLayer(hv.gameObject, L("MirrorOnly"));

        var a = root.gameObject.AddComponent<HalfBodyAvatar>();
        a.hmd = hmd; a.hands = hs; a.head = head; a.chest = chest; a.spine = new Transform[0];
        a.leftUpper = lu; a.leftLower = ll; a.leftHand = lh; a.rightUpper = ru; a.rightLower = rl; a.rightHand = rh;
        a.hideAtBind = new Transform[0];
        a.explicitHandAxes = true; a.handFingersAxis = Vector3.forward; a.handPalmAxis = Vector3.down;
        a.hmdToHead = new Vector3(0, -0.1f, -0.09f);
        return root.gameObject;
    }

    static GameObject BuildHuman(Transform parent, Transform hmd, HandSource hs)
    {
        var root = new GameObject("Human (Rocketbox)").transform; root.SetParent(parent);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(HumanFbx);
        var model = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root);
        var anim = model.GetComponent<Animator>();
        Transform B(HumanBodyBones b) => anim.GetBoneTransform(b);
        // face the model along +Z: its right shoulder should be at +X
        Vector3 across = B(HumanBodyBones.RightUpperArm).position - B(HumanBodyBones.LeftUpperArm).position;
        Vector3 fwd = Vector3.Cross(across, Vector3.up); fwd.y = 0;
        model.transform.rotation = Quaternion.FromToRotation(fwd.normalized, Vector3.forward) * model.transform.rotation;
        model.transform.localPosition = Vector3.zero;

        // materials: URP Lit with the downscaled color maps
        var tex = Root + "/Avatars/Rocketbox_Male_Adult_01/Textures/";
        var body = TexMat("Rocketbox Body", tex + "m002_body_color.png", false);
        var headM = TexMat("Rocketbox Head", tex + "m002_head_color.png", false);
        var opac = TexMat("Rocketbox Opacity", tex + "m002_opacity_color.png", true);
        foreach (var r in model.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                string n = mats[i] ? mats[i].name.ToLower() : "";
                mats[i] = n.Contains("opacity") ? opac : n.Contains("head") ? headM : body;
            }
            r.sharedMaterials = mats;
        }

        var a = root.gameObject.AddComponent<HalfBodyAvatar>();
        a.hmd = hmd; a.hands = hs;
        a.head = B(HumanBodyBones.Head); a.chest = B(HumanBodyBones.Chest) ?? B(HumanBodyBones.Spine);
        a.spine = new[] { B(HumanBodyBones.Spine), B(HumanBodyBones.Chest), B(HumanBodyBones.UpperChest) }.Where(t => t != null).ToArray();
        a.leftUpper = B(HumanBodyBones.LeftUpperArm); a.leftLower = B(HumanBodyBones.LeftLowerArm); a.leftHand = B(HumanBodyBones.LeftHand);
        a.rightUpper = B(HumanBodyBones.RightUpperArm); a.rightLower = B(HumanBodyBones.RightLowerArm); a.rightHand = B(HumanBodyBones.RightHand);
        a.hideAtBind = new[] { B(HumanBodyBones.LeftUpperLeg), B(HumanBodyBones.RightUpperLeg) };
        var fingers = new[] { "Thumb", "Index", "Middle", "Ring", "Little" };
        var seg = new[] { "Proximal", "Intermediate", "Distal" };
        for (int side = 0; side < 2; side++)
        {
            var arr = new Transform[15];
            for (int f = 0; f < 5; f++) for (int s = 0; s < 3; s++)
                    arr[f * 3 + s] = B((HumanBodyBones)System.Enum.Parse(typeof(HumanBodyBones), (side == 0 ? "Left" : "Right") + fingers[f] + seg[s]));
            if (side == 0) a.leftFingers = arr; else a.rightFingers = arr;
        }
        anim.enabled = false;            // we pose the bones ourselves
        var fp = root.gameObject.AddComponent<FirstPersonHead>(); fp.headBone = a.head;
        return root.gameObject;
    }

    static void BuildPanel(Transform parent, AvatarSwitcher sw, Material cap, Material frame)
    {
        var panel = new GameObject("Button Panel").transform; panel.SetParent(parent);
        panel.SetPositionAndRotation(new Vector3(0.62f, 1.0f, 0.55f), Quaternion.LookRotation(new Vector3(0.62f, 0f, 0.55f).normalized, Vector3.up));
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var textMat = new Material(Shader.Find("AvatarDemo/StereoText")) { mainTexture = font.material.mainTexture };
        AssetDatabase.CreateAsset(textMat, Root + "/Materials/StereoText.mat");
        TextMesh Text(Transform p, Vector3 pos, string s, float size)
        {
            var go = new GameObject("Label"); go.transform.SetParent(p, false); go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.identity;
            var t = go.AddComponent<TextMesh>(); t.font = font; t.text = s; t.fontSize = 64; t.characterSize = size;
            t.anchor = TextAnchor.MiddleCenter; t.alignment = TextAlignment.Center; t.color = new Color(0.9f, 0.93f, 0.96f);
            go.GetComponent<MeshRenderer>().sharedMaterial = textMat;
            return t;
        }
        var back = Prim(PrimitiveType.Cube, panel, new Vector3(0, 0.1f, 0.02f), new Vector3(0.52f, 0.5f, 0.02f), frame);
        Object.DestroyImmediate(back.GetComponent<Collider>());
        var items = new (string, UnityEngine.Events.UnityAction)[]
        { ("Next\navatar", sw.NextAvatar), ("Tracking\nmode", sw.NextMode), ("Mirror", sw.ToggleMirror),
          ("Height -", sw.HeightDown), ("Height +", sw.HeightUp), ("Arms -", sw.ArmsDown), ("Arms +", sw.ArmsUp) };
        for (int i = 0; i < items.Length; i++)
        {
            bool top = i < 3;
            float x = top ? -0.12f + i * 0.12f : -0.18f + (i - 3) * 0.12f, y = top ? 0.07f : -0.08f;
            var b = new GameObject("Button: " + items[i].Item1.Replace("\n", " ")).transform; b.SetParent(panel, false); b.localPosition = new Vector3(x, y, 0f);
            var col = b.gameObject.AddComponent<BoxCollider>(); col.isTrigger = true; col.size = new Vector3(0.08f, 0.08f, 0.06f); col.center = new Vector3(0, 0, -0.02f);
            var capT = Prim(PrimitiveType.Cube, b, Vector3.zero, new Vector3(0.07f, 0.07f, 0.025f), cap).transform;
            Object.DestroyImmediate(capT.GetComponent<Collider>());
            var pb = b.gameObject.AddComponent<PokeButton>(); pb.cap = capT;
            UnityEventTools.AddPersistentListener(pb.onPress, items[i].Item2);
            Text(b, new Vector3(0, -0.06f, -0.015f), items[i].Item1, 0.004f);
        }
        sw.status = Text(panel, new Vector3(0, 0.27f, -0.015f), "", 0.0038f);
    }

    // ---------- helpers ----------

    static GameObject Prim(PrimitiveType type, Transform parent, Vector3 localPos, Vector3 scale, Material m)
    {
        var g = GameObject.CreatePrimitive(type);
        Object.DestroyImmediate(g.GetComponent<Collider>());
        g.transform.SetParent(parent, false); g.transform.localPosition = localPos; g.transform.localScale = scale;
        g.GetComponent<MeshRenderer>().sharedMaterial = m;
        return g;
    }

    static void Limb(Transform bone, Vector3 a, Vector3 b, float thick, Material m)
    {
        var g = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        Object.DestroyImmediate(g.GetComponent<Collider>());
        g.transform.SetParent(bone, true);
        g.transform.position = (a + b) / 2f;
        g.transform.rotation = Quaternion.FromToRotation(Vector3.up, b - a);
        g.transform.localScale = new Vector3(thick, (b - a).magnitude / 2f, thick);
        g.GetComponent<MeshRenderer>().sharedMaterial = m;
    }

    static void SetLayer(GameObject g, int layer) { foreach (var t in g.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer; }

    static Material Mat(string name, Color c)
    {
        var path = Root + "/Materials/" + name + ".mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, path); }
        m.SetColor("_BaseColor", c); m.SetFloat("_Smoothness", 0.35f);
        return m;
    }

    static Transform ControllerModel(Transform parent, string side)
    {
        var fbx = AssetDatabase.LoadAssetAtPath<GameObject>($"{Root}/Controllers/QuestTouchPlus_{side}.fbx");
        var go = (GameObject)PrefabUtility.InstantiatePrefab(fbx, parent);
        go.name = "Quest controller " + side;
        var m = TexMat("QuestTouchPlus_" + side, $"{Root}/Controllers/QuestTouchPlus_{side}.png", false);
        var b = new Bounds(); bool first = true;
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
        {
            r.sharedMaterials = Enumerable.Repeat(m, r.sharedMaterials.Length).ToArray();
            r.shadowCastingMode = ShadowCastingMode.Off;
            if (first) { b = r.bounds; first = false; } else b.Encapsulate(r.bounds);
        }
        Debug.Log($"[AvatarDemo] controller model {side}: bounds center {b.center - go.transform.position:F3} size {b.size:F3}");
        go.SetActive(false);
        return go.transform;
    }

    static Material TexMat(string name, string texPath, bool cutout)
    {
        var m = Mat(name, Color.white);
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
        m.SetTexture("_BaseMap", tex);
        m.SetFloat("_Smoothness", 0.2f);
        if (cutout) { m.SetFloat("_AlphaClip", 1f); m.SetFloat("_Cutoff", 0.4f); m.EnableKeyword("_ALPHATEST_ON"); m.SetFloat("_Cull", 0f); }
        EditorUtility.SetDirty(m);
        return m;
    }

    static void SetupHumanImport()
    {
        var imp = (ModelImporter)AssetImporter.GetAtPath(HumanFbx);
        if (imp.animationType != ModelImporterAnimationType.Human)
        {
            imp.animationType = ModelImporterAnimationType.Human;
            imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            imp.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            imp.SaveAndReimport();
        }
        if (!imp.isReadable) { imp.isReadable = true; imp.SaveAndReimport(); }   // FirstPersonHead copies the mesh
        foreach (var t in new[] { "m002_body_color", "m002_head_color", "m002_opacity_color" })
        {
            var ti = (TextureImporter)AssetImporter.GetAtPath(Root + "/Avatars/Rocketbox_Male_Adult_01/Textures/" + t + ".png");
            if (ti != null && t.Contains("opacity") && !ti.alphaIsTransparency) { ti.alphaIsTransparency = true; ti.SaveAndReimport(); }
        }
    }

    static void EnsureLayers(params string[] names)
    {
        var tm = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var layers = tm.FindProperty("layers");
        foreach (var n in names)
        {
            bool exists = false;
            for (int i = 8; i < 32; i++) if (layers.GetArrayElementAtIndex(i).stringValue == n) exists = true;
            if (exists) continue;
            for (int i = 31; i >= 8; i--)
                if (string.IsNullOrEmpty(layers.GetArrayElementAtIndex(i).stringValue)) { layers.GetArrayElementAtIndex(i).stringValue = n; break; }
        }
        tm.ApplyModifiedProperties();
    }

    static void EnableHandTracking()
    {
        // in batch mode the feature list may not have been discovered yet
        UnityEditor.XR.OpenXR.Features.FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android);
        var s = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
        var ht = s.GetFeature<UnityEngine.XR.Hands.OpenXR.HandTracking>();
        if (ht && !ht.enabled) ht.enabled = true;
        var mbf = s.GetFeature<MetaBodyFeature>();
        if (mbf && !mbf.enabled) mbf.enabled = true;
        Debug.Log($"[AvatarDemo] MetaBodyFeature: {(mbf ? mbf.enabled.ToString() : "missing")}");
        var det = s.GetFeature<DetachedControllersFeature>();
        if (det && !det.enabled) det.enabled = true;
        Debug.Log($"[AvatarDemo] DetachedControllersFeature: {(det ? det.enabled.ToString() : "missing")}");
        var aim = s.GetFeature<UnityEngine.XR.Hands.OpenXR.MetaHandTrackingAim>();
        if (aim && !aim.enabled) aim.enabled = true;
        EditorUtility.SetDirty(s);
        Debug.Log($"[AvatarDemo] OpenXR hand tracking feature: {(ht ? ht.enabled.ToString() : "missing")}, aim: {(aim ? aim.enabled.ToString() : "missing")}");
    }

    // ---------- batch build ----------

    public static void BuildApk()
    {
        BuildScene();
        var t = NamedBuildTarget.Android;
        string id = PlayerSettings.GetApplicationIdentifier(t), product = PlayerSettings.productName;
        try
        {
            PlayerSettings.SetApplicationIdentifier(t, "com.ugavr.avatardemo");
            PlayerSettings.productName = "Avatar Demo";
            var r = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            { scenes = new[] { ScenePath }, locationPathName = ApkPath, target = BuildTarget.Android, options = BuildOptions.None });
            Debug.Log("[AvatarDemo] build " + r.summary.result + " " + r.summary.totalSize + " bytes, " + r.summary.totalErrors + " errors");
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
