#!/usr/bin/env bash
# bge-m3 int8 ONNX(약 570MB)와 토크나이저 파일을 models/bge-m3-int8/ 에 내려받는다.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEST="$ROOT/models/bge-m3-int8"
mkdir -p "$DEST"
fetch() { [ -s "$DEST/$2" ] || curl -fL --retry 3 -o "$DEST/$2" "$1"; }
fetch "https://huggingface.co/Xenova/bge-m3/resolve/main/onnx/model_int8.onnx" "model.onnx"
fetch "https://huggingface.co/Xenova/bge-m3/resolve/main/tokenizer.json" "tokenizer.json"
fetch "https://huggingface.co/BAAI/bge-m3/resolve/main/sentencepiece.bpe.model" "sentencepiece.bpe.model"
ls -l "$DEST"
