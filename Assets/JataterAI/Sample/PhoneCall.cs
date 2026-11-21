using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace JataterAI.Sample
{
    public sealed class PhoneCall : MonoBehaviour
    {
        [Serializable] class Caller { public string name, voice, personality, greeting; }
        [Serializable] class Directory { public Caller[] callers; }
        [SerializeField] ConnectionSettings settings;
        Caller[] callers;
        JataterClient client;
        ISpeaker speaker;
        ISteamTicketSource steam;
        IRecognizer recognizer;
        Conversation conversation;
        CancellationTokenSource life;
        AudioClip recording, rendered;
        AudioSource audioOut;
        string mic, testKey = "", input = "", feedback = "", feedbackId, feedbackSnapshot;
        string transcript = "", status = "";
        bool connected, busy, showFeedback;
        int current;
        Vector2 scroll;

        void Awake()
        {
            life = new CancellationTokenSource();
            audioOut = gameObject.AddComponent<AudioSource>();
            if (!settings) settings = Resources.Load<ConnectionSettings>("JataterSampleConnection");
            settings = settings ? Instantiate(settings) : ScriptableObject.CreateInstance<ConnectionSettings>();
            callers = JsonUtility.FromJson<Directory>(Resources.Load<TextAsset>("JataterCallers/Characters").text).callers;
            if (settings.authentication == Authentication.ScopedTestKey && (Application.isEditor || Debug.isDebugBuild))
                testKey = Environment.GetEnvironmentVariable("JATATER_SCOPED_KEY") ?? "";
            steam = settings.authentication == Authentication.Steam ? Adapters.Steam?.Invoke(gameObject, settings.externalSteamManager) : null;
        }

        void OnDestroy()
        {
            life.Cancel();
            EndRecording();
            if (rendered) Destroy(rendered);
        }

        void EndRecording()
        {
            if (!recording) return;
            Microphone.End(mic);
            Destroy(recording); recording = null;
        }

        void Update()
        {
            if (recording && !Microphone.IsRecording(mic)) FinishRecording(true);
        }

        void OnGUI()
        {
            float scale = Mathf.Clamp(Mathf.Min(Screen.width / 650f, Screen.height / 570f), 0.5f, 1.5f);
            var old = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            float w = Mathf.Min(680, Screen.width / scale - 28), h = Mathf.Min(630, Screen.height / scale - 20);
            GUILayout.BeginArea(new Rect((Screen.width / scale - w)/2, (Screen.height / scale - h)/2, w, h), GUI.skin.box);
            GUILayout.Space(10);
            GUILayout.Label("Jatater AI  /  Phone calls", new GUIStyle(GUI.skin.label) { fontSize = 25, alignment = TextAnchor.MiddleCenter });
            if (!connected)
            {
                GUILayout.Label("Project environment endpoint");
                settings.endpoint = GUILayout.TextField(settings.endpoint ?? "");
                if (settings.authentication == Authentication.ScopedTestKey)
                {
                    GUILayout.Label("Scoped testing key • held in memory for this session");
                    testKey = GUILayout.PasswordField(testKey, '•');
                }
                GUILayout.Label("Choose a caller");
                for (int i = 0; i < callers.Length; i++)
                {
                    int index = i;
                    if (GUILayout.Button(callers[i].name, GUILayout.Height(37))) Dial(index);
                }
            }
            else
            {
                GUILayout.Label("On the phone with " + callers[current].name);
                scroll = GUILayout.BeginScrollView(scroll, GUI.skin.box, GUILayout.ExpandHeight(true));
                GUILayout.Label(transcript, new GUIStyle(GUI.skin.label) { wordWrap = true, richText = false });
                GUILayout.EndScrollView();
                GUI.enabled = !busy;
                input = GUILayout.TextField(input ?? "", 2000);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Send text", GUILayout.Height(32)) && !string.IsNullOrWhiteSpace(input))
                {
                    string turn = input.Trim(); input = ""; Run(() => SendTurn(turn));
                }
                GUI.enabled = !busy && !settings.externalMicrophone;
                if (GUILayout.Button(recording ? "Send recording" : "Record a turn", GUILayout.Height(32)))
                {
                    if (recording) FinishRecording(false);
                    else Run(StartRecording);
                }
                GUILayout.EndHorizontal();
                GUI.enabled = !busy && !recording;
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Hang up")) { connected = false; audioOut.Stop(); conversation = null; status = ""; }
                if (GUILayout.Button("Feedback")) showFeedback = !showFeedback;
                GUILayout.EndHorizontal();
                if (showFeedback)
                {
                    feedback = GUILayout.TextArea(feedback, 10000);
                    if (GUILayout.Button("Send feedback") && !string.IsNullOrWhiteSpace(feedback)) Run(ReportFeedback);
                }
            }
            GUI.enabled = true;
            GUILayout.Label(status, new GUIStyle(GUI.skin.label) { wordWrap = true, richText = false });
            GUILayout.EndArea();
            GUI.matrix = old;
        }

        void Dial(int index)
        {
            if (settings.authentication == Authentication.ScopedTestKey && !Application.isEditor && !Debug.isDebugBuild)
            { status = "Scoped testing keys require the Editor or a development build. Configure Steam for this player."; return; }
            if (string.IsNullOrWhiteSpace(settings.endpoint) || (settings.authentication == Authentication.ScopedTestKey && string.IsNullOrWhiteSpace(testKey)))
            { status = "Enter your project endpoint and complete the selected authentication setup."; return; }
            client = new JataterClient(settings, () => testKey, steam);
            current = index; var caller = callers[index];
            conversation = new Conversation(settings, caller.personality, caller.greeting);
            transcript = caller.name + ": " + caller.greeting;
            connected = true; status = "";
            Run(() => Play(caller.greeting));
        }

        async void Run(Func<Task> action)
        {
            if (busy) return;
            busy = true; status = "";
            try { await action(); }
            catch (OperationCanceledException) { }
            catch (Exception e) { if (this) status = e.Message; }
            finally { busy = false; }
        }

        async Task StartRecording()
        {
            if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
            {
                var op = Application.RequestUserAuthorization(UserAuthorization.Microphone);
                while (!op.isDone) { life.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
            }
            if (Microphone.devices.Length == 0) throw new Exception("No microphone found. You can type a turn.");
            mic = Microphone.devices[0];
            recording = Microphone.Start(mic, false, 20, 16000);
            if (!recording) throw new Exception("Could not start the microphone.");
            status = "Listening… click Send recording when finished.";
        }

        void FinishRecording(bool atLimit)
        {
            int frames = atLimit ? recording.samples : Microphone.GetPosition(mic);
            if (frames <= 0) { EndRecording(); status = "No audio captured."; return; }
            var clip = recording;
            var samples = new float[frames * clip.channels];
            int rate = clip.frequency, channels = clip.channels;
            bool read = clip.GetData(samples, 0); EndRecording();
            if (!read) { status = "Could not read microphone audio."; return; }
            SubmitMicrophoneTurn(samples, rate, channels);
        }

        // Call on the Unity main thread with the completed local speaker turn.
        public void SubmitMicrophoneTurn(float[] interleavedPcm, int rate, int channels)
        {
            if (!connected || busy) return;
            Run(async () =>
            {
                status = "Transcribing…";
                string text;
                if (settings.recognition == Recognition.Whisper)
                {
                    recognizer ??= Adapters.Whisper?.Invoke(gameObject, settings.whisperModel, settings.language);
                    if (recognizer == null) throw new Exception("Install Whisper and its model in Tools > Jatater AI > Setup, or select Inworld recognition.");
                    text = await recognizer.TranscribeAsync(interleavedPcm, rate, channels, life.Token);
                }
                else text = await client.TranscribeAsync(VoiceAudio.Wav16(interleavedPcm, rate, channels), life.Token);
                if (string.IsNullOrWhiteSpace(text)) { status = "No speech recognized."; return; }
                await SendTurn(text.Trim());
            });
        }

        async Task SendTurn(string turn)
        {
            status = "Waiting for " + callers[current].name + "…";
            string reply = await client.ChatAsync(conversation.Next(turn), life.Token);
            conversation.Commit(turn, reply);
            transcript += "\n\nYou: " + turn + "\n" + callers[current].name + ": " + reply;
            if (transcript.Length > 18000) transcript = transcript.Substring(transcript.Length - 14000);
            scroll.y = float.MaxValue; status = "";
            await Play(reply);
        }

        async Task Play(string text)
        {
            if (settings.playback == Playback.Text) return;
            if (settings.playback == Playback.Pocket)
            {
                speaker ??= Adapters.Pocket?.Invoke(gameObject);
                if (speaker == null) { status = "Text is ready. Install Pocket TTS in Tools > Jatater AI > Setup to hear the caller."; return; }
                string id = callers[current].voice;
                var reference = Resources.Load<TextAsset>("JataterCallers/" + id + ".wav");
                string path = Path.Combine(Application.temporaryCachePath, "jatater-" + id + ".wav");
                File.WriteAllBytes(path, reference.bytes);
                status = "Synthesizing locally…";
                await speaker.SpeakAsync(text, path, life.Token);
            }
            else
            {
                byte[] wav = await client.SpeakAsync(text, life.Token);
                if (rendered) Destroy(rendered);
                rendered = VoiceAudio.Decode(wav); audioOut.clip = rendered; audioOut.Play();
                while (audioOut.isPlaying) { life.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
            }
            status = "";
        }

        async Task ReportFeedback()
        {
            if (feedback != feedbackSnapshot) { feedbackId = Guid.NewGuid().ToString(); feedbackSnapshot = feedback; }
            await client.FeedbackAsync(feedbackId, feedback, life.Token);
            showFeedback = false; feedback = ""; feedbackSnapshot = null; status = "Feedback received.";
        }
    }
}
