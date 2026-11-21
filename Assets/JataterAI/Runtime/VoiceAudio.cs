using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace JataterAI
{
    public static class VoiceAudio
    {
        // Accepts interleaved PCM from the game's existing microphone owner.
        public static byte[] Wav16(float[] samples, int rate, int channels)
        {
            if (samples == null || channels < 1 || channels > 2 || rate < 8000 || rate > 48000 || samples.Length == 0 || samples.Length % channels != 0)
                throw new ArgumentException("Supply mono or stereo interleaved PCM at 8–48 kHz.");
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            void Tag(string tag) => writer.Write(Encoding.ASCII.GetBytes(tag));
            Tag("RIFF"); writer.Write(36 + samples.Length * 2); Tag("WAVE");
            Tag("fmt "); writer.Write(16); writer.Write((short)1); writer.Write((short)channels);
            writer.Write(rate); writer.Write(rate * channels * 2); writer.Write((short)(channels * 2)); writer.Write((short)16);
            Tag("data"); writer.Write(samples.Length * 2);
            foreach (float sample in samples) writer.Write((short)Mathf.Clamp(Mathf.RoundToInt(sample * 32768f), -32768, 32767));
            return stream.ToArray();
        }

        public static AudioClip Decode(byte[] bytes)
        {
            using var reader = new BinaryReader(new MemoryStream(bytes));
            string Tag() => Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (Tag() != "RIFF") throw new InvalidDataException("Speech response must contain a RIFF WAV file.");
            reader.ReadUInt32();
            if (Tag() != "WAVE") throw new InvalidDataException("Speech response has no WAVE header.");
            int rate = 0, channels = 0;
            while (reader.BaseStream.Position + 8 <= bytes.Length)
            {
                string tag = Tag(); uint length = reader.ReadUInt32();
                long end = reader.BaseStream.Position + length;
                if (end > bytes.Length) throw new InvalidDataException("Incomplete WAV chunk.");
                if (tag == "fmt " && length >= 16)
                {
                    int format = reader.ReadUInt16();
                    channels = reader.ReadUInt16(); rate = reader.ReadInt32();
                    reader.ReadUInt32(); reader.ReadUInt16();
                    if (format != 1 || reader.ReadUInt16() != 16 || channels < 1 || channels > 2 || rate <= 0)
                        throw new InvalidDataException("Expected PCM16 WAV.");
                }
                else if (tag == "data" && rate > 0 && length > 0 && length % (2 * channels) == 0)
                {
                    var pcm = new float[length / 2];
                    for (int i = 0; i < pcm.Length; i++) pcm[i] = reader.ReadInt16() / 32768f;
                    var clip = AudioClip.Create("Jatater speech", pcm.Length / channels, channels, rate, false);
                    clip.SetData(pcm, 0);
                    return clip;
                }
                reader.BaseStream.Position = end + length % 2;
            }
            throw new InvalidDataException("Speech response has no playable PCM.");
        }
    }
}
