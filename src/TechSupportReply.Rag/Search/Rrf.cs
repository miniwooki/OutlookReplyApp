using System.Collections.Generic;
using System.Linq;

namespace TechSupportReply.Rag.Search
{
    /// <summary>Reciprocal Rank Fusion: score(d) = Σ 1 / (k + rank), rank는 1부터.</summary>
    public static class Rrf
    {
        public static List<KeyValuePair<long, double>> Fuse(IEnumerable<IReadOnlyList<long>> rankings, int k = 60)
        {
            var scores = new Dictionary<long, double>();
            var firstSeen = new Dictionary<long, int>();
            int order = 0;
            foreach (var ranking in rankings)
            {
                for (int i = 0; i < ranking.Count; i++)
                {
                    var id = ranking[i];
                    scores.TryGetValue(id, out var s);
                    scores[id] = s + 1.0 / (k + i + 1);
                    if (!firstSeen.ContainsKey(id)) firstSeen[id] = order++;
                }
            }
            return scores
                .OrderByDescending(p => p.Value)
                .ThenBy(p => firstSeen[p.Key])
                .ToList();
        }
    }
}
