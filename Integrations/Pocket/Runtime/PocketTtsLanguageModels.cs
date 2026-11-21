using System;
using System.Collections.Generic;
using System.IO;

namespace PocketTTS
{
    public static class PocketTtsLanguageModels
    {
        public const string RelativeRoot = "pocket-tts-unity/lookbe/pocket-tts-onnx-v2";

        private static readonly string[] RequiredFiles =
        {
            "mimi_encoder.onnx", "mimi_decoder_int8.onnx", "tokenizer.model",
            "text_conditioner.onnx", "flow_lm_main_int8.onnx", "flow_lm_flow_int8.onnx",
            "bos_before_voice.npy"
        };

public static string NormalizeCode(string language)
        {
            if (string.IsNullOrWhiteSpace(language)) return string.Empty;
            string value = language.Trim().ToLowerInvariant();
            if (value == "english") return "en";

            int separator = value.IndexOfAny(new[] { '-', '_' });
            if (separator > 0) value = value.Substring(0, separator);
            return value == "in" ? "id" : value;
        }

        public static bool TryResolve(string streamingAssetsPath, string language, out string code,
            out string directory, out string error)
        {
            code = NormalizeCode(language);
            directory = null;
            
            if (code != "en")
            {
                error = $"No Pocket TTS language pack is configured for '{language}'.";
                return false;
            }

            directory = Path.Combine(streamingAssetsPath,
                RelativeRoot, "english");
            foreach (string file in RequiredFiles)
            {
                if (File.Exists(Path.Combine(directory, file))) continue;
                error = $"Pocket TTS language '{code}' is unavailable: missing {Path.Combine(directory, file)}.";
                return false;
            }

            error = null;
            return true;
        }
    }
}


