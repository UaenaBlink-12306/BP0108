using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexRuntimePatch
{
    public static class AnswerMatcher
    {
        public static bool Matches(string message, string answer, IEnumerable<string> aliases)
        {
            var normalizedMessage = Normalize(message);
            if (string.IsNullOrWhiteSpace(normalizedMessage))
            {
                return false;
            }

            var variants = new List<string>();
            AddVariants(variants, answer);
            if (aliases != null)
            {
                foreach (var alias in aliases)
                {
                    AddVariants(variants, alias);
                }
            }

            return variants
                .Select(Normalize)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .Any(value => string.Equals(value, normalizedMessage, StringComparison.Ordinal));
        }

        private static void AddVariants(ICollection<string> variants, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            variants.Add(value);
            var withoutNotes = Regex.Replace(value, @"\s*[\(\[].*?[\)\]]", " ").Trim();
            variants.Add(withoutNotes);
            foreach (var part in withoutNotes.Split(new[] { '/', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                variants.Add(part.Trim());
            }
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var decomposed = value.Normalize(NormalizationForm.FormD).ToLowerInvariant();
            var builder = new StringBuilder(decomposed.Length);
            var pendingSpace = false;
            foreach (var character in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(character))
                {
                    if (pendingSpace && builder.Length > 0)
                    {
                        builder.Append(' ');
                    }

                    builder.Append(character);
                    pendingSpace = false;
                }
                else
                {
                    pendingSpace = true;
                }
            }

            var normalized = builder.ToString().Trim();
            foreach (var article in new[] { "the ", "a ", "an " })
            {
                if (normalized.StartsWith(article, StringComparison.Ordinal) && normalized.Length > article.Length)
                {
                    normalized = normalized.Substring(article.Length);
                    break;
                }
            }

            return normalized;
        }
    }
}
