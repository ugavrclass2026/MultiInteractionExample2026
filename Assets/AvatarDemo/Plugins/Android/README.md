`libOVRLipSync.so` (Android ARM64) is Meta's Oculus Lipsync native library, copied unchanged from the Oculus Lipsync
Unity package. It is licensed under the Oculus Audio SDK License Version 3.3:
https://developer.oculus.com/licenses/audio-3.3/

`Scripts/LipSync.cs` calls it directly (five functions) instead of using the package's C# scripts.
