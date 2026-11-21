using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace PocketTTS
{
    public class PocketTTSVoices : BackgroundRunner
    {
        public MimiEncoder encoder;

        [Tooltip("Optional default voice WAV loaded at startup. Leave empty to load voices on demand.")]
        public string defaultVoicePath = string.Empty;

        public delegate void StatusChangedDelegate(ModelStatus status);
        public event StatusChangedDelegate OnStatusChanged;

        private ModelStatus _status = ModelStatus.Init;
        public ModelStatus status
        {
            get => _status;
            protected set
            {
                if (_status != value)
                {
                    _status = value;
                    OnStatusChanged?.Invoke(_status);
                }
            }
        }

        protected void PostStatus(ModelStatus s)
        {
            unityContext?.Post(_ => status = s, null);
        }

        private readonly Dictionary<string, VoiceInfo> voiceCache = new Dictionary<string, VoiceInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> cachedVoicePaths = new Queue<string>();
        private readonly HashSet<string> failedVoicePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private const int MaxCachedVoices = 8;
        private const int MaxReferenceSeconds = 6;

        private string requestedVoicePath = string.Empty;

        public string RequestedVoicePath => requestedVoicePath;
        public string currentVoicePath { get; private set; } = string.Empty;
        public VoiceInfo currentVoice { get; private set; }

        public bool HasVoiceLoaded => currentVoice != null;

        void OnDestroy()
        {
            BackgroundStopSync();
            FreeModel();
        }

        public void InitModel()
        {
            if (status != ModelStatus.Init)
            {
                return;
            }

            LoadVoice(defaultVoicePath);
        }

        public void LoadVoice(string voicePath)
        {
            string normalizedVoicePath = NormalizeVoicePath(voicePath);
            string fullVoicePath = PocketTTS.GetStreamingAssetsFullPath(normalizedVoicePath);

            if (string.Equals(requestedVoicePath, normalizedVoicePath, StringComparison.OrdinalIgnoreCase) &&
                (status == ModelStatus.Loading || IsVoiceLoaded(normalizedVoicePath)))
            {
                return;
            }

            if (string.IsNullOrEmpty(normalizedVoicePath))
            {
                requestedVoicePath = string.Empty;
                currentVoicePath = string.Empty;
                currentVoice = null;
                status = ModelStatus.Ready;
                return;
            }

            if (status == ModelStatus.Loading)
            {
                // Rapid call transitions can queue a newer voice while an older one is still loading.
                // The caller will re-check once loading settles, so keep this path quiet and let the
                // latest request win on the next pass.
                return;
            }

            requestedVoicePath = normalizedVoicePath;

            if (failedVoicePaths.Contains(normalizedVoicePath))
            {
                status = ModelStatus.Error;
                return;
            }

            if (voiceCache.TryGetValue(normalizedVoicePath, out VoiceInfo cachedVoice))
            {
                currentVoicePath = normalizedVoicePath;
                currentVoice = cachedVoice;
                status = ModelStatus.Ready;
                return;
            }

            status = ModelStatus.Loading;
            StartCoroutine(WaitForEncoderAndLoadVoice(normalizedVoicePath, fullVoicePath));
        }

        public bool IsVoiceLoaded(string voicePath)
        {
            string normalizedVoicePath = NormalizeVoicePath(voicePath);

            return !string.IsNullOrEmpty(normalizedVoicePath) &&
                   status == ModelStatus.Ready &&
                   string.Equals(currentVoicePath, normalizedVoicePath, StringComparison.OrdinalIgnoreCase) &&
                   currentVoice != null;
        }

        public bool IsVoiceLoadInProgress(string voicePath)
        {
            string normalizedVoicePath = NormalizeVoicePath(voicePath);

            return !string.IsNullOrEmpty(normalizedVoicePath) &&
                   status == ModelStatus.Loading &&
                   string.Equals(requestedVoicePath, normalizedVoicePath, StringComparison.OrdinalIgnoreCase);
        }

        public bool TryUseCachedVoice(string voicePath)
        {
            string normalizedVoicePath = NormalizeVoicePath(voicePath);
            if (string.IsNullOrEmpty(normalizedVoicePath))
            {
                return false;
            }

            if (status == ModelStatus.Loading)
            {
                return false;
            }

            if (!voiceCache.TryGetValue(normalizedVoicePath, out VoiceInfo cachedVoice))
            {
                return false;
            }

            requestedVoicePath = normalizedVoicePath;
            currentVoicePath = normalizedVoicePath;
            currentVoice = cachedVoice;
            status = ModelStatus.Ready;
            return true;
        }

        public VoiceInfo GetCurrentVoice()
        {
            return currentVoice;
        }

        private IEnumerator WaitForEncoderAndLoadVoice(string voicePath, string fullVoicePath)
        {
            if (encoder == null)
            {
                Debug.LogError("[PocketTTSVoices] Encoder reference is missing.", this);
                status = ModelStatus.Error;
                yield break;
            }

            if (encoder.status == ModelStatus.Init || encoder.status == ModelStatus.Loading)
            {
                yield return new WaitUntil(() => encoder.status == ModelStatus.Ready || encoder.status == ModelStatus.Error);
            }

            if (encoder.status != ModelStatus.Ready)
            {
                Debug.LogError("[PocketTTSVoices] Encoder failed to initialize before loading a voice.", this);
                status = ModelStatus.Error;
                yield break;
            }

            RunBackground(new VoiceLoadPayload { VoicePath = voicePath, FullVoicePath = fullVoicePath }, RunLoadVoice);
        }

        private void RunLoadVoice(VoiceLoadPayload payload, CancellationToken cts)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(payload.VoicePath))
                {
                    currentVoicePath = string.Empty;
                    currentVoice = null;
                    PostStatus(ModelStatus.Ready);
                    return;
                }

                if (!File.Exists(payload.FullVoicePath))
                {
                    throw new FileNotFoundException($"Voice WAV file does not exist: {payload.VoicePath}");
                }

                cts.ThrowIfCancellationRequested();
                var memoryBefore = ReadProcessMemory();
                var timer = System.Diagnostics.Stopwatch.StartNew();
                // Mimi's attention allocation grows with reference length; cap before WAV allocation/resampling.
                float[] audioData = WavReader.LoadWav(payload.FullVoicePath, 24000, MaxReferenceSeconds);
                VoiceInfo voiceInfo = encoder.Encode(audioData);
                cts.ThrowIfCancellationRequested();

                CacheVoice(payload.VoicePath, voiceInfo);
                currentVoicePath = payload.VoicePath;
                currentVoice = voiceInfo;

                var memoryAfter = ReadProcessMemory();
                Debug.Log($"[SpeechVoice] provider=Pocket referenceMs={audioData.Length * 1000L / 24000} " +
                    $"elapsedMs={timer.ElapsedMilliseconds} memoryBeforeMiB={memoryBefore.privateBytes / 1048576} " +
                    $"memoryAfterMiB={memoryAfter.privateBytes / 1048576} peakWorkingSetMiB={memoryAfter.peakWorkingSetBytes / 1048576}");

                PostStatus(ModelStatus.Ready);
            }
            catch (OperationCanceledException)
            {
                currentVoicePath = string.Empty;
                currentVoice = null;
                PostStatus(ModelStatus.Ready);
            }
            catch (Exception e)
            {
                failedVoicePaths.Add(payload.VoicePath);
                currentVoicePath = string.Empty;
                currentVoice = null;
                Debug.LogError(e, this);
                PostStatus(ModelStatus.Error);
            }
        }

        private void CacheVoice(string path, VoiceInfo voice)
        {
            if (!voiceCache.ContainsKey(path)) cachedVoicePaths.Enqueue(path);
            voiceCache[path] = voice;
            while (cachedVoicePaths.Count > MaxCachedVoices) voiceCache.Remove(cachedVoicePaths.Dequeue());
        }

        private static (long privateBytes, long peakWorkingSetBytes) ReadProcessMemory()
        {
            try
            {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
                // Unity's Windows Mono Process memory properties can return zero.
                uint size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ProcessMemoryCounters>();
                if (GetProcessMemoryInfo(new IntPtr(-1), out ProcessMemoryCounters counters, size))
                    return ((long)counters.PrivateUsage.ToUInt64(), (long)counters.PeakWorkingSetSize.ToUInt64());
#endif
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                return (process.PrivateMemorySize64, process.PeakWorkingSet64);
            }
            catch (Exception) { return (0, 0); } // Diagnostics must never interrupt speech.
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct ProcessMemoryCounters
        {
            public uint Size, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
        }

        [System.Runtime.InteropServices.DllImport("psapi.dll")]
        private static extern bool GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCounters counters, uint size);
#endif

        private void FreeModel()
        {
            voiceCache.Clear();
            cachedVoicePaths.Clear();
            failedVoicePaths.Clear();
            requestedVoicePath = string.Empty;
            currentVoicePath = string.Empty;
            currentVoice = null;
        }

        public void UnloadModel()
        {
            StopAllCoroutines();
            BackgroundStopSync();
            FreeModel();
            status = ModelStatus.Init;
        }

        private static string NormalizeVoicePath(string voicePath)
        {
            return PocketTTS.NormalizeVoicePath(voicePath);
        }

        private class VoiceLoadPayload : IBackgroundPayload
        {
            public string VoicePath;
            public string FullVoicePath;
        }
    }

    public class VoiceInfo
    {
        public float[] Data;
        public long[] Shape;
    }
}
