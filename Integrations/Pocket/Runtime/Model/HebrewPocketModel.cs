using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.ML.OnnxRuntime;

namespace PocketTTS
{
    // The Hebrew export fuses the flow model and Mimi decoder into one streaming graph.
    internal sealed class HebrewPocketModel : IDisposable
    {
        private readonly InferenceSession model;
        private readonly HebrewPronunciation pronunciation;
        private readonly Random random = new();
        private static readonly string[] AudioOutputs =
            { "flow_kv_new", "audio", "next_latent", "eos_logit", "mimi_kv_new", "mimi_offset_out", "mimi_conv_out" };
        private static readonly string[] PrefillOutputs = { "flow_kv_new" };
        private const int FlowStride = 6 * 2 * 16 * 64;
        private const int MimiStride = 2 * 2 * 8 * 64;

        public HebrewPocketModel(string directory, SessionOptions options, SentencePieceWrapper tokenizer)
        {
            pronunciation = new HebrewPronunciation(directory, options, tokenizer);
            try { model = new InferenceSession(Path.Combine(directory, "model.onnx"), options); }
            catch { pronunciation.Dispose(); throw; }
        }

        public void Dispose()
        {
            model.Dispose();
            pronunciation.Dispose();
        }

        public void Speak(string text, VoiceInfo voice, int frameLimit, Action<float[]> emit, CancellationToken cancellation)
        {
            text = HebrewSpokenNumbers.Expand(text);
            // Bound ReNikud context and split on word boundaries before tokenizing.
            for (int start = 0; start < text.Length;)
            {
                cancellation.ThrowIfCancellationRequested();
                int limit = Math.Min(text.Length, start + 112);
                if (limit < text.Length)
                {
                    int space = text.LastIndexOf(' ', limit, limit - start);
                    if (space > start) limit = space + 1;
                }
                string chunk = text.Substring(start, limit - start).Trim();
                start = limit;
                if (chunk.Length == 0) continue;
                long[] tokens = pronunciation.Encode(chunk);
                if (tokens.Length == 0) continue;
                var state = new Decoder(model, voice, tokens, frameLimit);
                state.Generate(random, emit, cancellation);
            }
        }

        private sealed class Decoder
        {
            private readonly InferenceSession model;
            private readonly VoiceInfo voice;
            private readonly long[] tokens;
            private readonly int maxFrames;
            private readonly float[] flow, mimi = new float[266 * MimiStride], convolution = new float[14720];
            private readonly float[] latent = new float[32], noise = new float[32];
            private readonly float[] silentCondition = new float[1024];
            private int flowLength;
            private long mimiOffset;
            private bool eos;

            public Decoder(InferenceSession model, VoiceInfo voice, long[] tokens, int frameLimit)
            {
                this.model = model;
                this.voice = voice;
                this.tokens = tokens;
                maxFrames = Math.Min(frameLimit, (int)Math.Ceiling((tokens.Length / 3.0 + 2) * 12.5));
                flow = new float[checked(((int)voice.Shape[1] + tokens.Length + maxFrames) * FlowStride)];
            }

            public void Generate(Random random, Action<float[]> emit, CancellationToken cancellation)
            {
                using var options = new RunOptions();
                Step(options, 2, (int)voice.Shape[1], false);
                cancellation.ThrowIfCancellationRequested();
                Step(options, 0, tokens.Length, false);
                int eosFrame = -1;
                for (int frame = 0; frame < maxFrames; frame++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    for (int i = 0; i < noise.Length; i += 2)
                    {
                        double radius = Math.Sqrt(-0.6 * Math.Log(1 - random.NextDouble()));
                        double angle = 2 * Math.PI * random.NextDouble();
                        noise[i] = (float)(radius * Math.Cos(angle));
                        noise[i + 1] = (float)(radius * Math.Sin(angle));
                    }
                    float[] audio = Step(options, 1, 1, frame == 0);
                    if (eos && eosFrame < 0) eosFrame = frame;
                    if (eosFrame >= 0 && frame >= eosFrame + 2) return;
                    emit(audio);
                }
            }

            // Gates 0, 1 and 2 select text, an audio frame and voice conditioning respectively.
            private float[] Step(RunOptions options, int gate, int length, bool firstFrame)
            {
                bool generate = gate == 1;
                var gates = new float[3];
                gates[gate] = 1;
                var bos = new float[length];
                if (firstFrame) bos[0] = 1;
                var inputs = new Dictionary<string, OrtValue>();
                try
                {
                    inputs["tokens"] = Tensor(gate == 0 ? tokens : new long[length], 1, length);
                    inputs["cond"] = Tensor(gate == 2 ? voice.Data : generate ? silentCondition : new float[length * 1024], 1, length, 1024);
                    inputs["latent"] = Tensor(generate ? latent : new float[length * 32], 1, length, 32);
                    inputs["is_bos"] = Tensor(bos, 1, length, 1);
                    inputs["noise"] = Tensor(noise, 1, 32);
                    inputs["gates"] = Tensor(gates, 3);
                    inputs["lora"] = Tensor(new[] { 1f });
                    inputs["decode_steps"] = Tensor(new[] { 2f });
                    inputs["flow_kv"] = Tensor(flow, flowLength, 6, 2, 1, 16, 64);
                    inputs["flow_offset"] = Tensor(new long[] { flowLength });
                    inputs["mimi_kv"] = Tensor(mimi, 266, 2, 2, 1, 8, 64);
                    inputs["mimi_offset"] = Tensor(new[] { mimiOffset });
                    inputs["mimi_conv"] = Tensor(convolution, convolution.Length);
                    using var result = model.Run(options, inputs, generate ? AudioOutputs : PrefillOutputs);
                    result[0].GetTensorDataAsSpan<float>().CopyTo(flow.AsSpan(flowLength * FlowStride));
                    flowLength += length;
                    if (!generate) return null;
                    result[2].GetTensorDataAsSpan<float>().CopyTo(latent);
                    eos = result[3].GetTensorDataAsSpan<float>()[0] > -4f;
                    var append = result[4].GetTensorDataAsSpan<float>();
                    Array.Copy(mimi, append.Length, mimi, 0, mimi.Length - append.Length);
                    append.CopyTo(mimi.AsSpan(mimi.Length - append.Length));
                    mimiOffset = result[5].GetTensorDataAsSpan<long>()[0];
                    result[6].GetTensorDataAsSpan<float>().CopyTo(convolution);
                    return result[1].GetTensorDataAsSpan<float>().ToArray();
                }
                finally { foreach (var input in inputs.Values) input.Dispose(); }
            }

            private static OrtValue Tensor<T>(T[] values, params long[] shape) where T : unmanaged =>
                OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, values.AsMemory(), shape);
        }
    }
}
