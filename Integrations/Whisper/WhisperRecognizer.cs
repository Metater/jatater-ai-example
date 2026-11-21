using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Whisper;

namespace JataterAI.WhisperIntegration
{
    public sealed class WhisperRecognizer : IRecognizer
    {
        readonly WhisperManager manager;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Register() => Adapters.Whisper = (host, model, language) => new WhisperRecognizer(host, model, language);

        WhisperRecognizer(GameObject host, string model, string language)
        {
            if (!File.Exists(Path.Combine(Application.streamingAssetsPath, model)))
                throw new FileNotFoundException("Download the Whisper model in Tools > Jatater AI > Setup or select an installed model.", model);
            var obj = new GameObject("Local Whisper");
            obj.SetActive(false); obj.transform.SetParent(host.transform, false);
            manager = obj.AddComponent<WhisperManager>();
            manager.ModelPath = model;
            manager.language = language;
            obj.SetActive(true);
        }

        public async Task<string> TranscribeAsync(float[] pcm, int rate, int channels, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var result = await manager.GetTextAsync(pcm, rate, channels);
            cancellation.ThrowIfCancellationRequested();
            if (result == null) throw new Exception("Whisper could not transcribe this turn. Verify the model and native runtime in the Unity console.");
            return result.Result;
        }
    }
}
