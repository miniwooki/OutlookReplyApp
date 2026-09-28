using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TechSupportReply.Rag.Loaders;

namespace TechSupportReply.Rag.Indexing
{
    public sealed class ChunkDraft
    {
        public string Title { get; set; } = "";
        public int? Page { get; set; }
        public string Text { get; set; } = "";
    }

    /// <summary>
    /// 섹션을 검색·임베딩 단위 청크로 나눈다. 긴 섹션은 문단 → 줄 → 문장 → 글자 경계 순으로 자르고
    /// 앞 청크의 끝부분을 다음 청크 앞에 겹친다. 메일은 1통 = 1청크로 둔다.
    /// </summary>
    public sealed class Chunker
    {
        public const int MaxEmailChars = 4000;
        private static readonly Regex SentenceBreak = new Regex(@"(?<=[.!?。])\s+");

        private readonly int _maxChars;
        private readonly int _overlapChars;

        public Chunker(int maxChars = 1500, int overlapChars = 200)
        {
            if (maxChars < 20) throw new ArgumentOutOfRangeException(nameof(maxChars));
            if (overlapChars < 0 || overlapChars >= maxChars / 2) throw new ArgumentOutOfRangeException(nameof(overlapChars));
            _maxChars = maxChars;
            _overlapChars = overlapChars;
        }

        public List<ChunkDraft> Chunk(LoadedDocument doc)
        {
            var result = new List<ChunkDraft>();
            foreach (var section in doc.Sections)
            {
                var text = (section.Text ?? "").Replace("\r\n", "\n").Trim();
                if (text.Length == 0) continue;
                if (doc.Kind == DocKind.Email)
                {
                    result.Add(Draft(section, TruncateEmail(text)));
                    continue;
                }
                foreach (var piece in Split(text)) result.Add(Draft(section, piece));
            }
            return result;
        }

        private IEnumerable<string> Split(string text)
        {
            if (text.Length <= _maxChars) return new[] { text };
            int budget = _overlapChars > 0 ? _maxChars - _overlapChars - 1 : _maxChars;
            var chunks = new List<string>();
            string current = "";
            foreach (var segment in Segment(text, budget, 0))
            {
                if (current.Length == 0) current = segment;
                else if (current.Length + 1 + segment.Length <= budget) current += "\n" + segment;
                else
                {
                    chunks.Add(current);
                    current = segment;
                }
            }
            if (current.Length > 0) chunks.Add(current);

            for (int i = chunks.Count - 1; i > 0; i--)
            {
                var overlap = Overlap(chunks[i - 1]);
                if (overlap.Length > 0) chunks[i] = overlap + " " + chunks[i];
            }
            return chunks;
        }

        /// <summary>budget 이하 조각으로 나눈다. level: 0=문단, 1=줄, 2=문장, 3=글자.</summary>
        private static IEnumerable<string> Segment(string text, int budget, int level)
        {
            text = text.Trim();
            if (text.Length == 0) yield break;
            if (text.Length <= budget)
            {
                yield return text;
                yield break;
            }
            if (level >= 3)
            {
                foreach (var hard in HardSplit(text, budget)) yield return hard;
                yield break;
            }
            string[] parts = level == 0 ? Regex.Split(text, @"\n\s*\n")
                : level == 1 ? text.Split('\n')
                : SentenceBreak.Split(text);
            if (parts.Length == 1)
            {
                foreach (var s in Segment(text, budget, level + 1)) yield return s;
                yield break;
            }
            foreach (var part in parts)
                foreach (var s in Segment(part, budget, level + 1)) yield return s;
        }

        private static IEnumerable<string> HardSplit(string text, int budget)
        {
            int pos = 0;
            while (pos < text.Length)
            {
                int len = Math.Min(budget, text.Length - pos);
                if (pos + len < text.Length)
                {
                    int space = text.LastIndexOf(' ', pos + len - 1, len);
                    if (space > pos + budget / 2) len = space - pos;
                }
                var piece = text.Substring(pos, len).Trim();
                if (piece.Length > 0) yield return piece;
                pos += len;
            }
        }

        private string Overlap(string previous)
        {
            if (_overlapChars == 0) return "";
            var tail = previous.Length <= _overlapChars ? previous : previous.Substring(previous.Length - _overlapChars);
            int ws = tail.IndexOfAny(new[] { ' ', '\n' });
            if (ws >= 0 && tail.Length - ws - 1 >= _overlapChars / 2) tail = tail.Substring(ws + 1);
            return tail.Trim();
        }

        private static string TruncateEmail(string text) =>
            text.Length <= MaxEmailChars
                ? text
                : text.Substring(0, MaxEmailChars) + $"\n[... 이하 {text.Length - MaxEmailChars}자 생략 ...]";

        private static ChunkDraft Draft(LoadedSection section, string text) =>
            new ChunkDraft { Title = section.Title ?? "", Page = section.Page, Text = text };
    }
}
