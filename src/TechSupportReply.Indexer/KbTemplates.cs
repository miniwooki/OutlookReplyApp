using TechSupportReply.Core.Products;

namespace TechSupportReply.Indexer
{
    /// <summary>init-kb가 만드는 안내 파일 템플릿.</summary>
    internal static class KbTemplates
    {
        public static readonly string[] ProductSubfolders = { "manuals", "replies", "faq", "issues" };

        public static string Prompt(ProductDefinition product) =>
$@"# {product.DisplayName} 답변 지침

이 파일의 내용은 {product.DisplayName} 문의에 답변을 생성할 때 시스템 지침으로 그대로 전달됩니다.
팀의 답변 방식에 맞게 자유롭게 수정하세요.

## 용어·표기
- 키워드 카드, 옵션, 메뉴 이름은 매뉴얼 표기(대소문자 포함)를 그대로 씁니다.
- 제품명은 정식 명칭({product.DisplayName})으로 씁니다.

## 문의 유형별로 요청할 자료
- 해석 오류: 솔버 버전, 오류 메시지 전문, 로그 파일, 입력 파일(가능한 경우)
- 라이선스/설치: 라이선스 서버 로그, 호스트 이름, 오류 코드

## 금지 사항
- 확인되지 않은 버그나 출시 일정을 단정하지 않습니다.
- 고객 데이터나 다른 고객 사례를 언급하지 않습니다.
";

        public const string Readme =
@"# 기술지원 지식 폴더

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
";
    }
}
