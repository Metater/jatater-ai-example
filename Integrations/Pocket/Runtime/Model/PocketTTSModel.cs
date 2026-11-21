using Microsoft.ML.OnnxRuntime;
using PocketTts;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

[assembly: InternalsVisibleTo("PocketTTS.Tests")]

namespace PocketTTS
{
    public class PocketTTSModel : BackgroundRunner
    {
        [Header("Models")]
        public string tokenizerPath = string.Empty;
        public string textConditionerPath = string.Empty;
        public string flowLmMainPath = string.Empty;
        public string flowLmFlowPath = string.Empty;
        public string decoderPath = string.Empty;
        public string bosBeforeVoicePath = string.Empty;

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

        void OnDestroy()
        {
            BackgroundStopSync();
            FreeModel();
        }

        private InferenceSession _textConditioner;
        private InferenceSession _flowLmMain;
        private InferenceSession _flowLmFlow;
        private InferenceSession _mimiDecoder;
        private SentencePieceWrapper _sentencePiece;
        private HebrewPocketModel _hebrew;
        private float[] _bosData;
        private long[] _bosShape;

        #region Init / Free

        public void UnloadModel()
        {
            BackgroundStopSync();
            FreeModel();
            status = ModelStatus.Init;
        }

        public void InitModel()
        {
            if (status != ModelStatus.Init)
                return;

            status = ModelStatus.Loading;
            RunBackground(RunInitModel);
        }

        void RunInitModel(CancellationToken cts)
        {
            try
            {
                _sentencePiece = new SentencePieceWrapper(tokenizerPath);

                using var lightOpt = TensorUtil.GetMobileSessionOptions(1); // One thread for fast stages
                using var mimiOpt = TensorUtil.GetMobileSessionOptions(2);  // Pin to the 2 Big Cores (A76) on G99

                if (LanguageCode == "he")
                {
                    _hebrew = new HebrewPocketModel(System.IO.Path.GetDirectoryName(tokenizerPath), mimiOpt, _sentencePiece);
                    PostStatus(ModelStatus.Ready);
                    return;
                }
                _textConditioner = new InferenceSession(textConditionerPath, lightOpt);
                _flowLmMain = new InferenceSession(flowLmMainPath, lightOpt);
                _flowLmFlow = new InferenceSession(flowLmFlowPath, lightOpt);
                _mimiDecoder = new InferenceSession(decoderPath, mimiOpt);

                if (!string.IsNullOrEmpty(bosBeforeVoicePath) && System.IO.File.Exists(bosBeforeVoicePath))
                {
                    var bos = NpyUtil.ParseNpyFloat32(System.IO.File.ReadAllBytes(bosBeforeVoicePath));
                    _bosData = bos.data;
                    _bosShape = bos.shape;
                }

                PostStatus(ModelStatus.Ready);
            }
            catch (Exception e)
            {
                Debug.LogError(e);
                FreeModel();
                PostStatus(ModelStatus.Error);
            }
        }

        void FreeModel()
        {
            _hebrew?.Dispose();
            _hebrew = null;
            _bosData = null;
            _bosShape = null;
            _textConditioner?.Dispose();
            _textConditioner = null;

            _flowLmMain?.Dispose();
            _flowLmMain = null;

            _flowLmFlow?.Dispose();
            _flowLmFlow = null;

            _mimiDecoder?.Dispose();
            _mimiDecoder = null;

            _sentencePiece?.Dispose();
            _sentencePiece = null;
        }

        #endregion

        #region Prompt

        private class PromptPayload : IBackgroundPayload
        {
            public string Prompt;
            public VoiceInfo Voice;
        }

        public void PromptWithVoice(string prompt, VoiceInfo voice)
        {
            if (string.IsNullOrEmpty(prompt))
                return;

            if (status != ModelStatus.Ready)
                return;

            status = ModelStatus.Generate;
            RunBackground(new PromptPayload { Prompt = prompt, Voice = voice }, RunPrompt);
        }

        [Header("Config")]
        public int MaxFrames = 500;
        public int DiffusionStep = 10;
        public int AudioChunkSize = 12;
        public float Temperature = 0.7f;
        public string LanguageCode = "en";
        public float ChunkGapSec = 0.25f; // Exposed
        public bool EnableProfiling = true;

        const int FRAMES_AFTER_EOS = 3;
        const int SILENCE_POST_CHUNK_SAMPLES = PauseTagParser.SampleRate;
        const int BOUNDARY_FADE_SAMPLES = PauseTagParser.SampleRate / 100; // 10 ms
        const float ROOM_TONE_LEVEL = 0.0004f;

        // A deterministic, low-passed room tone is much less fatiguing than white noise.
        // It is generated once and reused for every explicit or inter-chunk pause.
        static readonly float[] RoomTone = CreateRoomTone();
        float _lastPostedSample;

        private struct SynthesisItem
        {
            public int Step;
            public float[] Conditioning;
            public float[] Noise;
            public bool IsFinal;
        }

        void RunPrompt(PromptPayload payload, CancellationToken cts)
        {
            _lastPostedSample = 0f;
            if (_hebrew != null)
            {
                try
                {
                    foreach (var segment in PauseTagParser.Parse(payload.Prompt))
                    {
                        cts.ThrowIfCancellationRequested();
                        if (segment.IsPause) { PostSilence(segment.PauseSamples, cts); continue; }
                        var words = TranscriptTagParser.CreateTranscriptWords(segment.Text);
                        int nextWord = 0;
                        _hebrew.Speak(TranscriptTagParser.StripTagsForSpeech(segment.Text), payload.Voice, MaxFrames, audio =>
                            PostAudioResponse(audio, SpeechTranscriptTiming.TakeTranscriptWords(words, ref nextWord, audio.Length, false)), cts);
                        var tail = CreateFinalAudioResponse(null, words, ref nextWord);
                        if (tail.DataLength > 0) PostAudioResponse(tail.Data, tail.Words);
                    }
                    PostStatus(ModelStatus.Ready);
                }
                catch (OperationCanceledException) { PostStatus(ModelStatus.Ready); }
                catch (Exception error) { Debug.LogError(error); PostStatus(ModelStatus.Error); }
                return;
            }
            using var runOptions = new RunOptions();
            Dictionary<string, OrtValue> flowState = null;
            Dictionary<string, OrtValue> mimiState = null;
            var stPairs = new List<STPair>();
            System.Random rand = new();

            // Pipelining: Synthesis Queue, Potential Buffer Pools, and Feedback
            using var synthQueue = new BlockingCollection<SynthesisItem>(new ConcurrentQueue<SynthesisItem>());
            using var latentFeedbackQueue = new BlockingCollection<float[]>(new ConcurrentQueue<float[]>());
            using var audioQueue = new BlockingCollection<KeyValuePair<int, float[]>>(new ConcurrentQueue<KeyValuePair<int, float[]>>());
            Task synthTask = null;
            Task decoderTask = null;

            var bufferPool = new ConcurrentStack<float[]>();
            var noisePool = new ConcurrentStack<float[]>();

            // Clear previous session state
            _decoderInputs = null;
            _decoderStateOutputIndices = null;
            _decoderStateKeys = null;
            // Diagnostic accumulators
            long totalArMs = 0, totalFlowMs = 0, totalMimiMs = 0;
            int framesProcessed = 0;

            // Real-time analysis
            var frameBirthTimes = new Dictionary<int, long>();
            System.Diagnostics.Stopwatch lifeStopwatch = System.Diagnostics.Stopwatch.StartNew();
            System.Diagnostics.Stopwatch gapStopwatch = new();
            gapStopwatch.Start();

            Func<int, float[]> getCondBuffer = (size) => bufferPool.TryPop(out var b) && b.Length >= size ? b : new float[size];
            Func<float[]> getNoiseBuffer = () => noisePool.TryPop(out var b) ? b : new float[32];

            Action<float[]> returnCondBuffer = (b) => bufferPool.Push(b);
            Action<float[]> returnNoiseBuffer = (b) => noisePool.Push(b);

            // Set high priority for this thread
            Thread.CurrentThread.Priority = System.Threading.ThreadPriority.AboveNormal;

            // Pre-allocate buffers for inference
            float[] currentLatentData = new float[32];
            float[] xData = new float[32];
            long[] currentLatentShape = { 1, 1, 32 };
            long[] xShape = { 1, 32 };

            // Adaptive conditioning size
            int condDim = 32;

            // Pre-generate Gaussian noise for the entire sequence to avoid Box-Muller in hot loop
            int noiseCount = MaxFrames * 32; // 500 * 32
            float[] noiseBuffer = new float[noiseCount];
            for (int n = 0; n < noiseCount; n += 2)
            {
                double u1 = 1.0 - rand.NextDouble();
                double u2 = 1.0 - rand.NextDouble();
                double radius = Math.Sqrt(-2.0 * Math.Log(u1));
                double theta = 2.0 * Math.PI * u2;
                noiseBuffer[n] = (float)(radius * Math.Cos(theta));
                if (n + 1 < noiseCount)
                    noiseBuffer[n + 1] = (float)(radius * Math.Sin(theta));
            }
            int diffusionSteps = LanguageCode == "zh" ? 1 : DiffusionStep;
            float noiseStd = (float)Math.Sqrt(LanguageCode == "zh" ? 0.3f : Temperature);
            for (int n = 0; n < noiseCount; n++) noiseBuffer[n] *= noiseStd;

            // Persistent dictionaries for inputs to avoid per-step allocations
            Dictionary<string, OrtValue> mainInputs = null;
            Dictionary<string, OrtValue> flowInputs = null;

            // Pin buffers to create persistent OrtValue tensors
            GCHandle latentHandle = GCHandle.Alloc(currentLatentData, GCHandleType.Pinned);
            GCHandle xHandle = GCHandle.Alloc(xData, GCHandleType.Pinned);

            OrtValue currentLatent = null;
            OrtValue xTensor = null;

            try
            {
                currentLatent = OrtValue.CreateTensorValueFromMemory<float>(
                    OrtMemoryInfo.DefaultInstance, currentLatentData, currentLatentShape);
                xTensor = OrtValue.CreateTensorValueFromMemory<float>(
                    OrtMemoryInfo.DefaultInstance, xData, xShape);

                for (int i = 0; i < diffusionSteps; i++)
                {
                    stPairs.Add(new STPair((float)i / diffusionSteps, (float)(i + 1) / diffusionSteps));
                }

                flowState = InitFlowLmState();
                mimiState = InitMimiDecoderState();

                using var emptySeq = TensorUtil.CreateEmptyFloatTensor(new long[] { 1, 0, 32 });
                using var emptyText = TensorUtil.CreateEmptyFloatTensor(new long[] { 1, 0, 1024 });

                float[] preppedVoiceData = payload.Voice.Data;
                long[] preppedVoiceShape = payload.Voice.Shape;

                if (_bosData != null)
                {
                    (preppedVoiceData, preppedVoiceShape) = PrependBosBeforeVoice(payload.Voice.Data, payload.Voice.Shape);
                }

                using var voiceTensor = OrtValue.CreateTensorValueFromMemory<float>(
                    OrtMemoryInfo.DefaultInstance, preppedVoiceData, preppedVoiceShape);

                // Helper to build/reset flow state conditioned on voice
                Action BuildVoiceConditionedState = () =>
                {
                    TensorUtil.DisposeState(flowState);
                    flowState = InitFlowLmState();

                    var voiceInputs = new Dictionary<string, OrtValue>(flowState);
                    voiceInputs["sequence"] = emptySeq;
                    voiceInputs["text_embeddings"] = voiceTensor;

                    using (var res = _flowLmMain.Run(runOptions, voiceInputs, _flowLmMain.OutputNames))
                    {
                        TensorUtil.UpdateState(flowState, res, _flowLmMain.OutputNames);
                    }
                };

                // Helper to reset mimi state
                Action ResetMimiState = () =>
                {
                    TensorUtil.DisposeState(mimiState);
                    mimiState = InitMimiDecoderState();
                    // CRITICAL: Invalidate the cached binding dictionary since the state tensors are new
                    _decoderInputs = null;
                };

                // Initial conditioning
                BuildVoiceConditionedState();

                List<PromptSegment> promptSegments = BuildPromptSegments(payload.Prompt);
                bool hasSynthesizedSpeech = false;
                for (int segmentIndex = 0; segmentIndex < promptSegments.Count; segmentIndex++)
                {
                    if (cts.IsCancellationRequested) break;

                    PromptSegment promptSegment = promptSegments[segmentIndex];
                    if (promptSegment.IsPause)
                    {
                        PostSilence(promptSegment.PauseSamples, cts);
                        continue;
                    }

                    Queue<string> transcriptWords = TranscriptTagParser.CreateTranscriptWords(promptSegment.TranscriptText);
                    int samplesUntilNextTranscriptWord = 0;

                    // --- State Resets ---
                    // "RESET_FLOW_STATE_EACH_CHUNK is always true"
                    // "RESET_MIMI_STATE_EACH_CHUNK is always true"
                    if (hasSynthesizedSpeech)
                    {
                        //Debug.Log("Reset");
                        BuildVoiceConditionedState();
                        ResetMimiState();
                    }

                    var ids = EncodeToIds(promptSegment.Text).ToArray();
                    using var tokenTensor = OrtValue.CreateTensorValueFromMemory<long>(
                        OrtMemoryInfo.DefaultInstance, ids, new long[] { 1, ids.Length });

                    using var textRes = _textConditioner.Run(
                        runOptions,
                        new Dictionary<string, OrtValue> { { "token_ids", tokenTensor } },
                        _textConditioner.OutputNames);

                    // Update mainInputs for text conditioning of this chunk
                    // Re-create mainInputs from current flowState (since it might have been reset)
                    mainInputs = new Dictionary<string, OrtValue>(flowState);
                    mainInputs["sequence"] = emptySeq;
                    mainInputs["text_embeddings"] = textRes[0];

                    using (var res = _flowLmMain.Run(runOptions, mainInputs, _flowLmMain.OutputNames))
                    {
                        TensorUtil.UpdateState(flowState, res, _flowLmMain.OutputNames);
                    }

                    // Reset latent buffer for new chunk
                    for (int i = 0; i < 32; i++) currentLatentData[i] = float.NaN;

                    int eosStep = -1, consecutiveEosFrames = 0;

                    flowInputs = new Dictionary<string, OrtValue>();

                    Exception synthException = null;
                    Exception decoderException = null;

                    // Stage 2: Synthesis Worker (Pipelined)
                    synthTask = Task.Run(() =>
                    {
                        using var workerRunOptions = new RunOptions();
                        var workerFlowInputs = new Dictionary<string, OrtValue>();
                        float[] workerXData = new float[32];
                        long[] workerXShape = { 1, 32 };
                        GCHandle workerXHandle = GCHandle.Alloc(workerXData, GCHandleType.Pinned);

                        using var workerXTensor = OrtValue.CreateTensorValueFromMemory<float>(
                            OrtMemoryInfo.DefaultInstance, workerXData, workerXShape);

                        // Set worker to AboveNormal (Mimi will be Highest)
                        Thread.CurrentThread.Priority = System.Threading.ThreadPriority.AboveNormal;

                        try
                        {
                            foreach (var item in synthQueue.GetConsumingEnumerable(cts))
                            {
                                if (item.IsFinal) break;

                                // 1. Flow Matching
                                Array.Copy(item.Noise, workerXData, 32);
                                float dt = 1.0f / diffusionSteps;

                                using var condTensor = OrtValue.CreateTensorValueFromMemory<float>(
                                    OrtMemoryInfo.DefaultInstance, item.Conditioning, new long[] { item.Conditioning.Length });

                                workerFlowInputs["c"] = condTensor;
                                System.Diagnostics.Stopwatch flowSw = System.Diagnostics.Stopwatch.StartNew();
                                for (int j = 0; j < diffusionSteps; j++)
                                {
                                    workerFlowInputs["s"] = stPairs[j].S;
                                    workerFlowInputs["t"] = stPairs[j].T;
                                    workerFlowInputs["x"] = workerXTensor;

                                    using var flowRes = _flowLmFlow.Run(workerRunOptions, workerFlowInputs, _flowLmFlow.OutputNames);
                                    var v = flowRes[0].GetTensorDataAsSpan<float>();

                                    int k = 0;
                                    if (Vector.IsHardwareAccelerated && (32 % Vector<float>.Count == 0))
                                    {
                                        int vectorSize = Vector<float>.Count;
                                        Vector<float> dtVec = new Vector<float>(dt);
                                        var vVectors = MemoryMarshal.Cast<float, Vector<float>>(v);
                                        var xVectors = MemoryMarshal.Cast<float, Vector<float>>(workerXData.AsSpan());
                                        for (int vIdx = 0; vIdx < vVectors.Length; vIdx++)
                                            xVectors[vIdx] = xVectors[vIdx] + (vVectors[vIdx] * dtVec);
                                        k = vVectors.Length * vectorSize;
                                    }
                                    for (; k < 32; k++)
                                        workerXData[k] += v[k] * dt;
                                }
                                if (EnableProfiling) totalFlowMs += flowSw.ElapsedMilliseconds;

                                // Pass REFINED latent back to producer for next AR step
                                var refinedLatent = new float[32];
                                Array.Copy(workerXData, refinedLatent, 32);
                                latentFeedbackQueue.Add(refinedLatent);

                                // 2. Queue for asynchronous decoding
                                audioQueue.Add(new KeyValuePair<int, float[]>(item.Step, refinedLatent));

                                // Return buffers to pool
                                returnCondBuffer(item.Conditioning);
                                returnNoiseBuffer(item.Noise);
                            }
                        }
                        catch (Exception e)
                        {
                            synthException = e;
                            Debug.LogError($"[TTS Synth Error] {e}");
                        }
                        finally
                        {
                            audioQueue.Add(new KeyValuePair<int, float[]>(0, null)); // Signal EOF to decoder
                            if (workerXHandle.IsAllocated) workerXHandle.Free();
                        }
                    }, cts);

                    // Stage 3: Asynchronous Decoder Worker
                    decoderTask = Task.Run(() =>
                    {
                        var workerChunk = new List<float[]>();
                        // Set decoder to HIGHEST priority - it's the biggest bottleneck
                        Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Highest;

                        try
                        {
                            foreach (var item in audioQueue.GetConsumingEnumerable(cts))
                            {
                                if (item.Value == null) break;
                                workerChunk.Add(item.Value);

                                if (workerChunk.Count >= AudioChunkSize)
                                {
                                    System.Diagnostics.Stopwatch mimiSw = System.Diagnostics.Stopwatch.StartNew();
                                    float[] audioChunk = DecodeChunk(workerChunk, ref mimiState, _mimiDecoder);
                                    if (EnableProfiling)
                                    {
                                        long now = lifeStopwatch.ElapsedMilliseconds;
                                        totalMimiMs += mimiSw.ElapsedMilliseconds;
                                        long gap = gapStopwatch.ElapsedMilliseconds;

                                        // RELIABLE MATH: Use actual sample count from the decoder
                                        float producedMs = (audioChunk.Length / 24000.0f) * 1000.0f;
                                        float rtRatio = producedMs / gap;

                                        // End-to-end latency for this chunk (from birth of first frame in chunk)
                                        long firstFrameBirth = frameBirthTimes.TryGetValue(item.Key - (AudioChunkSize - 1), out var b) ? b : 0;
                                        long latency = now - firstFrameBirth;

                                        string color = rtRatio >= 1.0f ? "green" : "red";
                                        string status = rtRatio >= 1.0f ? "HEALTHY" : "LAGGING";

                                        Debug.Log($"<color={color}>[TTS RT] Ratio: {rtRatio:F2}x ({status}) | Produced: {producedMs:F0}ms | Gap: {gap}ms | Latency: {latency}ms</color>");
                                        if (gap > producedMs + 5) Debug.Log($"<color=orange>[TTS GAP] Stutter detected! Gap ({gap}ms) > Audio ({producedMs:F0}ms)</color>");
                                    }

                                    PostAudioResponse(audioChunk, SpeechTranscriptTiming.TakeTranscriptWords(
                                        transcriptWords,
                                        ref samplesUntilNextTranscriptWord,
                                        audioChunk.Length,
                                        false));
                                    gapStopwatch.Restart();
                                    workerChunk.Clear();
                                }
                            }

                            // EOF can land exactly on a full decoder chunk. The
                            // remaining transcript still needs a final timing flush.
                            PocketTtsAudioResponse finalResponse = CreateFinalAudioResponse(
                                workerChunk.Count > 0 ? DecodeChunk(workerChunk, ref mimiState, _mimiDecoder) : null,
                                transcriptWords, ref samplesUntilNextTranscriptWord);
                            if (finalResponse.DataLength > 0)
                            {
                                PostAudioResponse(finalResponse.Data, finalResponse.Words);
                            }
                        }
                        catch (Exception e)
                        {
                            decoderException = e;
                            Debug.LogError($"[TTS Decoder Error] {e}");
                        }
                    }, cts);

                    // Stage 1: AR Generator (Producer)
                    mainInputs = new Dictionary<string, OrtValue>(flowState);
                    mainInputs["sequence"] = currentLatent;
                    mainInputs["text_embeddings"] = emptyText;

                    for (int step = 0; step < MaxFrames; step++)
                    {
                        if (cts.IsCancellationRequested) break;

                        System.Diagnostics.Stopwatch arSw = System.Diagnostics.Stopwatch.StartNew();
                        using var arRes = _flowLmMain.Run(runOptions, mainInputs, _flowLmMain.OutputNames);
                        if (EnableProfiling) totalArMs += arSw.ElapsedMilliseconds;

                        var arOutputSpan = arRes[0].GetTensorDataAsSpan<float>();
                        if (step == 0) condDim = arOutputSpan.Length;

                        float eos = arRes[1].GetTensorDataAsSpan<float>()[0];
                        if (eosStep < 0 && (LanguageCode == "zh" ? eos > 0f : ReachedEndOfSpeech(step, eos, ref consecutiveEosFrames)))
                            eosStep = step;

                        // Package conditioning and noise, then ship to worker
                        var synthItem = new SynthesisItem
                        {
                            Step = step,
                            Conditioning = getCondBuffer(condDim),
                            Noise = getNoiseBuffer(),
                            IsFinal = false
                        };

                        arOutputSpan.CopyTo(synthItem.Conditioning);

                        int noiseIdx = step * 32;
                        Array.Copy(noiseBuffer, noiseIdx, synthItem.Noise, 0, 32);

                        synthQueue.Add(synthItem);

                        // WAIT for refined latent before next AR step
                        if (latentFeedbackQueue.TryTake(out var refined, 2000, cts))
                        {
                            Array.Copy(refined, currentLatentData, 32);
                        }

                        if (EnableProfiling) frameBirthTimes[step] = lifeStopwatch.ElapsedMilliseconds;

                        if (eosStep >= 0 && step >= eosStep + FRAMES_AFTER_EOS)
                            break;

                        TensorUtil.UpdateState(flowState, arRes, _flowLmMain.OutputNames);

                        framesProcessed++;
                        if (EnableProfiling && framesProcessed % 20 == 0)
                        {
                            Debug.Log($"[TTS Stats] AR: {totalArMs / framesProcessed}ms | Flow: {totalFlowMs / framesProcessed}ms | Mimi: {totalMimiMs / (framesProcessed / AudioChunkSize)}ms");
                        }
                    }

                    synthQueue.Add(new SynthesisItem { IsFinal = true });
                    // A slow decoder still owns mimiState; never reset it while native work is running.
                    Task.WaitAll(synthTask, decoderTask);

                    if (synthException != null) throw synthException;
                    if (decoderException != null) throw decoderException;

                    hasSynthesizedSpeech = true;

                    // --- Silence Gap ---
                    if (segmentIndex < promptSegments.Count - 1 && !promptSegments[segmentIndex + 1].IsPause)
                    {
                        int gapSamples = (int)(PauseTagParser.SampleRate * ChunkGapSec);
                        PostSilence(gapSamples, cts);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[TTS CRITICAL] {e}");
            }
            finally
            {
                // The producer can fail before sending EOF. Join both workers before freeing their tensors.
                CompleteWorker(synthQueue, synthTask);
                CompleteWorker(audioQueue, decoderTask);
                foreach (var st in stPairs) st.Dispose();
                stPairs.Clear();
                currentLatent?.Dispose();
                xTensor?.Dispose();

                if (latentHandle.IsAllocated) latentHandle.Free();
                if (xHandle.IsAllocated) xHandle.Free();

                FreeDecoderResources();

                TensorUtil.DisposeState(flowState);
                TensorUtil.DisposeState(mimiState);

                bufferPool.Clear();
                noisePool.Clear();
                frameBirthTimes.Clear();

                PostStatus(ModelStatus.Ready);
            }
        }

        #endregion

        #region Helpers

        internal static void CompleteWorker<T>(BlockingCollection<T> input, Task worker)
        {
            input.CompleteAdding();
            try { worker?.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            catch (Exception error) { Debug.LogError($"[TTS Worker Cleanup] {error}"); }
        }

        internal static bool ReachedEndOfSpeech(int step, float eos, ref int consecutiveFrames)
        {
            // Some language packs spike at startup or mid-sentence. Require a clear or sustained end.
            consecutiveFrames = step >= 8 && eos > -3f ? consecutiveFrames + 1 : 0;
            return step >= 8 && (eos > 0f || consecutiveFrames >= 10);
        }

        internal static PocketTtsAudioResponse CreateFinalAudioResponse(
            float[] audio, Queue<string> transcriptWords, ref int samplesUntilNextWord)
        {
            // A short silent tail carries any words whose estimated timings ran
            // beyond the last full audio chunk (including group-ending digits).
            if (audio == null || audio.Length == 0)
                audio = transcriptWords.Count > 0 ? new float[PauseTagParser.SampleRate / 100] : Array.Empty<float>();
            return new PocketTtsAudioResponse(audio, audio.Length,
                SpeechTranscriptTiming.TakeTranscriptWords(transcriptWords,
                    ref samplesUntilNextWord, audio.Length, true));
        }

        List<PromptSegment> BuildPromptSegments(string prompt)
        {
            List<PromptSegment> parsedSegments = PauseTagParser.Parse(prompt);
            var result = new List<PromptSegment>();

            for (int i = 0; i < parsedSegments.Count; i++)
            {
                PromptSegment segment = parsedSegments[i];
                if (segment.IsPause)
                {
                    result.Add(segment);
                    continue;
                }

                if (LanguageCode == "zh")
                {
                    foreach (var span in TranscriptTagParser.ParseSpeechSpans(segment.Text))
                    {
                        var chunks = MandarinPocketText.Split(span.Text, text => _sentencePiece.EncodeToPieces(text).Count);
                        for (int chunk = 0; chunk < chunks.Count; chunk++)
                            result.Add(PromptSegment.Speech(chunks[chunk], chunk == 0 ? span.TranscriptText : ""));
                    }
                    continue;
                }

                if (TranscriptTagParser.ContainsTags(segment.Text))
                {
                    string spokenText = TranscriptTagParser.StripTagsForSpeech(segment.Text);
                    if (!string.IsNullOrWhiteSpace(spokenText))
                    {
                        result.Add(PromptSegment.Speech(spokenText, segment.Text));
                    }

                    continue;
                }

                List<string> textChunks = TextProcessor.SplitIntoBestSentences(segment.Text, _sentencePiece);
                for (int chunkIndex = 0; chunkIndex < textChunks.Count; chunkIndex++)
                {
                    if (!string.IsNullOrWhiteSpace(textChunks[chunkIndex]))
                    {
                        result.Add(PromptSegment.Speech(textChunks[chunkIndex]));
                    }
                }
            }

            return result;
        }

        void PostSilence(int sampleCount, CancellationToken cts)
        {
            int remainingSamples = sampleCount;
            int toneOffset = 0;
            while (remainingSamples > 0 && !cts.IsCancellationRequested)
            {
                int chunkSamples = Math.Min(remainingSamples, SILENCE_POST_CHUNK_SAMPLES);
                var gap = new float[chunkSamples];
                for (int sampleIndex = 0; sampleIndex < chunkSamples; sampleIndex++)
                {
                    float roomTone = RoomTone[(toneOffset + sampleIndex) % RoomTone.Length];
                    if (sampleIndex < BOUNDARY_FADE_SAMPLES)
                    {
                        float t = (sampleIndex + 1f) / BOUNDARY_FADE_SAMPLES;
                        gap[sampleIndex] = _lastPostedSample + ((roomTone - _lastPostedSample) * t);
                    }
                    else
                    {
                        gap[sampleIndex] = roomTone;
                    }
                }

                toneOffset += chunkSamples;
                PostAudioResponse(gap);
                remainingSamples -= chunkSamples;
            }
        }

        static float[] CreateRoomTone()
        {
            var roomTone = new float[PauseTagParser.SampleRate];
            uint state = 0x6D2B79F5u;
            float filtered = 0f;

            for (int i = 0; i < roomTone.Length; i++)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                float random = ((state & 0x00FFFFFFu) / 8388607.5f) - 1f;
                filtered += (random - filtered) * 0.015f;
                roomTone[i] = filtered * ROOM_TONE_LEVEL;
            }

            return roomTone;
        }

        void PostAudioResponse(float[] response, PocketTtsTranscriptWord[] words = null)
        {
            if (response != null && response.Length > 0)
            {
                _lastPostedSample = response[response.Length - 1];
            }

            PostResponse(new PocketTtsAudioResponse(
                response,
                response != null ? response.Length : 0,
                words ?? Array.Empty<PocketTtsTranscriptWord>()));
        }

        List<long> EncodeToIds(string prompt)
        {
            List<long> tokenIds = new List<long>();
            try
            {
                List<string> tokens = _sentencePiece.EncodeToPieces(prompt);
                for (int i = 0; i < tokens.Count; i++)
                    tokenIds.Add(_sentencePiece.PieceToId(tokens[i]));
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"An unexpected error occurred: {ex.Message}");
            }
            return tokenIds;
        }

        public Dictionary<string, OrtValue> InitFlowLmState()
        {
            var state = new Dictionary<string, OrtValue>();
            for (int i = 0; _flowLmMain.InputMetadata.ContainsKey($"state_{i}"); i += 3)
            {
                long[] kvShape = Array.ConvertAll(_flowLmMain.InputMetadata[$"state_{i}"].Dimensions, d => (long)d);
                if (Array.Exists(kvShape, d => d <= 0))
                    throw new InvalidOperationException("Pocket TTS cache dimensions must be static and positive.");
                state[$"state_{i}"] = TensorUtil.CreateEmptyFloat16Tensor(kvShape);
                state[$"state_{i + 1}"] = TensorUtil.CreateEmptyFloat16Tensor(kvShape);
                state[$"state_{i + 2}"] = TensorUtil.CreateInt64Tensor(0);
            }
            return state;
        }

        // Upstream 86eebf4: streaming convolutions have no previous audio at reset.
        // All first-frame flags must be true; MOSS uses a different codec state schema.
        public Dictionary<string, OrtValue> InitMimiDecoderState()
        {
            var state = new Dictionary<string, OrtValue>();
            state["state_0"] = TensorUtil.CreateBoolTensor(true);
            state["state_1"] = TensorUtil.CreateEmptyFloatTensor(new long[] { 1, 512, 6 });
            state["state_2"] = TensorUtil.CreateBoolTensor(true);
            state["state_3"] = TensorUtil.CreateEmptyFloatTensor(new long[] { 1, 64, 2 });
            state["state_4"] = TensorUtil.CreateEmptyFloatTensor(new long[] { 1, 256, 6 });
            state["state_5"] = TensorUtil.CreateBoolTensor(true);
            state["state_6"] = TensorUtil.CreateEmptyFloatTensor(new long[] { 1, 256, 2 });
            state["state_7"] = TensorUtil.CreateBoolTensor(true);
            state["state_8"] = TensorUtil.CreateEmptyFloatTensor(new long[] { 1, 128, 0 });
            state["state_9"] = TensorUtil.CreateEmptyFloatTensor(new long[] { 1, 128, 5 });
            state["state_10"] = TensorUtil.CreateBoolTensor(true);
            state["state_11"] = TensorUtil.CreateEmptyFloatTensor(new long[] { 1, 128, 2 });
            state["state_12"] = TensorUtil.CreateBoolTensor(true);
            state["state_13"] = TensorUtil.CreateEmptyFloatTensor(new long[] { 1, 64, 0 });
            state["state_14"] = TensorUtil.CreateEmptyFloatTensor(new long[] { 1, 64, 4 });
            state["state_15"] = TensorUtil.CreateBoolTensor(true);
            state["state_16"] = TensorUtil.CreateEmptyFloatTensor(new long[] { 1, 64, 2 });
            state["state_17"] = TensorUtil.CreateBoolTensor(true);
            state["state_18"] = TensorUtil.CreateEmptyFloatTensor(new long[] { 1, 32, 0 });
            state["state_19"] = TensorUtil.CreateEmptyFloat16Tensor(new long[] { 1, 250, 8, 64 });
            state["state_20"] = TensorUtil.CreateEmptyFloat16Tensor(new long[] { 1, 250, 8, 64 });
            state["state_21"] = TensorUtil.CreateInt64Tensor(0);
            state["state_22"] = TensorUtil.CreateEmptyFloat16Tensor(new long[] { 1, 250, 8, 64 });
            state["state_23"] = TensorUtil.CreateEmptyFloat16Tensor(new long[] { 1, 250, 8, 64 });
            state["state_24"] = TensorUtil.CreateInt64Tensor(0);
            state["state_25"] = TensorUtil.CreateBoolTensor(true);
            state["state_26"] = TensorUtil.CreateEmptyFloatTensor(new long[] { 1, 512, 16 });
            state["state_27"] = TensorUtil.CreateBoolTensor(true);
            state["state_28"] = TensorUtil.CreateEmptyFloatTensor(new long[] { 1, 1, 6 });
            state["state_29"] = TensorUtil.CreateBoolTensor(true);
            state["state_30"] = TensorUtil.CreateEmptyFloatTensor(new long[] { 1, 64, 2 });
            state["state_31"] = TensorUtil.CreateBoolTensor(true);
            state["state_32"] = TensorUtil.CreateEmptyFloatTensor(new long[] { 1, 32, 0 });
            state["state_33"] = TensorUtil.CreateBoolTensor(true);
            state["state_34"] = TensorUtil.CreateEmptyFloatTensor(new long[] { 1, 512, 2 });
            state["state_35"] = TensorUtil.CreateBoolTensor(true);
            state["state_36"] = TensorUtil.CreateEmptyFloatTensor(new long[] { 1, 64, 4 });
            state["state_37"] = TensorUtil.CreateBoolTensor(true);
            state["state_38"] = TensorUtil.CreateEmptyFloatTensor(new long[] { 1, 128, 2 });
            state["state_39"] = TensorUtil.CreateBoolTensor(true);
            state["state_40"] = TensorUtil.CreateEmptyFloatTensor(new long[] { 1, 64, 0 });
            state["state_41"] = TensorUtil.CreateBoolTensor(true);
            state["state_42"] = TensorUtil.CreateEmptyFloatTensor(new long[] { 1, 128, 5 });
            state["state_43"] = TensorUtil.CreateBoolTensor(true);
            state["state_44"] = TensorUtil.CreateEmptyFloatTensor(new long[] { 1, 256, 2 });
            state["state_45"] = TensorUtil.CreateBoolTensor(true);
            state["state_46"] = TensorUtil.CreateEmptyFloatTensor(new long[] { 1, 128, 0 });
            state["state_47"] = TensorUtil.CreateBoolTensor(true);
            state["state_48"] = TensorUtil.CreateEmptyFloatTensor(new long[] { 1, 256, 6 });
            state["state_49"] = TensorUtil.CreateEmptyFloat16Tensor(new long[] { 1, 250, 8, 64 });
            state["state_50"] = TensorUtil.CreateEmptyFloat16Tensor(new long[] { 1, 250, 8, 64 });
            state["state_51"] = TensorUtil.CreateInt64Tensor(0);
            state["state_52"] = TensorUtil.CreateEmptyFloat16Tensor(new long[] { 1, 250, 8, 64 });
            state["state_53"] = TensorUtil.CreateEmptyFloat16Tensor(new long[] { 1, 250, 8, 64 });
            state["state_54"] = TensorUtil.CreateInt64Tensor(0);
            state["state_55"] = TensorUtil.CreateEmptyFloatTensor(new long[] { 1, 512, 16 });
            return state;
        }

        private (float[] data, long[] shape) PrependBosBeforeVoice(float[] voiceData, long[] voiceShape)
        {
            if (_bosData == null || _bosShape == null) return (voiceData, voiceShape);

            // Shape: [1, frames, 1024]
            int bosFrames = (int)_bosShape[1];
            int voiceFrames = (int)voiceShape[1];
            int embDim = 1024;

            int outFrames = bosFrames + voiceFrames;
            float[] output = new float[outFrames * embDim];

            Array.Copy(_bosData, 0, output, 0, bosFrames * embDim);
            Array.Copy(voiceData, 0, output, bosFrames * embDim, voiceFrames * embDim);

            return (output, new long[] { 1, outFrames, embDim });
        }

        private float[] _decoderFlattenBuffer;
        private GCHandle _decoderFlattenHandle;
        private OrtValue _decoderLatentTensor;

        private Dictionary<string, OrtValue> _decoderInputs;
        private List<int> _decoderStateOutputIndices;
        private List<string> _decoderStateKeys;

        private float[] DecodeChunk(List<float[]> chunk, ref Dictionary<string, OrtValue> state, InferenceSession decoder)
        {
            try
            {
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                int count = chunk.Count;
                int size = count * 32;

                if (_decoderFlattenBuffer == null || _decoderFlattenBuffer.Length < size)
                {
                    if (_decoderFlattenHandle.IsAllocated) _decoderFlattenHandle.Free();
                    _decoderLatentTensor?.Dispose();

                    _decoderFlattenBuffer = new float[size * 2];
                    _decoderFlattenHandle = GCHandle.Alloc(_decoderFlattenBuffer, GCHandleType.Pinned);
                    _decoderLatentTensor = null;
                }

                if (_decoderLatentTensor == null || _decoderLatentTensor.GetTensorTypeAndShape().Shape[1] != count)
                {
                    _decoderLatentTensor?.Dispose();
                    _decoderLatentTensor = OrtValue.CreateTensorValueFromMemory<float>(
                        OrtMemoryInfo.DefaultInstance, _decoderFlattenBuffer, new long[] { 1, count, 32 });
                }

                for (int i = 0; i < count; i++)
                {
                    Array.Copy(chunk[i], 0, _decoderFlattenBuffer, i * 32, 32);
                }

                if (_decoderInputs == null)
                {
                    // CRITICAL: Fresh dictionary and cache for this session
                    _decoderInputs = new Dictionary<string, OrtValue>(state);

                    _decoderStateOutputIndices = new List<int>();
                    _decoderStateKeys = new List<string>();
                    var outputNames = decoder.OutputNames;
                    for (int i = 0; i < outputNames.Count; i++)
                    {
                        string name = outputNames[i];
                        if (name.StartsWith("out_state_"))
                        {
                            _decoderStateOutputIndices.Add(i);
                            _decoderStateKeys.Add("state_" + name.Replace("out_state_", ""));
                        }
                    }
                }
                _decoderInputs["latent"] = _decoderLatentTensor;
                long preMs = sw.ElapsedMilliseconds;

                sw.Restart();
                using var runOptions = new RunOptions();
                using var res = decoder.Run(runOptions, _decoderInputs, decoder.OutputNames);
                long runMs = sw.ElapsedMilliseconds;

                sw.Restart();
                for (int i = 0; i < _decoderStateOutputIndices.Count; i++)
                {
                    TensorUtil.CloneInto(res[_decoderStateOutputIndices[i]], state[_decoderStateKeys[i]]);
                }
                long mirrorMs = sw.ElapsedMilliseconds;

                if (EnableProfiling) Debug.Log($"[Mimi Micro] Pre: {preMs}ms | Run: {runMs}ms | Mirror: {mirrorMs}ms");

                // Optimized Audio extraction: Return COPY to avoid race condition
                var data = res[0].GetTensorDataAsSpan<float>();
                return data.ToArray();
            }
            catch (Exception e)
            {
                Debug.LogError($"[Mimi Error] {e}");
            }
            return new float[0];
        }

        private void FreeDecoderResources()
        {
            if (_decoderFlattenHandle.IsAllocated) _decoderFlattenHandle.Free();
            _decoderLatentTensor?.Dispose();
            _decoderLatentTensor = null;
            _decoderFlattenBuffer = null;
            _decoderInputs = null;
            _decoderStateOutputIndices = null;
            _decoderStateKeys = null;
        }

        #endregion

        public delegate void ResponseGeneratedDelegate(PocketTtsAudioResponse response);
        public event ResponseGeneratedDelegate OnResponseGenerated;

        void PostResponse(PocketTtsAudioResponse response)
        {
            unityContext?.Post(_ => OnResponseGenerated?.Invoke(response), null);
        }

    }

    public class STPair : IDisposable
    {
        public OrtValue S { get; }
        public OrtValue T { get; }

        public STPair(float s, float t)
        {
            S = OrtValue.CreateTensorValueFromMemory<float>(
                OrtMemoryInfo.DefaultInstance, new[] { s }, new long[] { 1, 1 });
            T = OrtValue.CreateTensorValueFromMemory<float>(
                OrtMemoryInfo.DefaultInstance, new[] { t }, new long[] { 1, 1 });
        }

        public void Dispose()
        {
            S.Dispose();
            T.Dispose();
        }
    }
}
