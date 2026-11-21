using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace JataterAI.Editor
{
    public sealed class JataterSetup : EditorWindow
    {
        const string Repo = "https://github.com/Metater/jatater-ai-example.git?path=/Integrations/";
        const string Tag = "#v0.1.0";
        const string Root = "Assets/JataterAI/Sample";
        const string SettingsPath = Root + "/Resources/JataterSampleConnection.asset";
        const string Ort = "com.github.asus4.onnxruntime";
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        static readonly Dictionary<string,string> PocketHashes = new Dictionary<string,string> {
            {"bos_before_voice.npy","F46EDF4F7007B7BA4EA58831F49D003E59E167B4641C44BB3ADDFE9231A780B1"},
            {"flow_lm_flow_int8.onnx","56520801306E84D33153D4510226CFF07CF55271FE5DA4170A064D8EEFB89E59"},
            {"flow_lm_main_int8.onnx","FE08A1D4132D549F726AC8ED197B240EC0A19A48859EC0558AD2F3D8CE659020"},
            {"mimi_decoder_int8.onnx","2AB9E276AB9F30559BEEF41EDF1049916CFBC5CEFEB908F6039AFBEA33CAA73C"},
            {"mimi_encoder.onnx","E65BA4EAF68D3813DC7B095AE3C319DA2B019254D813A087C93232073BFF2DAD"},
            {"text_conditioner.onnx","C2DBC15BC9C771532624D0E4469AA810D226AF7563CB8549756BA8E2B0EA0A1F"},
            {"tokenizer.model","D461765AE179566678C93091C5FA6F2984C31BBE990BF1AA62D92C64D91BC3F6"}
        };
        [Serializable] class RegistryEntry { public Distribution dist; }
        [Serializable] class Distribution { public string integrity; }
        string status;
        Vector2 scroll;
        CancellationTokenSource work;
        [MenuItem("Tools/Jatater AI/Setup")]
        public static void Open() => GetWindow<JataterSetup>("Jatater AI");
        void OnDisable() => work?.Cancel();

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("Phone call setup", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Create a project, publish a Client controlled prompt, enable scoped testing keys and create a $0.50 key. Paste the environment endpoint into Connection settings. Enter the key at runtime. For a shipped game, switch authentication to Steam.", MessageType.Info);
            if (GUILayout.Button("Open workspace")) Application.OpenURL("https://account.jatater.ai/dashboard/");
            if (GUILayout.Button("Select connection settings"))
            {
                var asset = AssetDatabase.LoadAssetAtPath<ConnectionSettings>(SettingsPath);
                if (!asset)
                {
                    asset = CreateInstance<ConnectionSettings>();
                    AssetDatabase.CreateAsset(asset, SettingsPath); AssetDatabase.SaveAssets();
                }
                Selection.activeObject = asset;
            }
            if (GUILayout.Button("Open phone call scene") && EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                EditorSceneManager.OpenScene(Root + "/PhoneCall.unity");
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Optional integrations", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Local speech in the sample targets Windows x64. You can type immediately. For Inworld, enable the service and select a server-side key in your project. If your game owns the microphone or Steam lifecycle, enable the corresponding external option.", MessageType.None);
            EditorGUI.BeginDisabledGroup(work != null || EditorApplication.isPlayingOrWillChangePlaymode);
            foreach (string item in new[]{"Whisper", "Pocket", "Steam"})
                if (GUILayout.Button("Install " + item + " adapter")) Run(token => Install(item, token));
            if (GUILayout.Button("Download English Whisper model (148 MB)")) Run(async token => {
                await Download("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin",
                    Path.Combine(Application.streamingAssetsPath,"Whisper/ggml-base.en.bin"),
                    "A03779C86DF3323075F5E796CB2CE5029F00EC8869EEE3FDFB897AFE36C6D002", token);
                AssetDatabase.Refresh();
            });
            if (GUILayout.Button("Download English Pocket models (165 MB)")) Run(async token => {
                foreach (var file in PocketHashes)
                    await Download("https://huggingface.co/lookbe/pocket-tts-onnx-v2/resolve/main/english/" + file.Key,
                        Path.Combine(Application.streamingAssetsPath,"pocket-tts-unity/lookbe/pocket-tts-onnx-v2/english",file.Key), file.Value, token);
                AssetDatabase.Refresh();
            });
            EditorGUI.EndDisabledGroup();
            if (work != null && GUILayout.Button("Cancel download")) work.Cancel();
            EditorGUILayout.HelpBox(status ?? "Model downloads are optional. Configure your own Whisper file in Connection settings.", MessageType.None);
            if (GUILayout.Button("Browse Whisper models")) Application.OpenURL("https://huggingface.co/ggerganov/whisper.cpp/tree/main");
            EditorGUILayout.EndScrollView();
        }
        async void Run(Func<CancellationToken,Task> action)
        {
            if (work != null) return;
            work = new CancellationTokenSource();
            try { await action(work.Token); status = "Ready."; }
            catch (OperationCanceledException) { status = "Canceled."; }
            catch (Exception e) { status = e.Message; UnityEngine.Debug.LogError(status); }
            finally { work.Dispose(); work = null; if (this) Repaint(); }
        }
        async Task Install(string name, CancellationToken token)
        {
            var list = Client.List(true, true);
            while (!list.IsCompleted) await Task.Delay(100, token);
            if (list.Status == StatusCode.Failure) throw new Exception(list.Error.message);
            var installed = new HashSet<string>();
            foreach (var package in list.Result) installed.Add(package.name);
            var packages = new List<string>();
            if (name == "Steam" && !installed.Contains("com.rlabrecque.steamworks.net"))
                packages.Add("https://github.com/rlabrecque/Steamworks.NET.git?path=/com.rlabrecque.steamworks.net#0da40a3dc7c98297435ca62438441fd792990418");
            if (name == "Whisper" && !installed.Contains("com.whisper.unity"))
                packages.Add("https://github.com/Macoron/whisper.unity.git?path=/Packages/com.whisper.unity#529a628a915a97799e89e061af9cb7c71407124d");
            if (name == "Pocket" && !installed.Contains(Ort))
            {
                string archive = Ort + "-0.4.2.tgz";
                string dest = "Packages/JataterDependencies/" + archive;
                var metadata = JsonUtility.FromJson<RegistryEntry>(await Http.GetStringAsync("https://registry.npmjs.org/" + Ort + "/0.4.2"));
                if (metadata?.dist?.integrity?.StartsWith("sha512-", StringComparison.Ordinal) != true) throw new Exception("Missing ONNX Runtime package integrity hash.");
                await Download("https://registry.npmjs.org/" + Ort + "/-/" + archive, dest, metadata.dist.integrity, token);
                packages.Add("file:JataterDependencies/" + archive);
            }
            string adapter = "ai.jatater." + name.ToLowerInvariant();
            if (!installed.Contains(adapter))
            {
                string local = "Integrations/" + name;
                string bundled = "Assets/JataterAI/Editor/" + name + ".tgz.bytes";
                if (Directory.Exists(local)) packages.Add("file:../" + local);
                else if (File.Exists(bundled))
                {
                    string file = adapter + "-0.1.0.tgz";
                    Directory.CreateDirectory("Packages/JataterDependencies");
                    File.Copy(bundled, "Packages/JataterDependencies/" + file, true);
                    packages.Add("file:JataterDependencies/" + file);
                }
                else packages.Add(Repo + name + Tag);
            }
            if (packages.Count == 0) return;
            status = "Installing packages…"; Repaint(); token.ThrowIfCancellationRequested();
            var add = Client.AddAndRemove(packages.ToArray());
            while (!add.IsCompleted) await Task.Delay(200);
            if (add.Status == StatusCode.Failure) throw new Exception(add.Error.message);
        }
        async Task Download(string url, string destination, string digest, CancellationToken token)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string temp = destination + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                if (File.Exists(destination))
                {
                    if (await Task.Run(() => Matches(destination, digest), token)) return;
                    throw new IOException("An existing file differs from the expected model. Move it or select it manually: " + destination);
                }
                status = "Downloading " + Path.GetFileName(destination) + "…"; Repaint();
                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                using (var source = await response.Content.ReadAsStreamAsync())
                using (var output = File.Create(temp)) await source.CopyToAsync(output, 81920, token);
                if (!await Task.Run(() => Matches(temp, digest), token)) throw new IOException("Checksum mismatch for " + Path.GetFileName(destination));
                File.Move(temp, destination);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        static bool Matches(string path, string digest)
        {
            using var file = File.OpenRead(path);
            if (digest.StartsWith("sha512-", StringComparison.Ordinal))
            {
                using var sha = SHA512.Create();
                return string.Equals(digest.Substring(7), Convert.ToBase64String(sha.ComputeHash(file)), StringComparison.Ordinal);
            }
            using var hash = SHA256.Create();
            return string.Equals(digest, BitConverter.ToString(hash.ComputeHash(file)).Replace("-", ""), StringComparison.OrdinalIgnoreCase);
        }
    }
}
