using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PocketTTS
{
    public readonly struct PromptSegment
    {
        public readonly string Text;
        public readonly string TranscriptText;
        public readonly int PauseSamples;
        public readonly bool IsPause;

        private PromptSegment(string text, string transcriptText, int pauseSamples, bool isPause)
        {
            Text = text;
            TranscriptText = transcriptText;
            PauseSamples = pauseSamples;
            IsPause = isPause;
        }

        public static PromptSegment Speech(string text, string transcriptText = null)
        {
            return new PromptSegment(text, transcriptText ?? text, 0, false);
        }

        public static PromptSegment Pause(int pauseSamples)
        {
            return new PromptSegment(null, null, pauseSamples, true);
        }
    }

    public readonly struct PocketTtsTranscriptWord
    {
        public readonly string Text;
        public readonly int SampleOffset;

        public PocketTtsTranscriptWord(string text, int sampleOffset)
        {
            Text = text;
            SampleOffset = sampleOffset;
        }
    }

    public readonly struct PocketTtsAudioResponse
    {
        public readonly float[] Data;
        public readonly int DataLength;
        public readonly PocketTtsTranscriptWord[] Words;

        public PocketTtsAudioResponse(float[] data, int dataLength, PocketTtsTranscriptWord[] words)
        {
            Data = data;
            DataLength = dataLength;
            Words = words;
        }
    }

    public static class RussianStressMarks
    {
        // Only remove stress accents on Russian vowels; preserve ё, й and other languages.
        private static readonly Regex Marks = new Regex(@"(?<=[аеёиоуыэюяАЕЁИОУЫЭЮЯ])[́́]+", RegexOptions.CultureInvariant);
        public static string Remove(string text) => string.IsNullOrEmpty(text) ? text : Marks.Replace(text, "");
    }

    public static class PauseTagParser
    {
        public const int SampleRate = 24000;
        public const int MaxPauseSeconds = 10;
        public const int MaxPauseSamples = SampleRate * MaxPauseSeconds;

        private static readonly Regex PauseTagRegex = new Regex(
            @"\[(?<value>\d+(?:\.\d+)?)(?<unit>ms|s)\]",
            RegexOptions.CultureInvariant);

        public static List<PromptSegment> Parse(string prompt)
        {
            var segments = new List<PromptSegment>();
            int textStart = 0;

            foreach (Match match in PauseTagRegex.Matches(prompt))
            {
                if (!TryGetPauseSamples(match, out int pauseSamples))
                {
                    continue;
                }

                if (match.Index > textStart)
                {
                    segments.Add(PromptSegment.Speech(prompt.Substring(textStart, match.Index - textStart)));
                }

                segments.Add(PromptSegment.Pause(pauseSamples));
                textStart = match.Index + match.Length;
            }

            if (textStart < prompt.Length)
            {
                segments.Add(PromptSegment.Speech(prompt.Substring(textStart)));
            }

            if (segments.Count == 0)
            {
                segments.Add(PromptSegment.Speech(prompt));
            }

            return segments;
        }

        private static bool TryGetPauseSamples(Match match, out int pauseSamples)
        {
            pauseSamples = 0;
            if (!decimal.TryParse(
                    match.Groups["value"].Value,
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out decimal value))
            {
                return false;
            }

            decimal seconds = match.Groups["unit"].Value == "ms" ? value / 1000m : value;
            if (seconds <= 0m || seconds > MaxPauseSeconds)
            {
                return false;
            }

            decimal roundedSamples = decimal.Round(
                seconds * SampleRate,
                0,
                MidpointRounding.AwayFromZero);
            pauseSamples = Math.Max(1, decimal.ToInt32(roundedSamples));
            return pauseSamples <= MaxPauseSamples;
        }
    }

    public static class TranscriptTagParser
    {
        private static readonly Regex TranscriptTagRegex = new Regex(
            "<transcript\\s+(?:sentinel=\"[A-Z0-9_]+\"\\s+)?text=\"(?<text>[^\"]*)\"\\s*>(?<spoken>.*?)</transcript>",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static readonly Regex WordRegex = new Regex(@"\s*\S+\s*", RegexOptions.CultureInvariant);

        public static bool ContainsTags(string text)
        {
            return !string.IsNullOrEmpty(text) && TranscriptTagRegex.IsMatch(text);
        }

        public static string StripTagsForSpeech(string text)
        {
            return string.IsNullOrEmpty(text)
                ? string.Empty
                : TranscriptTagRegex.Replace(text, match => match.Groups["spoken"].Value);
        }

        public static List<PromptSegment> ParseSpeechSpans(string text)
        {
            var spans = new List<PromptSegment>();
            if (string.IsNullOrEmpty(text)) return spans;
            int start = 0;
            foreach (Match match in TranscriptTagRegex.Matches(text))
            {
                if (match.Index > start) spans.Add(PromptSegment.Speech(text.Substring(start, match.Index - start)));
                spans.Add(PromptSegment.Speech(match.Groups["spoken"].Value, match.Groups["text"].Value));
                start = match.Index + match.Length;
            }
            if (start < text.Length) spans.Add(PromptSegment.Speech(text.Substring(start)));
            return spans;
        }

        public static Queue<string> CreateOverrideWords(string spokenText, string transcriptText)
        {
            var words = new List<string>();
            EnqueueTaggedWords(words, spokenText, transcriptText);
            return new Queue<string>(words);
        }

        public static Queue<string> CreateTranscriptWords(string text)
        {
            var words = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                return new Queue<string>();
            }

            int textIndex = 0;
            foreach (Match tagMatch in TranscriptTagRegex.Matches(text))
            {
                EnqueuePlainWords(words, text.Substring(textIndex, tagMatch.Index - textIndex));
                EnqueueTaggedWords(words, tagMatch.Groups["spoken"].Value, tagMatch.Groups["text"].Value);
                textIndex = tagMatch.Index + tagMatch.Length;
            }

            EnqueuePlainWords(words, text.Substring(textIndex));
            return new Queue<string>(words);
        }

        private static void EnqueuePlainWords(List<string> words, string text)
        {
            // A gap between transcript tags has no word for WordRegex to match.
            // Keep it with the previous word so it survives without adding a timing slot.
            if (!string.IsNullOrEmpty(text) && string.IsNullOrWhiteSpace(text))
            {
                if (words.Count > 0)
                {
                    words[words.Count - 1] += text;
                }
                else
                {
                    words.Add(text);
                }

                return;
            }

            foreach (Match wordMatch in WordRegex.Matches(text))
            {
                var unit = new StringBuilder();
                bool cjkUnit = false;
                var elements = StringInfo.GetTextElementEnumerator(wordMatch.Value);
                while (elements.MoveNext())
                {
                    string element = elements.GetTextElement();
                    bool cjk = IsCjkCharacter(char.ConvertToUtf32(element, 0));
                    bool separator = string.IsNullOrWhiteSpace(element) || char.IsPunctuation(element, 0);
                    // Unspaced CJK needs progressive timings; preserve Latin words, punctuation and Unicode elements.
                    if (!separator && (cjk || cjkUnit) && unit.Length > 0 && !string.IsNullOrWhiteSpace(unit.ToString()))
                    {
                        words.Add(unit.ToString());
                        unit.Clear();
                    }
                    unit.Append(element);
                    if (!separator) cjkUnit = cjk;
                }
                if (unit.Length > 0) words.Add(unit.ToString());
            }
        }

        public static bool IsCjkCharacter(int value) =>
            (value >= 0x3400 && value <= 0x9fff) || (value >= 0xf900 && value <= 0xfaff) ||
            (value >= 0x20000 && value <= 0x323af) || (value >= 0x3040 && value <= 0x30ff) ||
            (value >= 0x31f0 && value <= 0x31ff) || (value >= 0xac00 && value <= 0xd7af) ||
            (value >= 0xff66 && value <= 0xff9d);

        private static void EnqueueTaggedWords(List<string> words, string spokenText, string transcriptText)
        {
            MatchCollection spokenWords = WordRegex.Matches(spokenText);
            if (spokenWords.Count == 0)
            {
                if (words.Count > 0)
                {
                    words[words.Count - 1] += transcriptText;
                }
                else if (!string.IsNullOrEmpty(transcriptText))
                {
                    words.Add(transcriptText);
                }

                return;
            }

            for (int i = 0; i < spokenWords.Count; i++)
            {
                words.Add(i == spokenWords.Count - 1 ? transcriptText : string.Empty);
            }
        }
    }

    public static class SpeechTranscriptTiming
    {
        private const int TranscriptSamplesPerWord = PauseTagParser.SampleRate / 4;

        public static PocketTtsTranscriptWord[] TakeTranscriptWords(
            Queue<string> words,
            ref int samplesUntilNextWord,
            int audioSampleCount,
            bool isFinalChunk)
        {
            if (words.Count == 0 || audioSampleCount <= 0)
            {
                return Array.Empty<PocketTtsTranscriptWord>();
            }

            var result = new List<PocketTtsTranscriptWord>();
            int sampleOffset = Math.Max(0, samplesUntilNextWord);
            while (words.Count > 0 && sampleOffset < audioSampleCount)
            {
                result.Add(new PocketTtsTranscriptWord(words.Dequeue(), sampleOffset));
                sampleOffset += TranscriptSamplesPerWord;
            }

            samplesUntilNextWord = sampleOffset - audioSampleCount;
            if (isFinalChunk && words.Count > 0)
            {
                int firstOffset = result.Count > 0 ? result[result.Count - 1].SampleOffset + 1 : 0;
                int spacing = Math.Max(1, Math.Max(1, audioSampleCount - firstOffset) / words.Count);
                while (words.Count > 0)
                {
                    result.Add(new PocketTtsTranscriptWord(words.Dequeue(), Math.Min(audioSampleCount - 1, firstOffset)));
                    firstOffset += spacing;
                }

                samplesUntilNextWord = 0;
            }

            return result.Count == 0 ? Array.Empty<PocketTtsTranscriptWord>() : result.ToArray();
        }

    }
}
