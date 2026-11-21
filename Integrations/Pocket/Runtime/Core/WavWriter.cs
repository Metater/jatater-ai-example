using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace PocketTTS
{
    [RequireComponent(typeof(global::PocketTTS.PocketTTS))]
    public class WavWriter : MonoBehaviour
    {
        public string wavFileFolder = string.Empty;

        private global::PocketTTS.PocketTTSModel tts;
        private readonly List<float> response = new List<float>();
        private readonly object responseLock = new object();

        private void Awake()
        {
            tts = GetComponentInChildren<global::PocketTTS.PocketTTSModel>();
        }

        private void OnEnable()
        {
            if (tts == null)
            {
                tts = GetComponentInChildren<global::PocketTTS.PocketTTSModel>();
            }

            if (tts != null)
            {
                tts.OnStatusChanged += OnStatusChanged;
                tts.OnResponseGenerated += OnAudioChunkGenerated;
            }
        }

        private void OnDisable()
        {
            if (tts != null)
            {
                tts.OnStatusChanged -= OnStatusChanged;
                tts.OnResponseGenerated -= OnAudioChunkGenerated;
            }
        }

        private void OnStatusChanged(ModelStatus status)
        {
            if (status == ModelStatus.Error)
            {
                lock (responseLock)
                {
                    response.Clear();
                }

                return;
            }

            if (status == ModelStatus.Generate)
            {
                lock (responseLock)
                {
                    response.Clear();
                }

                return;
            }

            if (status != ModelStatus.Ready)
            {
                return;
            }

            float[] buffer;
            lock (responseLock)
            {
                if (response.Count == 0)
                {
                    return;
                }

                buffer = response.ToArray();
                response.Clear();
            }

            string folder = string.IsNullOrWhiteSpace(wavFileFolder)
                ? Application.persistentDataPath
                : wavFileFolder;
            Directory.CreateDirectory(folder);

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fffffff");
            string fileName = $"generated_{timestamp}.wav";
            string fullPath = Path.Combine(folder, fileName);

            SaveWav(fullPath, buffer, 24000);
            Debug.Log($"{fullPath} saved");
        }

        private void OnAudioChunkGenerated(PocketTtsAudioResponse responseChunk)
        {
            float[] samples = responseChunk.Data;
            if (samples == null || responseChunk.DataLength <= 0)
            {
                return;
            }

            lock (responseLock)
            {
                for (int i = 0; i < Math.Min(samples.Length, responseChunk.DataLength); i++) response.Add(samples[i]);
            }
        }

        public static void SaveWav(string filePath, float[] samples, int sampleRate)
        {
            using (var stream = File.Create(filePath))
            using (var writer = new BinaryWriter(stream))
            {
                int dataSize = samples.Length * sizeof(short);

                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + dataSize);
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));

                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16);
                writer.Write((short)1); // PCM
                writer.Write((short)1); // Mono
                writer.Write(sampleRate);
                writer.Write(sampleRate * sizeof(short));
                writer.Write((short)sizeof(short));
                writer.Write((short)16);

                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(dataSize);

                foreach (var sample in samples)
                {
                    short pcm = (short)Mathf.Clamp(Mathf.RoundToInt(sample * short.MaxValue), short.MinValue, short.MaxValue);
                    writer.Write(pcm);
                }
            }
        }
    }
}
