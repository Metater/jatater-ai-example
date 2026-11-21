using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace PocketTTS
{
    // ReNikud predicts sounds for letters. Expand numbers in the speech copy before G2P.
    internal static class HebrewSpokenNumbers
    {
        private static readonly Regex Numbers = new Regex(
            @"(?<sign>-)?(?<currency>[$₪€])?(?<sign>-)?(?<integer>[0-9]{1,3}(?:,[0-9]{3})+|[0-9]+)(?:\.(?<fraction>[0-9]+))?(?<percent>%?)");
        private static readonly string[] Feminine = "אפס אחת שתיים שלוש ארבע חמש שש שבע שמונה תשע עשר".Split(' ');
        private static readonly string[] Masculine = "אפס אחד שניים שלושה ארבעה חמישה שישה שבעה שמונה תשעה עשרה".Split(' ');
        private static readonly string[] Tens = "אפס עשר עשרים שלושים ארבעים חמישים שישים שבעים שמונים תשעים".Split(' ');
        private static readonly string[] Construct = "אפס אלף אלפיים שלושת ארבעת חמשת ששת שבעת שמונת תשעת".Split(' ');

        public static string Expand(string text) => Numbers.Replace(text, match =>
        {
            string integer = match.Groups["integer"].Value.Replace(",", "");
            string fraction = match.Groups["fraction"].Value;
            string currency = match.Groups["currency"].Value;
            bool leadingZero = integer.Length > 1 && integer[0] == '0';
            string spoken = !leadingZero && long.TryParse(integer, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                            && n < 1_000_000_000_000
                ? Cardinal(n, currency.Length > 0 || match.Groups["percent"].Length > 0) : Digits(integer);
            bool wholeTwo = integer == "2" && fraction.Length == 0;
            if (wholeTwo && (currency.Length > 0 || match.Groups["percent"].Length > 0)) spoken = "שני";
            if (fraction.Length > 0) spoken += " נקודה " + Digits(fraction);
            if (match.Groups["sign"].Length > 0) spoken = "מינוס " + spoken;
            switch (currency)
            {
                case "$": return spoken + (wholeTwo ? " דולרים" : " דולר");
                case "€": return spoken + " אירו";
                case "₪": return spoken + (integer == "1" && fraction.Length == 0 ? " שקל" : " שקלים");
            }
            return match.Groups["percent"].Length > 0 ? spoken + (wholeTwo ? " אחוזים" : " אחוז") : spoken;
        });

        private static string Digits(string value) => string.Join(" ", value.Select(ch => Feminine[ch - '0']));

        private static string Cardinal(long value, bool masculine)
        {
            var units = masculine ? Masculine : Feminine;
            if (value <= 10) return units[value];
            if (value < 20)
            {
                string unit = value == 12 ? (masculine ? "שנים" : "שתים") : units[value % 10];
                return unit + (masculine ? " עשר" : " עשרה");
            }
            if (value < 100) return Rest(Tens[value / 10], value % 10, masculine);
            if (value < 1000) return Rest(value < 200 ? "מאה" : value < 300 ? "מאתיים" :
                Feminine[value / 100] + " מאות", value % 100, masculine);
            if (value < 1_000_000)
            {
                long thousands = value / 1000;
                string prefix = thousands <= 2 ? Construct[thousands] : thousands < 10 ?
                    Construct[thousands] + " אלפים" : Cardinal(thousands, true) + " אלף";
                return Rest(prefix, value % 1000, masculine);
            }
            long scale = value < 1_000_000_000 ? 1_000_000 : 1_000_000_000;
            string noun = scale == 1_000_000 ? "מיליון" : "מיליארד";
            long count = value / scale;
            string group = count == 1 ? noun : count == 2 ? "שני " + noun : Cardinal(count, true) + " " + noun;
            return Rest(group, value % scale, masculine);
        }

        private static string Rest(string prefix, long rest, bool masculine) =>
            rest == 0 ? prefix : prefix + (rest < 20 || (rest < 100 && rest % 10 == 0) ? " ו" : " ") + Cardinal(rest, masculine);
    }
}
