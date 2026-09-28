"""bge-m3 토크나이저 id와 임베딩 기준값을 생성해 C# 패리티 테스트 픽스처로 저장한다."""
import json
import pathlib
import sys

import numpy as np
import onnxruntime as ort
from tokenizers import Tokenizer

ROOT = pathlib.Path(__file__).resolve().parents[1]
MODEL_DIR = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "models" / "bge-m3-int8"
OUT = ROOT / "tests" / "TechSupportReply.Tests" / "Fixtures" / "bge_m3_reference.json"

TEXTS = [
    "LS-DYNA에서 *CONTACT_AUTOMATIC_SURFACE_TO_SURFACE 사용 시 초기 관통 경고가 발생합니다.",
    "Negative volume error in solid elements during explicit analysis",
    "Fluent 계산이 수렴하지 않습니다. residual이 1e-3에서 멈춥니다.",
    "라이선스 서버(ansyslmd) 연결 실패: FlexNet Licensing error -15,10",
    "HFSS 해석에서 메모리 부족 오류가 납니다",
    "d3hsp 파일과 messag 파일을 첨부했습니다.",
    "How do I set *CONTROL_TIMESTEP to avoid mass scaling issues?",
    "안녕하세요. 문의드립니다.",
    "  공백과\t탭이   섞인   문장  ",
    "ＡＢＣ　전각 문자와 ①②③ 특수기호",
]

tok = Tokenizer.from_file(str(MODEL_DIR / "tokenizer.json"))
sess = ort.InferenceSession(str(MODEL_DIR / "model.onnx"), providers=["CPUExecutionProvider"])
input_names = [i.name for i in sess.get_inputs()]
output_names = [o.name for o in sess.get_outputs()]


def embed(ids):
    arr = np.array([ids], dtype=np.int64)
    feed = {}
    if "input_ids" in input_names:
        feed["input_ids"] = arr
    if "attention_mask" in input_names:
        feed["attention_mask"] = np.ones_like(arr)
    if "token_type_ids" in input_names:
        feed["token_type_ids"] = np.zeros_like(arr)
    outs = dict(zip(output_names, sess.run(None, feed)))
    hidden = outs.get("last_hidden_state", next(iter(outs.values())))
    vec = hidden[0, 0, :] if hidden.ndim == 3 else hidden[0]
    return (vec / np.linalg.norm(vec)).astype(float).tolist()


records = []
for text in TEXTS:
    ids = tok.encode(text).ids
    records.append({"text": text, "ids": ids, "embedding": [round(x, 6) for x in embed(ids)]})

OUT.parent.mkdir(parents=True, exist_ok=True)
OUT.write_text(json.dumps({"model": MODEL_DIR.name, "records": records}, ensure_ascii=False, indent=1), encoding="utf-8")
print(f"inputs={input_names} outputs={output_names}")
print(f"wrote {len(records)} records -> {OUT}")
