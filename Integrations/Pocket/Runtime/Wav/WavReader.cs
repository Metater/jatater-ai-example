using System;
using System.IO;
using System.Text;

namespace PocketTTS
{
    public static class WavReader
    {
        public static float[] LoadWav(string filePath, int targetSampleRate = 24000, int maxSeconds = 30)
        {
            var channels = LoadWavChannels(filePath, targetSampleRate, maxSeconds);
            if (channels.Length == 1) return channels[0];
            var mono = new float[channels[0].Length];
            for (int i = 0; i < mono.Length; i++)
                for (int c = 0; c < channels.Length; c++) mono[i] += channels[c][i] / channels.Length;
            return mono;
        }

        // Channel-major output preserves MOSS's native stereo reference conditioning.
        // Limit before allocation/resampling to bound reference encoder memory.
        public static float[][] LoadWavChannels(string filePath, int targetSampleRate, int maxSeconds = 30)
        {
            if (targetSampleRate <= 0 || maxSeconds <= 0) throw new ArgumentOutOfRangeException();
            using var stream = File.OpenRead(filePath);
            using var reader = new BinaryReader(stream, Encoding.ASCII);
            string FourCC() => Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (FourCC() != "RIFF") throw new InvalidDataException("Expected RIFF WAV.");
            reader.ReadUInt32();
            if (FourCC() != "WAVE") throw new InvalidDataException("Expected WAVE data.");
            int format = 0, channels = 0, sampleRate = 0, bits = 0, blockAlign = 0;
            while (stream.Position + 8 <= stream.Length)
            {
                string chunk = FourCC();
                uint size = reader.ReadUInt32();
                long start = stream.Position;
                if (size > stream.Length - start) throw new InvalidDataException("Truncated WAV chunk.");
                if (chunk == "fmt ")
                {
                    if (size < 16) throw new InvalidDataException("Invalid WAV format chunk.");
                    format = reader.ReadUInt16(); channels = reader.ReadUInt16(); sampleRate = reader.ReadInt32();
                    reader.ReadInt32(); blockAlign = reader.ReadUInt16(); bits = reader.ReadUInt16();
                    if (format == 0xfffe && size >= 40)
                    {
                        // WAVE_FORMAT_EXTENSIBLE stores PCM/IEEE float in the subformat GUID.
                        stream.Position = start + 24;
                        format = reader.ReadUInt16();
                    }
                }
                else if (chunk == "data")
                {
                    if (channels < 1 || channels > 2 || sampleRate <= 0 ||
                        !((format == 1 && (bits == 8 || bits == 16 || bits == 24 || bits == 32)) || (format == 3 && bits == 32)) ||
                        blockAlign != channels * (bits / 8) || size % blockAlign != 0)
                        throw new InvalidDataException("Unsupported or invalid WAV format.");
                    int frames = checked((int)Math.Min(size / blockAlign, (long)sampleRate * maxSeconds));
                    var pcm = new float[channels][];
                    for (int c = 0; c < channels; c++) pcm[c] = new float[frames];
                    for (int i = 0; i < frames; i++)
                        for (int c = 0; c < channels; c++)
                        {
                            float value;
                            if (format == 3) value = reader.ReadSingle();
                            else if (bits == 8) value = (reader.ReadByte() - 128) / 128f;
                            else if (bits == 16) value = reader.ReadInt16() / 32768f;
                            else if (bits == 24)
                            {
                                int raw = reader.ReadByte() | reader.ReadByte() << 8 | reader.ReadByte() << 16;
                                value = ((raw << 8) >> 8) / 8388608f;
                            }
                            else value = reader.ReadInt32() / 2147483648f;
                            if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidDataException("Non-finite WAV sample.");
                            pcm[c][i] = value;
                        }
                    for (int c = 0; c < channels; c++)
                        if (sampleRate != targetSampleRate) pcm[c] = ApplyWdlResample(pcm[c], sampleRate, targetSampleRate);
                    return pcm;
                }
                stream.Position = start + size + (size & 1); // RIFF chunks are word-aligned.
            }
            throw new InvalidDataException("WAV has no data chunk.");
        }

        private static float[] ApplyWdlResample(float[] input, int srcRate, int dstRate)
        {
            if (input.Length == 0) return Array.Empty<float>();
            var resampler = new WdlResampler();
            resampler.SetMode(true, 6, false);
            resampler.SetRates(srcRate, dstRate);
            resampler.SetFeedMode(true);
            // Request one extra frame, then supply the real count to flush the tail.
            resampler.ResamplePrepare(input.Length + 1, 1, out var buffer, out int offset);
            Array.Copy(input, 0, buffer, offset, input.Length);
            var output = new float[checked((int)Math.Ceiling(input.Length * (double)dstRate / srcRate))];
            int count = resampler.ResampleOut(output, 0, input.Length, output.Length, 1);
            if (count != output.Length) Array.Resize(ref output, count);
            return output;
        }
    }
}
