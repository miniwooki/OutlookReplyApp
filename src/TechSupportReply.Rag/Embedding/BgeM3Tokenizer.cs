using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.ML.Tokenizers;

namespace TechSupportReply.Rag.Embedding
{
    /// <summary>
    /// bge-m3(XLM-RoBERTa) 토크나이저. SentencePiece id에 fairseq 오프셋(+1)을 적용하고
    /// 앞뒤에 &lt;s&gt;(0), &lt;/s&gt;(2)를 붙인다.
    /// </summary>
    public sealed class BgeM3Tokenizer
    {
        public const int ClsId = 0;
        public const int PadId = 1;
        public const int EosId = 2;
        public const int UnkId = 3;
        private const int FairseqOffset = 1;

        private readonly SentencePieceTokenizer _spm;
        private readonly int _spaceId;

        public BgeM3Tokenizer(string sentencePieceModelPath)
        {
            using (var stream = File.OpenRead(sentencePieceModelPath))
            {
                _spm = SentencePieceTokenizer.Create(stream, false, false);
            }
            _spaceId = _spm.Vocabulary.TryGetValue("▁", out var id) ? id : -1;
        }

        public IReadOnlyList<int> Encode(string text, int maxLength)
        {
            if (maxLength < 2) throw new ArgumentOutOfRangeException(nameof(maxLength));
            text = text ?? string.Empty;
            var pieces = new List<int>(_spm.EncodeToIds(text, false, false));
            // HF Metaspace는 끝 공백을 단독 "▁" 조각으로 남기지만 SentencePiece는 제거한다.
            if (pieces.Count > 0 && text.Length > 0 && char.IsWhiteSpace(text[text.Length - 1])
                && pieces[pieces.Count - 1] != _spaceId)
                pieces.Add(_spaceId);
            int bodyMax = maxLength - 2;
            var ids = new List<int>(Math.Min(pieces.Count, bodyMax) + 2) { ClsId };
            for (int i = 0; i < pieces.Count && i < bodyMax; i++)
                ids.Add(pieces[i] == 0 ? UnkId : pieces[i] + FairseqOffset);
            ids.Add(EosId);
            return ids;
        }
    }
}
