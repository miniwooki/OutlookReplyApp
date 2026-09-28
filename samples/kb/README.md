# 기술지원 지식 폴더

Outlook 기술지원 자동 답변 애드인이 검색하는 공유 지식 폴더입니다.

- `products.json` — 제품군 목록(id, 표시명, 폴더, 분류 키워드)
- `<제품 폴더>\_prompt.md` — 제품별 답변 지침
- `<제품 폴더>\manuals` — 매뉴얼·기술문서(PDF, DOCX, MD, TXT)
- `<제품 폴더>\replies` — 과거 답변 메일(.msg/.eml, 문체 예시로 사용)
- `<제품 폴더>\faq` — FAQ(MD, DOCX, TXT)
- `<제품 폴더>\issues` — 이슈 목록(XLSX, CSV)
- `_common` — 라이선스·설치 등 모든 제품에 공통으로 검색되는 자료
- `_index` — Indexer가 만드는 색인(직접 수정하지 마세요)
- `_models` — 임베딩 모델(bge-m3-int8)

자료를 바꾼 뒤에는 `TechSupportReply.Indexer.exe index --root <이 폴더>`를 실행하세요.
`_`로 시작하는 하위 폴더와 `~$` 임시 파일은 색인하지 않습니다.
