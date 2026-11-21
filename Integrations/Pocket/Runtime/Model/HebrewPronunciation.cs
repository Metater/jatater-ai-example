using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Newtonsoft.Json;

namespace PocketTTS
{
    // ReNikud and the mixed IPA/SentencePiece vocabulary from the pinned Hebrew export.
    internal sealed class HebrewPronunciation : IDisposable
    {
        private readonly InferenceSession session;
        private readonly Dictionary<string, int> vocabulary;
        private readonly Dictionary<int, string> consonants, vowels;
        private readonly Dictionary<string, int[]> constraints;
        private readonly Dictionary<string, string> geresh;
        private readonly long start, end, unknown;
        private readonly Dictionary<char, long> atomic = new();
        private readonly SentencePieceWrapper tokenizer;

        public HebrewPronunciation(string directory, SessionOptions options, SentencePieceWrapper tokenizer)
        {
            this.tokenizer = tokenizer;
            var adapter = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(directory, "adapter.json")));
            long index = (long)adapter["vocab_base"];
            foreach (char ch in (string)adapter["atomic_chars"]) atomic.Add(ch, index++);
            session = new InferenceSession(Path.Combine(directory, "renikud.onnx"), options);
            try
            {
                var meta = session.ModelMetadata.CustomMetadataMap;
                vocabulary = JsonConvert.DeserializeObject<Dictionary<string, int>>(meta["vocab"]);
                consonants = JsonConvert.DeserializeObject<Dictionary<int, string>>(meta["consonant_vocab"]);
                vowels = JsonConvert.DeserializeObject<Dictionary<int, string>>(meta["vowel_vocab"]);
                constraints = JsonConvert.DeserializeObject<Dictionary<string, int[]>>(meta["letter_consonant_constraints"]);
                geresh = JsonConvert.DeserializeObject<Dictionary<string, string>>(meta["geresh_map"]);
                start = long.Parse(meta["cls_token_id"]);
                end = long.Parse(meta["sep_token_id"]);
                unknown = vocabulary["[UNK]"];
            }
            catch { session.Dispose(); throw; }
        }

        public void Dispose() => session.Dispose();

        internal long[] Encode(string text)
        {
            text = text.Normalize(NormalizationForm.FormD).Replace('׳', '\'').Replace('״', '"').Replace('’', '\'');
            var ids = new long[text.Length + 2];
            ids[0] = start;
            for (int i = 0; i < text.Length; i++)
                ids[i + 1] = vocabulary.TryGetValue(text[i].ToString(), out int id) ? id : unknown;
            ids[ids.Length - 1] = end;
            long[] shape = { 1, ids.Length };
            using var tokens = OrtValue.CreateTensorValueFromMemory(ids, shape);
            using var mask = OrtValue.CreateTensorValueFromMemory(Enumerable.Repeat(1L, ids.Length).ToArray(), shape);
            using var options = new RunOptions();
            using var result = session.Run(options, new[] { "input_ids", "attention_mask" }, new[] { tokens, mask },
                new[] { "consonant_logits", "vowel_logits", "stress_logits" });
            var c = result[0].GetTensorDataAsSpan<float>();
            var v = result[1].GetTensorDataAsSpan<float>();
            var stress = result[2].GetTensorDataAsSpan<float>();
            int nc = c.Length / ids.Length, nv = v.Length / ids.Length, ns = stress.Length / ids.Length;
            var phonemes = new StringBuilder();
            int pos = 0;
            while (pos < text.Length)
            {
                if (char.IsWhiteSpace(text[pos])) { phonemes.Append(' '); pos++; continue; }
                int limit = pos, stressed = pos;
                bool pointed = false;
                while (limit < text.Length && !char.IsWhiteSpace(text[limit]))
                {
                    if (stress[(limit + 1) * ns + 1] > stress[(stressed + 1) * ns + 1]) stressed = limit;
                    if (text[limit] >= 'ְ' && text[limit] <= 'ׇ' && char.GetUnicodeCategory(text[limit]) ==
                        System.Globalization.UnicodeCategory.NonSpacingMark) pointed = true;
                    limit++;
                }
                // This adapter also accepts fully pointed Hebrew directly.
                for (; pos < limit; pos++)
                {
                    char ch = text[pos];
                    if (pointed || ch < 'א' || ch > 'ת')
                    {
                        if (ch != '\'' && ch != '"') phonemes.Append(ch);
                        continue;
                    }
                    var scores = c.Slice((pos + 1) * nc, nc);
                    int best = Best(scores);
                    if (constraints.TryGetValue(ch.ToString(), out var allowed) && allowed.Length > 0)
                    {
                        best = allowed[0];
                        foreach (int candidate in allowed) if (scores[candidate] > scores[best]) best = candidate;
                    }
                    char next = pos + 1 < text.Length ? text[pos + 1] : ' ';
                    string consonant = next == '\'' && geresh.TryGetValue(ch.ToString(), out string modified) ? modified : consonants[best];
                    string vowel = vowels[Best(v.Slice((pos + 1) * nv, nv))];
                    if (ch == 'ח' && !char.IsLetter(next) && vowel == "a")
                    {
                        if (pos == stressed) phonemes.Append('ˈ');
                        phonemes.Append("aχ");
                        continue;
                    }
                    if (consonant != "∅") phonemes.Append(consonant);
                    if (pos == stressed) phonemes.Append('ˈ');
                    if (vowel != "∅") phonemes.Append(vowel);
                }
            }
            var encoded = new List<long>();
            var pending = new StringBuilder();
            void Flush()
            {
                if (pending.Length == 0) return;
                var pieces = tokenizer.EncodeToPieces(pending.ToString());
                if (pieces.Count == 0 && string.IsNullOrWhiteSpace(pending.ToString())) pieces.Add("▁");
                foreach (string piece in pieces) encoded.Add(tokenizer.PieceToId(piece));
                pending.Clear();
            }
            foreach (char ch in phonemes.ToString())
            {
                if (atomic.TryGetValue(ch, out long id)) { Flush(); encoded.Add(id); }
                else pending.Append(ch);
            }
            Flush();
            return encoded.ToArray();
        }

        private static int Best(ReadOnlySpan<float> scores)
        {
            int best = 0;
            for (int i = 1; i < scores.Length; i++) if (scores[i] > scores[best]) best = i;
            return best;
        }
    }
}
