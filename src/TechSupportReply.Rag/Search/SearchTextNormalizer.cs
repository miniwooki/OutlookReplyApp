using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TechSupportReply.Rag.Search
{
    /// <summary>
    /// FTS5(unicode61)에 넣을 용어를 만든다. 한글·한자·가나는 2글자 bigram(1글자면 그대로),
    /// 영문·숫자는 소문자 단어(2글자 이상)로 만들고, '_'가 있는 단어는 분할 하위어도 추가한다.
    /// </summary>
    public static class SearchTextNormalizer
    {
        private static readonly HashSet<string> StopTerms = new HashSet<string>(StringComparer.Ordinal)
        {
            "the", "and", "for", "you", "are", "with", "this", "that", "have", "from", "please", "hello",
            "thanks", "thank", "regards", "dear",
            "안녕", "녕하", "하세", "세요", "감사", "사합", "합니", "니다", "습니", "입니", "드립", "립니", "문의", "의드",
        };

        public static IReadOnlyList<string> Terms(string text)
        {
            var terms = new List<string>();
            if (string.IsNullOrEmpty(text)) return terms;
            var s = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
            int i = 0;
            while (i < s.Length)
            {
                if (IsCjk(s[i]))
                {
                    int start = i;
                    while (i < s.Length && IsCjk(s[i])) i++;
                    AddCjkRun(s.Substring(start, i - start), terms);
                }
                else if (IsWordChar(s[i]))
                {
                    int start = i;
                    while (i < s.Length && IsWordChar(s[i])) i++;
                    AddWord(s.Substring(start, i - start), terms);
                }
                else
                {
                    i++;
                }
            }
            return terms;
        }

        public static string ToIndexText(string text) => string.Join(" ", Terms(text));

        public static string ToMatchQuery(string text, int maxTerms = 64)
        {
            var distinct = Terms(text).Where(t => !StopTerms.Contains(t)).Distinct().Take(maxTerms).ToList();
            return distinct.Count == 0 ? null : string.Join(" OR ", distinct.Select(t => "\"" + t + "\""));
        }

        private static bool IsCjk(char c) =>
            (c >= '가' && c <= '힣') || (c >= 'ㄱ' && c <= 'ㆎ')
            || (c >= '一' && c <= '鿿') || (c >= '぀' && c <= 'ヿ');

        private static bool IsWordChar(char c) => c == '_' || (char.IsLetterOrDigit(c) && !IsCjk(c));

        private static void AddWord(string word, List<string> terms)
        {
            word = word.Trim('_');
            if (word.Length < 2) return;
            terms.Add(word);
            if (word.IndexOf('_') < 0) return;
            foreach (var part in word.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries))
                if (part.Length >= 2) terms.Add(part);
        }

        private static void AddCjkRun(string run, List<string> terms)
        {
            if (run.Length == 1)
            {
                terms.Add(run);
                return;
            }
            for (int k = 0; k + 1 < run.Length; k++) terms.Add(run.Substring(k, 2));
        }
    }
}
