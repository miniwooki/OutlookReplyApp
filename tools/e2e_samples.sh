#!/usr/bin/env bash
# 샘플 지식 폴더로 실제 bge-m3 모델 E2E를 확인한다(메일 발송 없음).
#  1) samples/kb를 임시 폴더로 복사하고 모델을 _models에 넣은 뒤 index
#  2) 같은 질의를 ls-dyna / ansys-fluent로 search해 근거가 제품별로 다른지 확인
#  3) 공유 폴더를 없는 경로로 바꿔 캐시로 search(오프라인 동작)
#  4) ANTHROPIC_API_KEY가 있으면 reply로 실제 답변 생성
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
EXE="$ROOT/src/TechSupportReply.Indexer/bin/Debug/net48/TechSupportReply.Indexer.exe"
MODEL="$ROOT/models/bge-m3-int8"
[ -f "$MODEL/model.onnx" ] || { echo "모델이 없습니다. bash tools/download_model.sh 를 먼저 실행하세요."; exit 1; }
dotnet build "$ROOT/src/TechSupportReply.Indexer" -v q -nologo >/dev/null

TMP="$(mktemp -d)"
KB="$TMP/kb"
CACHE="$TMP/cache"
cp -r "$ROOT/samples/kb" "$KB"
mkdir -p "$KB/_models"
cp -r "$MODEL" "$KB/_models/"
trap 'rm -rf "$TMP"' EXIT

echo "===== 1) index ====="
"$EXE" index --root "$KB" --work "$TMP/work"
test -f "$KB/_index/manifest.json"
test -f "$KB/_index/ls-dyna.sqlite"

Q="접촉 관통 경고 해결 방법"
echo "===== 2a) search ls-dyna ====="
"$EXE" search --root "$KB" --product ls-dyna --query "$Q" --top 4 --cache "$CACHE" | tee "$TMP/dyna.txt"
echo "===== 2b) search ansys-fluent ====="
"$EXE" search --root "$KB" --product ansys-fluent --query "$Q" --top 4 --cache "$CACHE" | tee "$TMP/fluent.txt"
grep -q "\[ls-dyna\]" "$TMP/dyna.txt"
! grep -q "\[ls-dyna\]" "$TMP/fluent.txt"

echo "===== 3) offline search (cache) ====="
"$EXE" search --root "$TMP/offline-share" --product ls-dyna --query "$Q" --top 2 --cache "$CACHE" | tee "$TMP/offline.txt"
grep -q "캐시된 색인을 사용" "$TMP/offline.txt"
grep -q "\[ls-dyna\]" "$TMP/offline.txt"

if [ -n "${ANTHROPIC_API_KEY:-}" ]; then
  echo "===== 4) reply (Claude) ====="
  "$EXE" reply --root "$KB" --mail "$ROOT/samples/mails/lsdyna_contact.txt" --cache "$CACHE"
else
  echo "===== 4) reply 건너뜀: ANTHROPIC_API_KEY 없음 ====="
fi
echo "E2E 완료"
