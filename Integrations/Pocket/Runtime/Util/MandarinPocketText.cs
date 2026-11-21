using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PocketTTS
{
    // The ZH checkpoint was trained on spoken numbers and full-width punctuation.
    public static class MandarinPocketText
    {
        private const string Digits = "零一二三四五六七八九";

        private static string SpellDigits(string value)
        {
            var result = new StringBuilder();
            foreach (char c in value) result.Append(c >= '0' && c <= '9' ? Digits[c - '0'] : c);
            return result.ToString();
        }

        private static string Integer(long value, bool omitLeadingOne = true)
        {
            if (value < 10) return Digits[(int)value].ToString();
            foreach (var unit in new[] { (100000000L, "亿"), (10000L, "万"), (1000L, "千"), (100L, "百"), (10L, "十") })
            {
                if (value < unit.Item1) continue;
                long high = value / unit.Item1, rest = value % unit.Item1;
                string prefix = omitLeadingOne && value < 20 ? "" : Integer(high);
                return prefix + unit.Item2 + (rest == 0 ? "" :
                    (rest < unit.Item1 / 10 ? "零" : "") + Integer(rest, false));
            }
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        private static string Number(string value)
        {
            string[] parts = value.Split('.');
            // Long identifiers and leading zeroes must retain every digit.
            string whole = parts[0].Length > 12 || (parts[0].Length > 1 && parts[0][0] == '0')
                ? SpellDigits(parts[0]) : Integer(long.Parse(parts[0], CultureInfo.InvariantCulture));
            return whole + (parts.Length == 2 ? "点" + SpellDigits(parts[1]) : "");
        }

        public static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            text = text.Replace("\r", "").Replace("\n", "").Replace("\t", "");
            text = Regex.Replace(text, @"(订单(?:编)?号?|单号|编号|受理号|流水号|工单号|运单号|快递单号|航班号|票号|卡号|尾号|电话)([是为]?[:：]?\s*[A-Za-z]{0,4})([0-9]{4,})",
                m => m.Groups[1].Value + m.Groups[2].Value + SpellDigits(m.Groups[3].Value));
            text = Regex.Replace(text, @"([0-9]{4})年", m => SpellDigits(m.Groups[1].Value) + "年");
            text = Regex.Replace(text, @"([0-9]{4})[-/]([0-9]{1,2})[-/]([0-9]{1,2})",
                m => SpellDigits(m.Groups[1].Value) + "年" + Integer(long.Parse(m.Groups[2].Value)) + "月" + Integer(long.Parse(m.Groups[3].Value)) + "日");
            text = Regex.Replace(text, @"(?<=[0-9]),(?=[0-9]{3}(?:[^0-9]|$))", "");
            text = Regex.Replace(text, @"([0-9]+(?:\.[0-9]+)?)%", m => "百分之" + Number(m.Groups[1].Value));
            text = Regex.Replace(text, @"[¥￥]([0-9]+(?:\.[0-9]+)?)", m => Number(m.Groups[1].Value) + "元");
            text = Regex.Replace(text, @"\$([0-9]+(?:\.[0-9]+)?)", m => Number(m.Groups[1].Value) + "美元");
            text = Regex.Replace(text, @"-([0-9]+(?:\.[0-9]+)?)", m => "负" + Number(m.Groups[1].Value));
            text = Regex.Replace(text, @"[0-9]+(?:\.[0-9]+)?", m => Number(m.Value));
            const string ascii = ",.!?;:", wide = "，。！？；：";
            for (int i = 0; i < ascii.Length; i++) text = text.Replace(ascii[i], wide[i]);
            text = text.Trim();
            if (text.Length > 0 && "，。！？；：".IndexOf(text[text.Length - 1]) < 0) text += "。";
            return text;
        }

        public static List<string> Split(string text, Func<string, int> tokenCount)
        {
            text = Normalize(text);
            var result = new List<string>();
            while (text.Length > 0)
            {
                if (tokenCount(text) <= 48) { result.Add(text); break; }
                int end = 0, boundary = 0;
                while (end < text.Length)
                {
                    int next = end + (char.IsHighSurrogate(text[end]) && end + 1 < text.Length ? 2 : 1);
                    if (tokenCount(text.Substring(0, next)) > 48) break;
                    end = next;
                    if ("，。！？；、".IndexOf(text[end - 1]) >= 0 && tokenCount(text.Substring(0, end)) >= 12)
                        boundary = end;
                }
                if (end == 0) throw new InvalidOperationException("A Mandarin character exceeds the token budget.");
                if (boundary > 0) end = boundary;
                result.Add(text.Substring(0, end));
                text = text.Substring(end);
            }
            return result;
        }
    }
}
