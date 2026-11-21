using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PocketTTS
{
    public class PocketTTS : MonoBehaviour
    {
        [Header("Language")]
        [Tooltip("Language pack for startup and speech requests without a language code.")]
        public string defaultLanguageCode = "en";
        public string ActiveLanguageCode { get; private set; }

        [Header("Voice")]
        [Tooltip("Optional default voice WAV path. If empty, PocketTTS will keep using the last loaded voice until a new one is passed to Prompt().")]
        public string defaultVoicePath = string.Empty;

        [Header("Codec")]
        public string encoderPath = string.Empty;
        public string decoderPath = string.Empty;

        [Header("Text")]
        public string tokenizerPath = string.Empty;
        public string textConditionerPath = string.Empty;

        [Header("Models")]
        public string flowLmMainPath = string.Empty;
        public string flowLmFlowPath = string.Empty;
        public string bosBeforeVoicePath = string.Empty;

        [Header("Output Frames")]
        [Min(1)]
        public int frameMilliseconds = 40;

        protected PocketTTSModel pocketTTS;
        protected PocketTTSVoices voices;
        protected MimiEncoder encoder;

        public PocketTTSVoices Voices => voices;

        public IEnumerator UnloadModel()
        {
            // The request queue calls this between utterances. Wait for any outstanding voice warmup.
            yield return new WaitWhile(() => pocketTTS.IsBackgroundRunning || encoder.IsBackgroundRunning ||
                voices.IsBackgroundRunning || voices.status == ModelStatus.Loading);
            StopAllCoroutines();
            activePromptCoroutine = null;
            voices.UnloadModel(); encoder.UnloadModel(); pocketTTS.UnloadModel();
            ClearGeneratedAudio(); ActiveLanguageCode = null; Status = ModelStatus.Init;
        }

        private Coroutine activePromptCoroutine;
        private bool discardPlayback;

        public bool HasBufferedAudio => GetQueuedSampleCount() > 0;
        public bool IsAudioGenerationComplete { get; private set; }
        public double GeneratedAudioSeconds => generatedAudioSamples / (double)(SampleRate * Channels);
        private long generatedAudioSamples;
        public bool CanUnload => !pocketTTS.IsBackgroundRunning && !encoder.IsBackgroundRunning &&
            !voices.IsBackgroundRunning && voices.status != ModelStatus.Loading &&
            Status != ModelStatus.Loading && activePromptCoroutine == null;

        public void StopPlayback()
        {
            discardPlayback = true;
            ClearGeneratedAudio();
        }

        private const string StreamingAssetsVoiceRefPath = "kolkata-voices/Connor/dorothy_mayfield.wav";

        public delegate void StatusChangedDelegate(ModelStatus status);
        public event StatusChangedDelegate OnStatusChanged;

        private ModelStatus _status = ModelStatus.Init;

        // Public getter, no public setter
        public ModelStatus Status
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

        // harcoded value from snac decoder
        private const int SampleRate = 24000;
        private const int Channels = 1;

        public event Action<PocketTtsFrame> OnFrameGenerated;

        private ushort sequenceNumber = 0;
        private uint timestamp = 0;
        private readonly Queue<PocketTtsAudioResponse> generatedAudioQueue = new Queue<PocketTtsAudioResponse>();
        private PocketTtsAudioResponse currentAudioResponse;
        private int currentAudioResponseSampleOffset;
        private int currentAudioResponseWordOffset;
        private bool hasCurrentAudioResponse;
        private int queuedAudioSampleCount;
        private float[] frameBuffer = Array.Empty<float>();
        private const int MaxFramesPerUpdate = 8;
        private double nextFrameDispatchTime = -1;
        private bool restartFrameDispatch = true;
        private double promptStartedAt;
        private double generationStartedAt;
        private bool awaitingFirstFrame;

        private void Awake()
        {
            pocketTTS = GetComponentInChildren<PocketTTSModel>();
            voices = GetComponentInChildren<PocketTTSVoices>();
            encoder = GetComponentInChildren<MimiEncoder>();
        }

        private void OnEnable()
        {
            pocketTTS.OnStatusChanged += OnModelStatusChanged;
            voices.OnStatusChanged += OnModelStatusChanged;
            encoder.OnStatusChanged += OnModelStatusChanged;

            pocketTTS.OnResponseGenerated += OnResponseGenerated;
        }

        private void OnDisable()
        {
            pocketTTS.OnStatusChanged -= OnModelStatusChanged;
            voices.OnStatusChanged -= OnModelStatusChanged;
            encoder.OnStatusChanged -= OnModelStatusChanged;

            pocketTTS.OnResponseGenerated -= OnResponseGenerated;
            ClearGeneratedAudio();
        }

        private void Update()
        {
            DispatchGeneratedAudioFrames();
        }

        private void OnValidate()
        {
            frameMilliseconds = Mathf.Max(1, frameMilliseconds);
        }

        void OnModelStatusChanged(ModelStatus status)
        {
            if (status == ModelStatus.Error)
            {
                StopAllCoroutines();
                activePromptCoroutine = null;
                ClearGeneratedAudio();
                this.Status = ModelStatus.Error;
            }
        }

        void OnResponseGenerated(PocketTtsAudioResponse audioResponse)
        {
            if (discardPlayback) return;
            if (audioResponse.Data == null || audioResponse.DataLength <= 0)
            {
                return;
            }

            generatedAudioQueue.Enqueue(audioResponse);
            queuedAudioSampleCount += audioResponse.DataLength;
            generatedAudioSamples += audioResponse.DataLength;
        }

        private void DispatchGeneratedAudioFrames()
        {
            DispatchGeneratedAudioFramesAtTime(Time.realtimeSinceStartupAsDouble);
        }

        private void DispatchGeneratedAudioFramesAtTime(double now)
        {
            int frameSampleCount = GetFrameSampleCount();
            EnsureFrameBuffer(frameSampleCount);

            if (GetQueuedSampleCount() < frameSampleCount)
            {
                // Silence while inference catches up must not become playback debt.
                restartFrameDispatch = true;
                if (Status != ModelStatus.Generate)
                {
                    ClearGeneratedAudio();
                }

                return;
            }

            double interval = frameMilliseconds / 1000.0;
            if (restartFrameDispatch)
            {
                // A refilled queue must still respect the previous packet's duration.
                nextFrameDispatchTime = Math.Max(now, nextFrameDispatchTime);
                restartFrameDispatch = false;
            }

            int dispatched = 0;
            while (now >= nextFrameDispatchTime && GetQueuedSampleCount() >= frameSampleCount &&
                   dispatched < MaxFramesPerUpdate)
            {
                string transcriptText = DequeueFrameSamples(frameSampleCount);

                PocketTtsFrame frame = new PocketTtsFrame(
                    data: frameBuffer,
                    dataLength: frameSampleCount,
                    sampleRateHz: SampleRate,
                    channels: Channels,
                    sequenceNumber: sequenceNumber,
                    timestamp: timestamp,
                    transcriptText: transcriptText
                );

                if (OnFrameGenerated != null)
                {
                    OnFrameGenerated(frame);
                }

                if (awaitingFirstFrame)
                {
                    awaitingFirstFrame = false;
                    Debug.Log($"[SpeechTiming] provider=Pocket language={ActiveLanguageCode} stage=first_audio " +
                        $"requestMs={(now - promptStartedAt) * 1000:F0} generationMs={(now - generationStartedAt) * 1000:F0}");
                }

                unchecked
                {
                    sequenceNumber++;
                    timestamp += (uint)(frameSampleCount / Channels);
                }

                nextFrameDispatchTime += interval;
                dispatched++;
            }

            // Catch up after slow rendering without flooding the voice transport after a long stall.
            // Keep every audio sample; discard only excess wall-clock debt.
            if (GetQueuedSampleCount() < frameSampleCount)
                restartFrameDispatch = true;
            else if (dispatched == MaxFramesPerUpdate && nextFrameDispatchTime <= now)
                nextFrameDispatchTime = now + interval;
        }

        private int GetFrameSampleCount()
        {
            return Mathf.Max(1, SampleRate * Channels * Mathf.Max(1, frameMilliseconds) / 1000);
        }

        private int GetQueuedSampleCount()
        {
            return queuedAudioSampleCount;
        }

        private string DequeueFrameSamples(int frameSampleCount)
        {
            System.Text.StringBuilder transcript = null;
            int frameOffset = 0;
            while (frameOffset < frameSampleCount)
            {
                if (!hasCurrentAudioResponse)
                {
                    currentAudioResponse = generatedAudioQueue.Dequeue();
                    currentAudioResponseSampleOffset = 0;
                    currentAudioResponseWordOffset = 0;
                    hasCurrentAudioResponse = true;
                }

                int availableSamples = currentAudioResponse.DataLength - currentAudioResponseSampleOffset;
                int sampleCount = Mathf.Min(frameSampleCount - frameOffset, availableSamples);
                int responseFrameEnd = currentAudioResponseSampleOffset + sampleCount;
                PocketTtsTranscriptWord[] words = currentAudioResponse.Words;
                while (words != null && currentAudioResponseWordOffset < words.Length &&
                       words[currentAudioResponseWordOffset].SampleOffset < responseFrameEnd)
                {
                    if (words[currentAudioResponseWordOffset].SampleOffset >= currentAudioResponseSampleOffset)
                    {
                        string text = words[currentAudioResponseWordOffset].Text;
                        if (!string.IsNullOrEmpty(text))
                        {
                            transcript ??= new System.Text.StringBuilder();
                            transcript.Append(text);
                        }
                    }

                    currentAudioResponseWordOffset++;
                }

                Array.Copy(currentAudioResponse.Data, currentAudioResponseSampleOffset, frameBuffer, frameOffset, sampleCount);
                frameOffset += sampleCount;
                currentAudioResponseSampleOffset = responseFrameEnd;
                queuedAudioSampleCount -= sampleCount;
                if (currentAudioResponseSampleOffset >= currentAudioResponse.DataLength)
                {
                    hasCurrentAudioResponse = false;
                }
            }

            return transcript == null ? string.Empty : transcript.ToString();
        }

        private void EnsureFrameBuffer(int frameSampleCount)
        {
            if (frameBuffer.Length != frameSampleCount)
            {
                frameBuffer = new float[frameSampleCount];
            }
        }

        //private void OnAudioRead(float[] data)
        //{
        //    for (int i = 0; i < data.Length; i++)
        //    {
        //        if (audioQueue.Count > 0)
        //            data[i] = audioQueue.Dequeue();
        //        else
        //            data[i] = 0f;
        //    }
        //}

        public void InitModel()
        {
            if (_status != ModelStatus.Init)
            {
                Debug.LogError("invalid status");
                return;
            }

            if (!PocketTtsLanguageModels.TryResolve(Application.streamingAssetsPath, defaultLanguageCode,
                out string code, out string directory, out string error))
            {
                Debug.LogError(error, this);
                Status = ModelStatus.Error;
                return;
            }

            SetStreamingAssetsPaths(directory, code);
            Status = ModelStatus.Loading;
            StartCoroutine(RunInitModel(code, defaultVoicePath));
        }

        IEnumerator RunInitModel(string languageCode, string initialVoicePath, bool markReady = true)
        {
            Debug.Log($"[PocketTTS] Loading '{languageCode}' voice synthesis data.");

            pocketTTS.decoderPath = decoderPath;
            pocketTTS.tokenizerPath = tokenizerPath;
            pocketTTS.textConditionerPath = textConditionerPath;
            pocketTTS.flowLmMainPath = flowLmMainPath;
            pocketTTS.flowLmFlowPath = flowLmFlowPath;
            pocketTTS.bosBeforeVoicePath = bosBeforeVoicePath;
            pocketTTS.LanguageCode = languageCode;
            pocketTTS.InitModel();

            encoder.encoderPath = encoderPath;
            encoder.InitModel();

            voices.defaultVoicePath = initialVoicePath;
            voices.InitModel();

            yield return new WaitUntil(() =>
                pocketTTS.status == ModelStatus.Error || encoder.status == ModelStatus.Error || voices.status == ModelStatus.Error ||
                (pocketTTS.status == ModelStatus.Ready && encoder.status == ModelStatus.Ready && voices.status == ModelStatus.Ready));
            if (pocketTTS.status == ModelStatus.Error || encoder.status == ModelStatus.Error || voices.status == ModelStatus.Error)
            {
                Status = ModelStatus.Error;
                yield break;
            }
            yield return new WaitWhile(() => pocketTTS.IsBackgroundRunning || encoder.IsBackgroundRunning || voices.IsBackgroundRunning);

            ActiveLanguageCode = languageCode;
            Debug.Log($"[PocketTTS] Local '{languageCode}' voice synthesis model loaded.");

            if (markReady) Status = ModelStatus.Ready;
        }

        public bool Prompt(string prompt, string voicePath = null, string languageCode = null)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return false;
            }

            if (Status == ModelStatus.Error)
            {
                Debug.LogWarning("Cannot start TTS prompt while model is in error state.");
                return false;
            }

            if (Status != ModelStatus.Ready || activePromptCoroutine != null)
            {
                Debug.LogWarning($"Cannot start TTS prompt while model status is {Status}.");
                return false;
            }

            string requestedCode = PocketTtsLanguageModels.NormalizeCode(
                string.IsNullOrWhiteSpace(languageCode) ? defaultLanguageCode : languageCode);
            string directory = null;
            if (requestedCode != ActiveLanguageCode && !PocketTtsLanguageModels.TryResolve(
                Application.streamingAssetsPath, requestedCode, out requestedCode, out directory, out string error))
            {
                Debug.LogWarning(error, this);
                return false;
            }

            discardPlayback = false;
            generatedAudioSamples = 0;
            IsAudioGenerationComplete = false;
            promptStartedAt = Time.realtimeSinceStartupAsDouble;
            awaitingFirstFrame = true;
            Status = requestedCode == ActiveLanguageCode ? ModelStatus.Generate : ModelStatus.Loading;
            activePromptCoroutine = StartCoroutine(RunPrompt(prompt.Trim(), ResolveVoicePath(voicePath), requestedCode, directory));
            return true;
        }

        IEnumerator RunPrompt(string prompt, string voicePath, string languageCode, string directory)
        {
            if (languageCode != ActiveLanguageCode)
            {
                // Voice warmup can still be encoding after the previous speech has finished.
                yield return new WaitWhile(() => voices.status == ModelStatus.Loading ||
                    voices.IsBackgroundRunning || encoder.IsBackgroundRunning || pocketTTS.IsBackgroundRunning);
                string initialVoicePath = string.IsNullOrEmpty(voicePath) ? voices.currentVoicePath : voicePath;
                if (string.IsNullOrEmpty(initialVoicePath)) initialVoicePath = defaultVoicePath;
                ClearGeneratedAudio();
                voices.UnloadModel();
                encoder.UnloadModel();
                pocketTTS.UnloadModel();
                ActiveLanguageCode = null;
                SetStreamingAssetsPaths(directory, languageCode);
                yield return RunInitModel(languageCode, initialVoicePath, false);
                if (Status == ModelStatus.Error) { activePromptCoroutine = null; yield break; }
                Status = ModelStatus.Generate;
            }

            if (!string.IsNullOrEmpty(voicePath))
            {
                yield return EnsureVoiceLoaded(voicePath);

                if (Status == ModelStatus.Error)
                {
                    activePromptCoroutine = null;
                    yield break;
                }
            }

            yield return WaitForGenerationAndPlaybackDone(prompt);
            activePromptCoroutine = null;
        }

        IEnumerator WaitForGenerationAndPlaybackDone(string prompt)
        {
            if (discardPlayback)
            {
                Status = ModelStatus.Ready;
                yield break;
            }
            VoiceInfo voiceInfo = voices.GetCurrentVoice();
            if (voiceInfo == null)
            {
                Debug.LogError("No voice is loaded.");
                Status = ModelStatus.Ready;
                yield break;
            }

            generationStartedAt = Time.realtimeSinceStartupAsDouble;
            Debug.Log($"[SpeechTiming] provider=Pocket language={ActiveLanguageCode} stage=generation_start " +
                $"prepareMs={(generationStartedAt - promptStartedAt) * 1000:F0}");
            pocketTTS.PromptWithVoice(prompt, voiceInfo);

            yield return new WaitUntil(() => pocketTTS.status == ModelStatus.Ready || pocketTTS.status == ModelStatus.Error);
            yield return new WaitWhile(() => pocketTTS.IsBackgroundRunning);

            Debug.Log($"[SpeechTiming] provider=Pocket language={ActiveLanguageCode} stage=generation_done " +
                $"generationMs={(Time.realtimeSinceStartupAsDouble - generationStartedAt) * 1000:F0} " +
                $"queuedAudioMs={GetQueuedSampleCount() * 1000L / SampleRate}");

            if (pocketTTS.status == ModelStatus.Error)
            {
                ClearGeneratedAudio();
                Status = ModelStatus.Error;
                yield break;
            }
            PadFinalAudioFrame();
            IsAudioGenerationComplete = true;
            yield return new WaitUntil(() => GetQueuedSampleCount() == 0);
            ClearGeneratedAudio();

            Status = ModelStatus.Ready;
        }

        private void PadFinalAudioFrame()
        {
            int remainder = GetQueuedSampleCount() % GetFrameSampleCount();
            if (remainder == 0) return;
            // Deliver the last samples and their transcript words through the same
            // paced transport instead of discarding a packet shorter than 40 ms.
            var padding = new float[GetFrameSampleCount() - remainder];
            OnResponseGenerated(new PocketTtsAudioResponse(padding, padding.Length, null));
        }

        private void ClearGeneratedAudio()
        {
            generatedAudioQueue.Clear();
            currentAudioResponse = default;
            currentAudioResponseSampleOffset = 0;
            currentAudioResponseWordOffset = 0;
            hasCurrentAudioResponse = false;
            queuedAudioSampleCount = 0;
            nextFrameDispatchTime = -1;
            restartFrameDispatch = true;
        }

        private IEnumerator EnsureVoiceLoaded(string voicePath)
        {
            if (voices == null)
            {
                Debug.LogError("PocketTTS voices component is missing.");
                Status = ModelStatus.Error;
                yield break;
            }

            string normalizedVoicePath = NormalizeVoicePath(voicePath);
            if (string.IsNullOrEmpty(normalizedVoicePath))
            {
                yield break;
            }

            while (voices != null)
            {
                if (voices.IsVoiceLoaded(normalizedVoicePath) || voices.TryUseCachedVoice(normalizedVoicePath))
                {
                    yield break;
                }

                if (voices.status == ModelStatus.Loading)
                {
                    yield return new WaitUntil(() => voices == null || voices.status == ModelStatus.Ready || voices.status == ModelStatus.Error);
                    continue;
                }

                Debug.Log($"[PocketTTS] Loading voice reference: {normalizedVoicePath}", this);
                voices.LoadVoice(normalizedVoicePath);
                yield return new WaitUntil(() => voices == null || voices.status == ModelStatus.Ready || voices.status == ModelStatus.Error);

                if (voices == null)
                {
                    yield break;
                }

                if (voices.status == ModelStatus.Error)
                {
                    break;
                }
            }

            if (voices == null || !voices.IsVoiceLoaded(normalizedVoicePath))
            {
                Debug.LogError($"Failed to load voice: {normalizedVoicePath}");
                Status = ModelStatus.Error;
            }
        }

        private void SetStreamingAssetsPaths(string modelPath, string languageCode)
        {
            if (string.IsNullOrEmpty(defaultVoicePath))
            {
                defaultVoicePath = StreamingAssetsVoiceRefPath;
            }
            else
            {
                defaultVoicePath = NormalizeVoicePath(defaultVoicePath);
            }

            encoderPath = Path.Combine(modelPath, languageCode == "he" ? "encoder.onnx" : "mimi_encoder.onnx");
            decoderPath = Path.Combine(modelPath, "mimi_decoder_int8.onnx");
            tokenizerPath = Path.Combine(modelPath, "tokenizer.model");
            textConditionerPath = Path.Combine(modelPath, "text_conditioner.onnx");
            flowLmMainPath = Path.Combine(modelPath, "flow_lm_main_int8.onnx");
            flowLmFlowPath = Path.Combine(modelPath, "flow_lm_flow_int8.onnx");
            bosBeforeVoicePath = Path.Combine(modelPath, "bos_before_voice.npy");
        }

        private string ResolveVoicePath(string voicePath)
        {
            return NormalizeVoicePath(voicePath);
        }

        public static string NormalizeVoicePath(string voicePath)
        {
            if (string.IsNullOrWhiteSpace(voicePath))
            {
                return string.Empty;
            }

            string trimmedPath = voicePath.Trim();

            try
            {
                if (Path.IsPathRooted(trimmedPath))
                {
                    string fullPath = Path.GetFullPath(trimmedPath);
                    string streamingAssetsPath = Path.GetFullPath(Application.streamingAssetsPath);

                    if (IsPathInsideDirectory(fullPath, streamingAssetsPath))
                    {
                        string relativePath = fullPath.Substring(streamingAssetsPath.Length)
                            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        return NormalizeRelativePath(relativePath);
                    }

                    return fullPath.Replace('\\', '/');
                }
            }
            catch (Exception)
            {
            }

            return ResolveVoicePathAlias(NormalizeRelativePath(trimmedPath));
        }

        public static string GetStreamingAssetsFullPath(string relativeOrFullPath)
        {
            string normalizedPath = NormalizeVoicePath(relativeOrFullPath);

            if (string.IsNullOrEmpty(normalizedPath))
            {
                return string.Empty;
            }

            if (Path.IsPathRooted(normalizedPath))
            {
                return Path.GetFullPath(normalizedPath);
            }

            return Path.GetFullPath(Path.Combine(
                Application.streamingAssetsPath,
                normalizedPath.Replace('/', Path.DirectorySeparatorChar)));
        }

        private static string NormalizeRelativePath(string path)
        {
            return path.Replace('\\', '/').TrimStart('/');
        }

        private static string ResolveVoicePathAlias(string normalizedPath)
        {
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return string.Empty;
            }

            if (StreamingAssetsFileExists(normalizedPath))
            {
                return normalizedPath;
            }

            string pathWithExtension = EnsureWavExtension(normalizedPath);
            if (StreamingAssetsFileExists(pathWithExtension))
            {
                return pathWithExtension;
            }

            if (normalizedPath.IndexOf('/') >= 0)
            {
                return normalizedPath;
            }

            string kolkataVoicesPath = $"kolkata-voices/{pathWithExtension}";
            if (StreamingAssetsFileExists(kolkataVoicesPath))
            {
                return kolkataVoicesPath;
            }

            string matchedPath = FindUniqueVoiceMatch(pathWithExtension);
            return string.IsNullOrEmpty(matchedPath) ? normalizedPath : matchedPath;
        }

        private static string EnsureWavExtension(string normalizedPath)
        {
            return Path.HasExtension(normalizedPath)
                ? normalizedPath
                : $"{normalizedPath}.wav";
        }

        private static bool StreamingAssetsFileExists(string normalizedPath)
        {
            string fullPath = BuildStreamingAssetsFullPath(normalizedPath);
            return !string.IsNullOrEmpty(fullPath) && File.Exists(fullPath);
        }

        private static string BuildStreamingAssetsFullPath(string normalizedPath)
        {
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return string.Empty;
            }

            string streamingAssetsPath = Application.streamingAssetsPath;
            if (string.IsNullOrWhiteSpace(streamingAssetsPath))
            {
                return string.Empty;
            }

            return Path.GetFullPath(Path.Combine(
                streamingAssetsPath,
                normalizedPath.Replace('/', Path.DirectorySeparatorChar)));
        }

        private static string FindUniqueVoiceMatch(string fileName)
        {
            try
            {
                string kolkataVoicesDirectory = Path.Combine(Application.streamingAssetsPath, "kolkata-voices");
                if (!Directory.Exists(kolkataVoicesDirectory))
                {
                    return string.Empty;
                }

                string[] matches = Directory.GetFiles(kolkataVoicesDirectory, fileName, SearchOption.AllDirectories);
                if (matches.Length != 1)
                {
                    return string.Empty;
                }

                return NormalizeVoicePath(matches[0]);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static bool IsPathInsideDirectory(string fullPath, string directoryPath)
        {
            string normalizedDirectoryPath = directoryPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);

            return fullPath.Length > normalizedDirectoryPath.Length &&
                   fullPath.StartsWith(normalizedDirectoryPath, StringComparison.OrdinalIgnoreCase) &&
                   (fullPath[normalizedDirectoryPath.Length] == Path.DirectorySeparatorChar ||
                    fullPath[normalizedDirectoryPath.Length] == Path.AltDirectorySeparatorChar);
        }
    }

    public readonly struct PocketTtsFrame
    {
        public readonly float[] data;
        public readonly int dataLength;
        public readonly int sampleRateHz;
        public readonly int channels;
        public readonly ushort sequenceNumber;
        public readonly uint timestamp;
        public readonly string transcriptText;

        public PocketTtsFrame(
            float[] data,
            int dataLength,
            int sampleRateHz,
            int channels,
            ushort sequenceNumber,
            uint timestamp,
            string transcriptText)
        {
            this.data = data;
            this.dataLength = dataLength;
            this.sampleRateHz = sampleRateHz;
            this.channels = channels;
            this.sequenceNumber = sequenceNumber;
            this.timestamp = timestamp;
            this.transcriptText = transcriptText;
        }

        public int SamplesPerChannel
        {
            get { return channels > 0 ? dataLength / channels : 0; }
        }
    }
}
