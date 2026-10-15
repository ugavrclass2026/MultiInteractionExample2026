using System.IO;
using System.Xml;
using UnityEditor.Android;

/// <summary>
/// Adds what Meta's body and face tracking need to the Android manifest: the BODY_TRACKING and FACE_TRACKING
/// permissions, RECORD_AUDIO (face tracking from audio), and the (optional) features. The app asks for the
/// permissions at runtime.
/// </summary>
class BodyTrackingManifest : IPostGenerateGradleAndroidProject
{
    public int callbackOrder => 20;
    const string Android = "http://schemas.android.com/apk/res/android";

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        string file = Path.Combine(path, "src", "main", "AndroidManifest.xml");
        var doc = new XmlDocument(); doc.Load(file);
        var manifest = doc.DocumentElement;
        Add(doc, manifest, "uses-permission", "com.oculus.permission.BODY_TRACKING", null);
        Add(doc, manifest, "uses-feature", "com.oculus.software.body_tracking", "false");
        Add(doc, manifest, "uses-permission", "com.oculus.permission.FACE_TRACKING", null);
        Add(doc, manifest, "uses-permission", "android.permission.RECORD_AUDIO", null);
        Add(doc, manifest, "uses-feature", "oculus.software.face_tracking", "false");
        doc.Save(file);
    }

    static void Add(XmlDocument doc, XmlElement manifest, string tag, string name, string required)
    {
        foreach (XmlElement e in manifest.GetElementsByTagName(tag))
            if (e.GetAttribute("name", Android) == name) return;
        var el = doc.CreateElement(tag);
        el.SetAttribute("name", Android, name);
        if (required != null) el.SetAttribute("required", Android, required);
        manifest.AppendChild(el);
    }
}
