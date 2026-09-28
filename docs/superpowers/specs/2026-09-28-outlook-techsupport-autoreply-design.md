# Outlook Classic 기술지원 자동 답변 애드인 — 설계 및 구현 계획

## Context

KOSTECH 기술지원팀은 LS-DYNA, Ansys 제품군 기술지원 메일에 반복적으로 답변하고 있습니다. 이 애드인은 Outlook Classic에서 메일을 선택하면 다음 순서로 동작하여 **회신 초안**을 생성합니다.
1. 제품군을 자동으로 판별하고, 사용자가 확인하거나 변경합니다.
2. 선택된 제품군의 공유 지식 폴더(매뉴얼, 과거 답변, FAQ, 이슈 목록)에서 RAG로 관련 근거를 검색합니다.
3. 등록된 LLM(Claude / OpenAI)으로 답변을 생성합니다.

자동 발송은 하지 않으며, 엔지니어가 검토한 뒤 직접 발송합니다. 사용자는 팀 여러 명이며, 팀원들은 공유 RAG 폴더와 관리자가 만든 중앙 색인을 함께 사용합니다.

### 확정된 결정 사항
| 항목 | 결정 |
|---|---|
| 기술 스택 | **VSTO C# 단일 애드인** (.NET Framework 4.8, Outlook x64 / M365 16.0) |
| LLM | **Anthropic Claude**(공식 `Anthropic` NuGet 12.x) + **OpenAI**(공식 `OpenAI` NuGet 2.x). 프로필을 여러 개 등록할 수 있음(공급자·모델·API 키) |
| 임베딩 | **로컬 ONNX** (기본값 bge-m3 int8 양자화, 다국어 한/영). 문서 내용을 외부로 전송하지 않음 |
| RAG 자료 | PDF, 과거 답변 메일(.msg/.eml), txt/md/docx, xlsx/csv |
| 색인 | **중앙 색인 + 로컬 캐시**: 관리자가 Indexer CLI로 공유 폴더에 색인을 만들고, 클라이언트는 새 버전을 로컬로 복사해 검색만 수행 |
| 발송 | 자동 발송 없음. `MailItem.ReplyAll()` 초안을 만든 뒤 사용자가 검토하고 발송 |

### 가정 (수정 가능)
- 메일을 선택한 뒤 리본 버튼으로 실행합니다(수신 즉시 자동 실행은 하지 않음).
- 답변 언어는 원문 언어(한국어/영어)를 따릅니다.
- 기본 제품군은 LS-DYNA, LS-PrePost/LS-OPT, Ansys Mechanical, Ansys Fluent, Ansys CFX, Ansys Electronics(HFSS/Maxwell), Ansys SpaceClaim/Discovery, 공통(라이선스/설치)입니다. 목록은 `products.json`에서 수정할 수 있습니다.

---

## 1. 아키텍처

```
Outlook (STA UI 스레드)
 └─ TechSupportReply.AddIn (VSTO)
     ├─ Ribbon (Explorer/읽기 Inspector): [기술지원 답변] [설정]
     ├─ TaskPane (WinForms UserControl): 제품군 드롭다운·신뢰도·근거 / LLM 프로필 선택 /
     │                                   [답변 생성][중지][재생성][회신 초안 만들기] / 스트리밍 미리보기 / 참고 문서 목록
     └─ MailExtractor: MailItem → MailSnapshot(POCO) 변환 (UI 스레드에서만 COM 접근)
          │  (백그라운드 Task, CancellationToken)
 ├─ TechSupportReply.Core  (netstandard2.0)
 │   ├─ Settings/SecretStore (JSON + DPAPI)
 │   ├─ ProductCatalog / ProductClassifier (키워드 점수 + LLM 구조화 분류)
 │   ├─ Llm: ILlmProvider ─ AnthropicProvider / OpenAiProvider, LlmProviderFactory
 │   ├─ PromptBuilder (시스템 + 제품별 _prompt.md + 문체 예시 + 근거 + 메일)
 │   └─ ReplyGenerator (오케스트레이션: 검색 → 프롬프트 → 스트리밍 생성)
 ├─ TechSupportReply.Rag   (netstandard2.0)
 │   ├─ Loaders: Pdf(PdfPig) / Docx·Xlsx(OpenXml) / Csv / Msg·Eml(MsgReader) / Txt·Md
 │   ├─ Chunker (문서 유형별 분할 + 메타데이터)
 │   ├─ OnnxEmbedder (OnnxRuntime + SentencePiece 토크나이저)
 │   ├─ SqliteIndexStore (청크·벡터 BLOB·FTS5 trigram)
 │   ├─ HybridRetriever (BM25 + 코사인 → RRF 융합)
 │   └─ IndexCacheSync (공유 → %LOCALAPPDATA% 복사, manifest 비교)
 ├─ TechSupportReply.Indexer (콘솔, net48) — 관리자용 색인 생성/증분 갱신, 작업 스케줄러 등록 가능
 └─ TechSupportReply.Tests (xUnit, net48) — Core/Rag 단위·통합 테스트, FakeLlmProvider
```

**설계 원칙**: Outlook COM 객체는 UI 스레드에서만 사용하고, 읽은 내용은 즉시 `MailSnapshot`(Subject, 정리된 Body, From, 수신일, 텍스트 첨부 요약)으로 복사합니다. LLM 호출·RAG 검색은 비동기로 처리하고, 결과는 `SynchronizationContext`로 UI 스레드에 전달합니다. Outlook 시작 시 애드인 부하 때문에 비활성화되는 문제(resiliency)를 피하기 위해 `ThisAddIn_Startup`에서는 리본 등록만 하고, ONNX 모델과 색인은 첫 사용 시 지연 로딩합니다.

## 2. 공유 RAG 폴더 구조 (관리자 관리)

```
\\server\KB\                      ← 설정의 "RAG 루트"
  products.json                   ← 제품군 id/표시명/폴더/분류 키워드 (팀 공용)
  _common\ ...                    ← 라이선스·설치 등 공통 (모든 제품 검색에 함께 포함)
  LS-DYNA\
    _prompt.md                    ← 제품별 답변 지침·용어·금지사항 (개인화 핵심)
    manuals\*.pdf   replies\*.msg|*.eml   faq\*.md|*.docx|*.txt   issues\*.xlsx|*.csv
  Ansys-Fluent\ ...
  _index\
    manifest.json                 ← 임베딩 모델 id·차원·제품별 색인 버전/해시/빌드시각
    LS-DYNA.sqlite, Ansys-Fluent.sqlite, _common.sqlite ...
  _models\bge-m3-int8\            ← model.onnx, sentencepiece.bpe.model (클라이언트가 로컬 캐시로 복사)
```

- 하위 폴더 이름(`replies`, `manuals` 등)으로 청크의 `doc_type`을 정합니다. `replies`는 **답변 문체 예시(few-shot)**로 사용하고, 나머지는 **근거 자료**로 사용합니다.
- 색인 파일은 Indexer가 임시 파일에 만든 뒤 원자적으로 이름을 바꿉니다. 클라이언트는 SMB 위의 SQLite 파일을 직접 열지 않고, 로컬 캐시로 복사한 뒤 읽기 전용으로 엽니다.
- `manifest.json`의 임베딩 모델 id가 로컬 모델과 다르면 벡터 검색을 끄고 BM25만 사용하며, 경고를 표시합니다.

## 3. 주요 컴포넌트 상세

### 3.1 설정 / 비밀 저장 (`Core/Settings`)
- `%APPDATA%\TechSupportReply\settings.json`에 사용자 프로필(이름·직함·기본 어조), RAG 루트, LLM 프로필 목록, 기본 생성 프로필 id, 분류 프로필 id, top-k 등을 저장합니다.
- `LlmProfile { Id, DisplayName, Provider(Anthropic|OpenAI), Model, BaseUrl?, MaxTokens, Effort?, SecretId }`: 같은 공급자라도 API 키를 여러 개 등록할 수 있습니다(예: 개인 키 / 팀 키).
- API 키는 `secrets.dat`에 `ProtectedData.Protect(..., DataProtectionScope.CurrentUser)`(DPAPI)로 암호화하여 저장하고, settings.json에는 `SecretId`만 둡니다.
- 설정 대화상자(WinForms)에서 프로필 추가/편집/삭제, **[연결 테스트]**(짧은 요청을 보내 확인), RAG 루트 지정, 색인 상태(제품별 버전·빌드시각) 확인, [지금 동기화]를 제공합니다.

### 3.2 LLM 추상화 (`Core/Llm`)
```csharp
interface ILlmProvider {
  Task<string> CompleteAsync(LlmRequest req, CancellationToken ct);            // 분류(JSON)
  Task StreamAsync(LlmRequest req, Action<string> onDelta, CancellationToken ct); // 답변 생성
}
LlmRequest { string System; string CachedSystemPrefix; List<LlmMessage> Messages; int MaxTokens; string JsonSchema? }
```
- **AnthropicProvider**: 공식 `Anthropic` SDK를 사용하고 기본 모델은 `claude-opus-5`입니다. 적응형 사고 `ThinkingConfigAdaptive`, `OutputConfig.Effort`(설정값, 기본 medium), 스트리밍을 사용합니다. 공통 시스템 프롬프트와 제품 지침에는 `CacheControlEphemeral`(프롬프트 캐싱)을 적용합니다. 분류는 `OutputConfig.Format` 구조화 출력으로 처리합니다. `claude-opus-5`의 refusal 대응으로 server-side fallback(`fallbacks: "default"` + 베타 헤더 `server-side-fallback-2026-07-01`)을 기본으로 켜고, 호출 전에 `StopReason == "refusal"`인지 확인합니다. 예외는 SDK 타입(`AnthropicRateLimitException` 등)의 세부 체인으로 처리합니다.
- **OpenAiProvider**: 공식 `OpenAI` SDK의 `ChatClient`를 사용하고, 스트리밍과 JSON schema 응답 형식을 적용합니다. 모델명은 사용자가 입력하는 자유 텍스트입니다.
- `LlmProviderFactory`가 프로필과 비밀값으로 인스턴스를 만듭니다. 테스트에서는 `FakeLlmProvider`를 사용합니다.
- 주의: SDK 코드는 추측하지 않고 claude-api 스킬의 `csharp/` 문서와 컴파일러 오류를 기준으로 작성합니다.

### 3.3 제품군 분류 (`Core/ProductClassifier`)
1. **키워드 점수**: `products.json`의 키워드(예: `*KEYWORD`, `d3plot`, `d3hsp`, `LS-DYNA`, `Fluent`, `Workbench`, `HFSS`, `ansyslmd`)를 제목·본문·첨부파일명에 대해 가중치로 계산합니다.
2. **LLM 분류**: 키워드 상위 후보를 힌트로 넣고 구조화 출력 `{productId, confidence, reason}`을 받습니다. 제품 목록은 enum으로 제한합니다.
3. LLM 분류가 실패하거나 타임아웃되면 키워드 결과를 사용하고, 둘 다 없으면 `_common`을 사용합니다.
4. TaskPane 드롭다운에 판별된 제품, 신뢰도, 판별 근거를 표시합니다. **사용자가 변경하면 그 제품으로 검색·생성**합니다.

### 3.4 RAG (`Rag/*`)
- **청킹**: PDF는 페이지 단위로 약 600토큰씩 나누고 약 80토큰을 겹칩니다(메타: 파일·페이지). DOCX/MD는 제목 단위로 나눕니다. 메일은 1통을 1청크(질문+답변)로 만들되 서명과 인용 이력을 제거합니다. XLSX/CSV는 행 단위(`헤더: 값` 연결)로 나눕니다.
- **저장**: SQLite(`Microsoft.Data.Sqlite`)에 `files(path,size,mtime,hash)`, `chunks(id,file_id,doc_type,title,page,text)`, `vectors(chunk_id, blob)`, `chunks_fts`(FTS5 `unicode61 tokenchars '_'`) 테이블을 둡니다. 색인/질의 텍스트는 `SearchTextNormalizer`로 **한글은 2글자 bigram**, 영문·숫자는 단어(`contact_automatic`과 `_` 분할 하위어 포함)로 변환한 뒤 저장합니다. (trigram 토크나이저는 3글자 미만 질의를 매칭하지 못해 "접촉", "수렴" 같은 2음절 한국어 용어를 찾을 수 없으므로 채택하지 않았습니다.)
- **증분 색인**: 파일 크기·mtime·해시를 비교하여 변경된 파일만 다시 임베딩합니다. 삭제된 파일은 청크를 제거합니다.
- **검색**: 질의 텍스트(제목 + 정리된 본문 앞부분)를 ① FTS5 BM25 top 30, ② 코사인 top 30(제품 벡터를 메모리에 로드, 브루트포스)으로 검색하고 **RRF**로 융합합니다. 결과에서 근거 top 8(선택 제품 + `_common`)과 과거 답변 top 3(문체 예시)을 가져옵니다.
- **임베딩**: `Microsoft.ML.OnnxRuntime`(CPU, x64 네이티브)을 사용합니다. 클라이언트는 질의 1건만 임베딩하므로 빠릅니다.

### 3.5 프롬프트 / 생성 (`Core/PromptBuilder`, `ReplyGenerator`)
- **시스템(캐시)**: KOSTECH 기술지원 엔지니어 역할, 원문 언어로 답변, 근거 문서에 없는 키워드 옵션이나 수치를 지어내지 말 것, 불확실한 부분은 `[확인 필요]`로 표시, 정보가 부족하면 필요한 파일(d3hsp, messag, 버전, 라이선스 로그 등)을 요청할 것, 인사말과 서명 형식.
- **제품 지침(캐시)**: `{제품}\_prompt.md` 전문.
- **사용자 메시지**: 문체 예시(과거 답변) → 근거 청크(`[출처: 파일명 p.12]` 표기) → 고객 메일(MailSnapshot) → 사용자 추가 지시(TaskPane 입력란, 선택).
- 출력은 일반 텍스트(문단)로 받습니다. TaskPane 미리보기에서 편집할 수 있고, **[회신 초안 만들기]**를 누르면 `ReplyAll()`을 만들고 HTMLBody 맨 앞에 HTML 인코딩한 문단을 삽입합니다(기존 서명과 인용 스레드는 보존). 이어서 `Display()`로 창을 엽니다.
- 참고 문서 목록은 TaskPane에만 표시하고 메일 본문에는 넣지 않습니다.

### 3.6 오류 처리 / 로깅
- 잘못된 API 키(401), 429/5xx(SDK 재시도 이후), 타임아웃, 사용자 중지(CancellationToken)는 TaskPane 상태줄에 한국어 메시지로 표시합니다.
- 색인이 없거나 동기화에 실패하면 "RAG 없이 생성"을 선택할 수 있게 하고, 경고 배지를 표시합니다.
- 로그는 `%LOCALAPPDATA%\TechSupportReply\logs\`에 날짜별 파일로 남깁니다. 기본값에서는 메일 본문과 API 키를 기록하지 않습니다.
- 애드인의 모든 이벤트 핸들러는 try/catch로 감싸 Outlook 크래시(→ 애드인 자동 비활성화)를 방지합니다.

## 4. 기술 위험 — Phase 0 스파이크에서 먼저 검증

1. **VSTO에서 네이티브 DLL 로딩**: `onnxruntime.dll`, `e_sqlite3.dll`(SQLitePCLRaw)이 애드인 설치 폴더에서 로드되는지 확인합니다. 필요하면 `SetDllDirectory`로 경로를 지정합니다.
2. **바인딩 리디렉션**: `System.Text.Json 10`, `System.Memory` 등이 충돌하는지 확인합니다. VSTO는 `*.dll.config`를 사용하므로 `AutoGenerateBindingRedirects` + `GenerateBindingRedirectsOutputType`을 설정합니다.
3. **bge-m3 토크나이저**: `Microsoft.ML.Tokenizers` 2.0 `SentencePieceTokenizer`로 XLM-R Unigram 모델과 fairseq id 오프셋을 정확히 처리할 수 있는지 확인합니다. Python `transformers` 기준 토큰 id·임베딩과 비교합니다(코사인 ≥ 0.999). 실패하면 `tokenizer.json` 기반 Unigram 토크나이저를 직접 구현하거나 `Microsoft.ML.OnnxRuntime.Extensions`의 SentencePiece op로 대체합니다.
4. **SDK 동작**: `Anthropic`/`OpenAI` SDK가 net48에서 스트리밍 호출에 성공하는지 확인합니다.

## 5. 구현 단계

| 단계 | 내용 | 산출물 |
|---|---|---|
| 0 | 환경 구성: VS Installer에서 **"Office/SharePoint 개발" 워크로드 추가**, `git init`, 솔루션 골격 생성, 스펙 문서 저장 → **스파이크 4항목 검증** | `TechSupportReply.sln`, `docs/superpowers/specs/2026-09-28-outlook-techsupport-autoreply-design.md` |
| 1 | Core: Settings/SecretStore, ProductCatalog, ILlmProvider + Anthropic/OpenAI 구현, PromptBuilder (TDD) | Core + Tests |
| 2 | Rag: Loaders, Chunker, OnnxEmbedder, SqliteIndexStore, HybridRetriever, IndexCacheSync (TDD, 샘플 문서 픽스처) | Rag + Tests |
| 3 | Indexer CLI: `index --root \\server\KB [--product X] [--full]`, 진행률·요약 출력 | Indexer |
| 4 | AddIn: Ribbon, TaskPane, MailExtractor, 분류→변경→생성(스트리밍)→회신 초안 흐름 | AddIn |
| 5 | 설정 UI: 프로필 CRUD, 연결 테스트, RAG 경로·색인 상태·동기화 | AddIn |
| 6 | 배포 및 문서: ClickOnce 게시 설정, 문서 3종 작성 | `docs/` |

승인 후 첫 작업으로 이 설계를 스펙 파일로 저장하고 커밋합니다. 이어서 `superpowers:writing-plans`로 단계별 세부 작업 계획(파일·테스트 단위)을 만들고, 실행 방식을 선택합니다.

## 6. 문서 (한국어, `docs/`)

1. **`설치가이드.md` — Outlook에 애드인 추가하는 방법** (핵심 요구사항)
   - 사전 요구사항: Outlook Classic(M365/2016+) x64/x86 확인 방법, .NET Framework 4.8, VSTO Runtime(Office 2016+에 포함)
   - **방법 A (권장, 팀 배포)**: ClickOnce로 `\\server\deploy\TechSupportReply\`에 게시하면, 사용자는 `setup.exe`를 실행해 설치하고 이후 자동 업데이트됩니다. 코드 서명 인증서와 신뢰 프롬프트에 대해서도 설명합니다.
   - **방법 B (수동 등록)**: 빌드 산출물을 복사하고 레지스트리 `HKCU\Software\Microsoft\Office\Outlook\Addins\TechSupportReply`(`FriendlyName`, `Description`, `LoadBehavior=3`, `Manifest="file:///...TechSupportReply.AddIn.vsto|vstolocal"`)를 등록하는 방법입니다(.reg 예시 포함).
   - **방법 C (개발자)**: VS에서 F5로 실행하면 자동 등록됩니다.
   - Outlook에서 활성화하기: 파일 → 옵션 → 추가 기능 → COM 추가 기능 [이동] → 체크
   - 문제 해결: `LoadBehavior`가 2로 바뀐 경우, "사용 안 함 항목" 복구, 느린 애드인 자동 비활성화 방지(GPO 관리 애드인 목록), `VSTO_SUPPRESSDISPLAYALERTS=0`으로 로드 오류 표시, 로그 위치
   - 제거 방법
2. **`관리자가이드.md`**: 공유 폴더 구조, `products.json`/`_prompt.md` 작성법, ONNX 모델 배치, Indexer 실행 및 작업 스케줄러 등록
3. **`사용자가이드.md`**: API 키·프로필 등록, 사용 흐름(선택 → 제품군 확인·변경 → 생성 → 초안 검토 → 발송), 개인정보 주의(고객 메일 내용이 선택한 LLM 공급자에게 전송됨)

## 7. 검증

- **단위 테스트** (`dotnet test` / VS 테스트 탐색기): 키워드 분류, DPAPI 암호화 왕복, 청킹 경계, FTS5+벡터 RRF 순위, 증분 색인(변경/삭제), PromptBuilder 출력 스냅샷, 로더별 샘플 파일 파싱
- **스파이크 검증**: 임베딩 결과를 Python 기준과 비교(코사인 ≥ 0.999), VSTO 로드 시 네이티브 DLL과 바인딩 오류가 없는지 확인
- **통합 테스트**(실제 키 필요, 수동 실행 카테고리): Anthropic/OpenAI 각각 분류 JSON과 스트리밍 생성이 성공하는지 확인
- **E2E 수동 시나리오** (Outlook 실행):
  1. 샘플 KB 폴더(LS-DYNA, Fluent 각 PDF 1개·과거 답변 .msg 2개·FAQ md·이슈 csv)로 Indexer를 실행 → manifest와 sqlite 생성 확인
  2. 애드인 로드 → LS-DYNA 문의 메일 선택 → 제품군이 LS-DYNA로 판별되는지 확인 → Fluent로 변경했을 때 검색 근거가 바뀌는지 확인
  3. 답변 생성 중 스트리밍이 표시되는지, [중지]가 동작하는지 확인 → 회신 초안에 서명과 인용이 보존되는지 확인
  4. 프로필을 Claude ↔ OpenAI로 전환해 재생성, 잘못된 키로 연결 테스트 시 오류 메시지 확인
  5. RAG 루트에 접근할 수 없을 때 "RAG 없이 생성" 경고 흐름 확인
  6. `설치가이드.md`의 방법 A/B대로 깨끗한 사용자 프로필에서 설치 → Outlook 재시작 후 리본이 표시되는지 확인
