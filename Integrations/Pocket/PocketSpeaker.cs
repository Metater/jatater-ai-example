using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PocketTTS;
using UnityEngine;

namespace JataterAI.PocketIntegration
{
    public sealed class PocketSpeaker : MonoBehaviour, ISpeaker
    {
        PocketTTS.PocketTTS engine;
        AudioSource output;
        AudioClip clip;
        readonly Queue<float> pcm = new Queue<float>();
        readonly object audioLock = new object();
        int queued { get { lock (audioLock) return pcm.Count; } }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Register() => Adapters.Pocket = host => host.AddComponent<PocketSpeaker>();

        public async Task SpeakAsync(string text, string referencePath, CancellationToken cancellation)
        {
            if (Application.platform != RuntimePlatform.WindowsEditor && Application.platform != RuntimePlatform.WindowsPlayer)
                throw new PlatformNotSupportedException("The sample Pocket TTS integration requires Windows x64. Select Inworld or Text for this platform.");
            if (!engine)
            {
                if (!PocketTtsLanguageModels.TryResolve(Application.streamingAssetsPath, "en", out _, out _, out string error))
                    throw new Exception(error + " Download English models in Tools > Jatater AI > Setup.");
                var child = new GameObject("Local Pocket TTS");
                child.transform.SetParent(transform, false); child.SetActive(false);
                var encoder = child.AddComponent<MimiEncoder>();
                var voices = child.AddComponent<PocketTTSVoices>();
                voices.encoder = encoder;
                var model = child.AddComponent<PocketTTSModel>();
                model.DiffusionStep = 3; model.EnableProfiling = false;
                engine = child.AddComponent<PocketTTS.PocketTTS>();
                engine.defaultVoicePath = referencePath;
                child.SetActive(true);
                engine.OnFrameGenerated += Frame;
                output = child.AddComponent<AudioSource>();
                clip = AudioClip.Create("Phone caller", 24000, 1, 24000, true, Read);
                output.clip = clip; output.loop = true;
                engine.InitModel();
            }
            await Ready(cancellation);
            lock (audioLock) pcm.Clear();
            if (!engine.Prompt(text, referencePath, "en")) throw new Exception("Pocket TTS could not start this reply.");
            var time = Stopwatch.StartNew();
            bool played = false;
            try
            {
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!engine || engine.Status == ModelStatus.Error) throw new Exception("Pocket TTS could not synthesize this reply.");
                    if (time.Elapsed.TotalSeconds > 120) throw new TimeoutException("Local voice synthesis timed out.");
                    bool done = engine.Status == ModelStatus.Ready && engine.CanUnload && !engine.HasBufferedAudio;
                    int count = queued;
                    if (!played && (count >= 1920 || (done && count > 0))) { output.Play(); played = true; }
                    if (done && count == 0) break;
                    await Task.Yield();
                }
                if (!played) throw new Exception("Pocket TTS did not return audio.");
                AudioSettings.GetDSPBufferSize(out int bufferLength, out int buffers);
                await Task.Delay(Math.Max(40, 1000 * bufferLength * buffers / Math.Max(1, AudioSettings.outputSampleRate)), cancellation);
            }
            finally { if (output) output.Stop(); }
        }
        void Frame(PocketTtsFrame frame)
        {
            lock (audioLock) for (int i = 0; i < frame.dataLength; i++) pcm.Enqueue(frame.data[i]);
        }
        void Read(float[] samples)
        {
            lock (audioLock)
            {
                int count = Math.Min(samples.Length, pcm.Count);
                for (int i = 0; i < count; i++) samples[i] = pcm.Dequeue();
                Array.Clear(samples, count, samples.Length - count);
            }
        }
        async Task Ready(CancellationToken cancellation)
        {
            var time = Stopwatch.StartNew();
            while (engine && (engine.Status == ModelStatus.Loading || engine.Status == ModelStatus.Generate || engine.Status == ModelStatus.Init || !engine.CanUnload || engine.HasBufferedAudio))
            {
                cancellation.ThrowIfCancellationRequested();
                if (time.Elapsed.TotalSeconds > 120) throw new TimeoutException("Local Pocket TTS timed out. Check the Unity console.");
                await Task.Yield();
            }
            cancellation.ThrowIfCancellationRequested();
            if (!engine || engine.Status != ModelStatus.Ready) throw new Exception("Pocket TTS failed to load or generate audio. Check the Unity console.");
        }
        void OnDestroy()
        {
            if (engine) engine.OnFrameGenerated -= Frame;
            if (clip) Destroy(clip);
        }
    }
}
