# Plan A — 헤드리스 코어 (Core · Rag · Indexer) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Outlook 없이도 동작하는 기술지원 답변 생성 엔진(설정·비밀 저장, 제품군 분류, Claude/OpenAI 호출, 로컬 ONNX 임베딩 기반 하이브리드 RAG, 중앙 색인 생성·로컬 캐시 동기화)과 관리자용 Indexer CLI를 만든다.

**Architecture:** `TechSupportReply.Core`(netstandard2.0)는 도메인 모델, 설정, LLM 공급자 추상화, 분류기, 프롬프트 빌더, 답변 생성기를 담는다. `TechSupportReply.Rag`(netstandard2.0)는 문서 로더, 청킹, ONNX 임베딩, SQLite(FTS5+벡터) 색인, 하이브리드 검색, 공유폴더→로컬 캐시 동기화를 담으며 Core의 `IKnowledgeRetriever`를 구현한다. `TechSupportReply.Indexer`(net48 콘솔)는 관리자 색인 생성과 개발용 검색/답변 명령을 제공한다. Plan B의 VSTO 애드인은 이 두 라이브러리를 그대로 참조한다.

**Tech Stack:** .NET Framework 4.8 / netstandard2.0, C# 12, xUnit 2 + Xunit.SkippableFact, `Anthropic`(공식 C# SDK), `OpenAI`(공식 .NET SDK), `Microsoft.ML.OnnxRuntime`, `Microsoft.ML.Tokenizers`, `Microsoft.Data.Sqlite`(FTS5), `UglyToad.PdfPig`, `DocumentFormat.OpenXml`, `ExcelDataReader`, `MsgReader`, `System.Text.Json`, `System.Security.Cryptography.ProtectedData`. 픽스처/기준값 생성에 Python 3(tokenizers, onnxruntime, python-docx, openpyxl, fpdf2).

**Spec:** `docs/superpowers/specs/2026-09-28-outlook-techsupport-autoreply-design.md`

**범위:** 스펙의 5장 단계 0(스파이크 중 토크나이저·임베딩·SDK 항목), 1, 2, 3. VSTO 애드인·설정 UI·ClickOnce·설치 문서(단계 4–6)와 VSTO 네이티브 DLL/바인딩 리디렉션 스파이크는 **Plan B**에서 다룬다.

## Global Constraints

- 호스트 런타임: .NET Framework 4.8, **x64** (Outlook M365 16.0 x64). 라이브러리는 `netstandard2.0`, 실행 프로젝트(Indexer, Tests)는 `net48` + `<PlatformTarget>x64</PlatformTarget>`.
- C# `LangVersion` 12, `Nullable` disable, `ImplicitUsings` disable. netstandard2.0에 `IsExternalInit`이 없으므로 **`record`와 `init` 접근자를 쓰지 않는다**(일반 `get; set;` 클래스 사용).
- NuGet 패키지는 `dotnet add <proj> package <이름>`으로 추가하여 최신 안정 버전을 고정한다(버전을 추측해 적지 않는다).
- Anthropic 기본 모델 `claude-opus-5`. 적응형 사고(`ThinkingConfigAdaptive`)와 `OutputConfig.Effort`를 지원하는 모델만 허용한다(Claude 4.6 이상). Claude SDK 코드는 claude-api 스킬 `csharp/` 문서의 이름을 따르고, 이름이 맞지 않으면 컴파일러 오류와 `strings ~/.nuget/packages/anthropic/*/lib/netstandard2.0/Anthropic.dll | grep -i <이름>`으로 확인한다. 추측으로 API를 바꾸지 않는다.
- 사용자에게 보이는 모든 메시지와 로그 문구는 **한국어**.
- 메일 자동 발송 없음(Plan A에는 발송 코드 자체가 없다).
- 메일 본문과 API 키는 로그/콘솔 진단 출력에 쓰지 않는다. API 키는 DPAPI `SecretStore` 또는(CLI 개발용) 환경 변수로만 받는다.
- 경로: 설정 `%APPDATA%\TechSupportReply\`, 캐시 `%LOCALAPPDATA%\TechSupportReply\cache\`.
- 임베딩 모델 id는 **모델 폴더 이름**(`bge-m3-int8`), 최대 512 토큰, CLS 풀링 + L2 정규화, 1024차원.
- SQLite 연결은 항상 `Pooling=False`(파일 복사/교체 시 잠금 방지).
- 공유 폴더 파일 교체는 항상 `임시파일 → AtomicFile.Replace`.

## Review Focus

1. **공유 지식 폴더 접근 불가(VPN 끊김·서버 다운)** — 캐시된 색인으로 계속 동작하고, 캐시도 없으면 예외 없이 "RAG 없이 생성" 경고와 함께 답변을 만든다. → Task 19 `Sync_SharedUnreachable_*`, Task 20 `Retrieve_SharedUnreachableAndNoCache_ReturnsEmptyWithWarning`, Task 22 `Generate_RetrieverThrows_WarnsAndStillGenerates`.
2. **손상·암호화·스캔(텍스트 없음) PDF 등 읽을 수 없는 파일** — 해당 파일만 건너뛰고 사유를 보고하며 나머지 색인은 계속된다. → Task 12 `PdfLoader_Scanned/Encrypted`, Task 17 `Build_CorruptFiles_SkippedWithReasonOthersIndexed`.
3. **CP949(ANSI) 인코딩 한글 txt/csv** — 글자 깨짐 없이 읽힌다. → Task 11 `Decode_Cp949`, `CsvLoader_Cp949WithQuotedFields`.
4. **2음절 한국어 기술 용어 질의("접촉", "수렴")** — 키워드 검색에서 매칭된다. → Task 10 `Terms_KoreanTwoSyllable`, Task 15 `SearchKeyword_FindsKoreanTwoSyllableTerm`.
5. **매우 긴 메일 스레드·대용량 로그 첨부** — 조용히 잘리지 않고 `[... 이하 N자 생략 ...]` 표기와 경고가 남는다. → Task 21 `Build_LongBody_TruncatesWithMarkerAndWarning`, `Build_LongAttachment_TruncatesWithWarning`.

---

## File Structure

```
TechSupportReply.sln
Directory.Build.props                      공통 빌드 설정(LangVersion 등)
tools/
  requirements.txt                         Python 도구 의존성
  download_model.sh                        bge-m3 int8 ONNX + 토크나이저 다운로드
  make_reference.py                        토크나이저/임베딩 기준값(JSON) 생성
  make_fixtures.py                         docx/xlsx/pdf/eml/cp949 픽스처 생성
  make_msg.ps1                             Outlook COM으로 .msg 픽스처 생성
src/TechSupportReply.Core/
  Models/MailSnapshot.cs                   Outlook 비의존 메일 POCO
  Knowledge/KnowledgeTypes.cs              KnowledgeChunk, RetrievalResult, IKnowledgeRetriever
  Text/EmailTextCleaner.cs                 인용 이력 분리
  IO/AtomicFile.cs                         원자적 파일 쓰기/교체
  Serialization/JsonDefaults.cs            공용 JSON 옵션
  Settings/AppSettings.cs                  AppSettings, UserProfile, LlmProfile, LlmProviderKind
  Settings/SettingsStore.cs                settings.json 로드/저장
  Settings/SecretStore.cs                  DPAPI API 키 저장소
  Products/ProductDefinition.cs            제품 정의 + products.json 파일 모델
  Products/ProductCatalog.cs               제품 목록 로드/기본값
  Products/ClassificationResult.cs         분류 결과
  Products/KeywordClassifier.cs            키워드 점수 분류
  Products/ProductClassifier.cs            LLM 분류 + 키워드 폴백
  Llm/LlmModels.cs                         LlmRole, LlmMessage, LlmRequest
  Llm/ILlmProvider.cs
  Llm/LlmException.cs                      LlmErrorKind + 한국어 사용자 메시지
  Llm/AnthropicProvider.cs
  Llm/OpenAiProvider.cs
  Llm/LlmProviderFactory.cs
  Prompting/PromptBuilder.cs               PromptInput, BuiltPrompt, PromptBuilder
  Generation/ReplyGenerator.cs             ReplyRequest, ReplyResult, ReplyGenerator
src/TechSupportReply.Rag/
  Embedding/BgeM3Tokenizer.cs              XLM-R SentencePiece + fairseq 오프셋
  Embedding/IEmbedder.cs
  Embedding/VectorMath.cs
  Embedding/OnnxEmbedder.cs
  Search/SearchTextNormalizer.cs           한글 bigram / 영문 단어 용어화
  Search/Rrf.cs                            Reciprocal Rank Fusion
  Search/HybridRetriever.cs                BM25 + 코사인 → RRF
  Loaders/LoadedDocument.cs                LoadedDocument, LoadedSection, DocKind
  Loaders/DocumentLoadException.cs
  Loaders/IDocumentLoader.cs
  Loaders/DocumentLoaderRegistry.cs
  Loaders/TextFileReader.cs                BOM/UTF-8/CP949 판별
  Loaders/TextLoader.cs  MarkdownLoader.cs  CsvLoader.cs(CsvParser, TableSections 포함)
  Loaders/XlsxLoader.cs  DocxLoader.cs  PdfLoader.cs
  Loaders/HtmlText.cs  EmailLoader.cs(MailFileReader 포함)
  Indexing/Chunker.cs                      ChunkDraft, Chunker
  Indexing/IndexBuilder.cs                 IndexBuildReport, SkippedFile, IndexBuilder
  Indexing/KbLayout.cs                     공유 폴더 경로 규칙
  Indexing/IndexManifest.cs                manifest.json 모델
  Indexing/IndexPublisher.cs               로컬 작업 → 공유 폴더 게시
  Store/StoreTypes.cs                      DocType, IndexedFile, NewChunk, StoredChunk
  Store/SqliteIndexStore.cs
  Sync/IndexCacheSync.cs                   SyncResult, IndexCacheSync
  KnowledgeRetriever.cs                    IKnowledgeRetriever 구현
src/TechSupportReply.Indexer/
  Program.cs  CliArgs.cs  ConsoleProgress.cs  KbTemplates.cs
  Commands/IndexCommand.cs  SearchCommand.cs  ReplyCommand.cs  InitKbCommand.cs
tests/TechSupportReply.Tests/
  test.runsettings
  TestSupport/TestPaths.cs  TempDir.cs  BgeReference.cs  FakeLlmProvider.cs  FakeEmbedder.cs  FakeRetriever.cs
  Fixtures/bge_m3_reference.json  Fixtures/docs/*
  (Core|Rag|Indexer 영역별 *Tests.cs)
samples/kb/ …  samples/mails/*.txt        E2E용 샘플 지식 폴더와 문의 메일
```

---

### Task 1: 솔루션 골격과 테스트 실행 환경

**Files:**
- Create: `TechSupportReply.sln`, `Directory.Build.props`
- Create: `src/TechSupportReply.Core/TechSupportReply.Core.csproj`, `src/TechSupportReply.Rag/TechSupportReply.Rag.csproj`, `src/TechSupportReply.Indexer/TechSupportReply.Indexer.csproj`, `src/TechSupportReply.Indexer/Program.cs`
- Create: `tests/TechSupportReply.Tests/TechSupportReply.Tests.csproj`, `tests/TechSupportReply.Tests/test.runsettings`
- Create: `tests/TechSupportReply.Tests/TestSupport/TestPaths.cs`, `tests/TechSupportReply.Tests/TestSupport/TempDir.cs`, `tests/TechSupportReply.Tests/SmokeTests.cs`
- Modify: `.gitignore`

**Interfaces:**
- Produces: `TestPaths.RepoRoot`, `TestPaths.FixturesDir`, `TestPaths.Fixture(params string[])`, `TestPaths.ModelDir`, `TestPaths.ModelAvailable`, `TestPaths.ModelMissingMessage`; `TempDir.Root`, `TempDir.File(string relative, string content = null)`, `TempDir.Sub(string relative)`.

- [ ] **Step 1: 솔루션과 프로젝트 생성**

```bash
cd /d/Develop/claude_outlook_addon_autoreply
dotnet new sln -n TechSupportReply
dotnet new classlib -n TechSupportReply.Core -o src/TechSupportReply.Core -f netstandard2.0
dotnet new classlib -n TechSupportReply.Rag -o src/TechSupportReply.Rag -f netstandard2.0
dotnet new console -n TechSupportReply.Indexer -o src/TechSupportReply.Indexer
dotnet new xunit -n TechSupportReply.Tests -o tests/TechSupportReply.Tests
rm -f src/TechSupportReply.Core/Class1.cs src/TechSupportReply.Rag/Class1.cs tests/TechSupportReply.Tests/UnitTest1.cs
```

- [ ] **Step 2: 공통 빌드 설정과 csproj 내용 작성** (템플릿이 만든 csproj를 아래 내용으로 **덮어쓴다**)

`Directory.Build.props`:
```xml
<Project>
  <PropertyGroup>
    <LangVersion>12.0</LangVersion>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
</Project>
```

`src/TechSupportReply.Core/TechSupportReply.Core.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="TechSupportReply.Tests" />
  </ItemGroup>
</Project>
```

`src/TechSupportReply.Rag/TechSupportReply.Rag.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="TechSupportReply.Tests" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\TechSupportReply.Core\TechSupportReply.Core.csproj" />
  </ItemGroup>
</Project>
```

`src/TechSupportReply.Indexer/TechSupportReply.Indexer.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net48</TargetFramework>
    <PlatformTarget>x64</PlatformTarget>
    <AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects>
    <AssemblyName>TechSupportReply.Indexer</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="TechSupportReply.Tests" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\TechSupportReply.Core\TechSupportReply.Core.csproj" />
    <ProjectReference Include="..\TechSupportReply.Rag\TechSupportReply.Rag.csproj" />
  </ItemGroup>
</Project>
```

`src/TechSupportReply.Indexer/Program.cs` (Task 23에서 교체):
```csharp
namespace TechSupportReply.Indexer
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            return 0;
        }
    }
}
```

`tests/TechSupportReply.Tests/TechSupportReply.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <PlatformTarget>x64</PlatformTarget>
    <IsPackable>false</IsPackable>
    <AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects>
    <GenerateBindingRedirectsOutputType>true</GenerateBindingRedirectsOutputType>
    <RunSettingsFilePath>$(MSBuildProjectDirectory)\test.runsettings</RunSettingsFilePath>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\TechSupportReply.Core\TechSupportReply.Core.csproj" />
    <ProjectReference Include="..\..\src\TechSupportReply.Rag\TechSupportReply.Rag.csproj" />
    <ProjectReference Include="..\..\src\TechSupportReply.Indexer\TechSupportReply.Indexer.csproj" />
  </ItemGroup>
  <ItemGroup>
    <None Include="Fixtures\**\*" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

`tests/TechSupportReply.Tests/test.runsettings`:
```xml
<?xml version="1.0" encoding="utf-8"?>
<RunSettings>
  <RunConfiguration>
    <TargetPlatform>x64</TargetPlatform>
  </RunConfiguration>
</RunSettings>
```

- [ ] **Step 3: 테스트 패키지 추가, 솔루션 등록**

```bash
dotnet add tests/TechSupportReply.Tests package Microsoft.NET.Test.Sdk
dotnet add tests/TechSupportReply.Tests package xunit
dotnet add tests/TechSupportReply.Tests package xunit.runner.visualstudio
dotnet add tests/TechSupportReply.Tests package Xunit.SkippableFact
dotnet sln add src/TechSupportReply.Core src/TechSupportReply.Rag src/TechSupportReply.Indexer tests/TechSupportReply.Tests
```

- [ ] **Step 4: 테스트 지원 클래스 작성**

`tests/TechSupportReply.Tests/TestSupport/TestPaths.cs`:
```csharp
using System;
using System.IO;
using System.Linq;

namespace TechSupportReply.Tests.TestSupport
{
    internal static class TestPaths
    {
        public const string ModelMissingMessage = "임베딩 모델이 없습니다. tools/download_model.sh를 먼저 실행하세요.";

        public static string RepoRoot { get; } = FindRepoRoot();

        public static string FixturesDir => Path.Combine(AppContext.BaseDirectory, "Fixtures");

        public static string Fixture(params string[] parts) =>
            Path.Combine(new[] { FixturesDir }.Concat(parts).ToArray());

        public static string ModelDir =>
            Environment.GetEnvironmentVariable("TSR_MODEL_DIR")
            ?? Path.Combine(RepoRoot, "models", "bge-m3-int8");

        public static bool ModelAvailable =>
            File.Exists(Path.Combine(ModelDir, "model.onnx"))
            && File.Exists(Path.Combine(ModelDir, "sentencepiece.bpe.model"));

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TechSupportReply.sln")))
                dir = dir.Parent;
            if (dir == null) throw new InvalidOperationException("TechSupportReply.sln을 찾을 수 없습니다.");
            return dir.FullName;
        }
    }
}
```

`tests/TechSupportReply.Tests/TestSupport/TempDir.cs`:
```csharp
using System;
using System.IO;
using System.Text;

namespace TechSupportReply.Tests.TestSupport
{
    internal sealed class TempDir : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "tsr-tests", Guid.NewGuid().ToString("N"));

        public TempDir()
        {
            Directory.CreateDirectory(Root);
        }

        /// <summary>상대 경로 파일의 전체 경로를 만들고, content가 있으면 UTF-8(BOM 없음)로 쓴다.</summary>
        public string File(string relative, string content = null)
        {
            var full = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            if (content != null) System.IO.File.WriteAllText(full, content, new UTF8Encoding(false));
            return full;
        }

        public string Sub(string relative)
        {
            var full = Path.Combine(Root, relative);
            Directory.CreateDirectory(full);
            return full;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
```

`tests/TechSupportReply.Tests/SmokeTests.cs`:
```csharp
using System.IO;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests
{
    public class SmokeTests
    {
        [Fact]
        public void RepoRoot_ContainsSolution()
        {
            Assert.True(File.Exists(Path.Combine(TestPaths.RepoRoot, "TechSupportReply.sln")));
        }

        [Fact]
        public void Process_Is64Bit()
        {
            Assert.True(System.Environment.Is64BitProcess, "테스트는 x64로 실행되어야 합니다(ONNX/SQLite 네이티브 DLL).");
        }
    }
}
```

- [ ] **Step 5: 빌드 및 테스트 실행**

Run: `dotnet test tests/TechSupportReply.Tests`
Expected: `Passed!  - Failed: 0, Passed: 2` (net48, x64). `Process_Is64Bit`가 실패하면 test.runsettings가 적용되지 않은 것이므로 `dotnet test tests/TechSupportReply.Tests --settings tests/TechSupportReply.Tests/test.runsettings`로 확인한다.

- [ ] **Step 6: .gitignore 보강 후 커밋**

`.gitignore` 끝에 추가:
```
# Python 도구 가상환경
tools/.venv/
__pycache__/
```

```bash
git add -A
git commit -m "chore: 솔루션 골격과 net48 x64 테스트 환경 구성"
```

---

### Task 2: [스파이크] bge-m3 모델 준비와 토크나이저 패리티

**Files:**
- Create: `tools/requirements.txt`, `tools/download_model.sh`, `tools/make_reference.py`
- Create: `tests/TechSupportReply.Tests/Fixtures/bge_m3_reference.json` (스크립트 생성물, 커밋)
- Create: `src/TechSupportReply.Rag/Embedding/BgeM3Tokenizer.cs`
- Create: `tests/TechSupportReply.Tests/TestSupport/BgeReference.cs`, `tests/TechSupportReply.Tests/Rag/Embedding/BgeM3TokenizerTests.cs`

**Interfaces:**
- Produces: `BgeM3Tokenizer(string sentencePieceModelPath)`, `IReadOnlyList<int> BgeM3Tokenizer.Encode(string text, int maxLength)`; 상수 `ClsId=0, PadId=1, EosId=2, UnkId=3`.
- Produces(테스트): `BgeReference.Load()` → `ReferenceFile { string Model; List<ReferenceRecord> Records }`, `ReferenceRecord { string Text; int[] Ids; float[] Embedding }`.

**스파이크 판정 기준:** 10개 기준 문장 모두 토큰 id가 Python `tokenizers`와 일치하면 통과. Step 6의 보정 후에도 2개 이상 불일치하면 **중단하고 사용자에게 보고**한다(토크나이저 직접 구현은 설계 변경이므로 이 계획 범위 밖).

- [ ] **Step 1: 모델 다운로드 스크립트 작성·실행**

`tools/download_model.sh`:
```bash
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
```

Run: `bash tools/download_model.sh`
Expected: `models/bge-m3-int8/`에 `model.onnx`(약 568MB), `tokenizer.json`(약 17MB), `sentencepiece.bpe.model`(약 5MB).

- [ ] **Step 2: Python 도구 환경과 기준값 생성**

`tools/requirements.txt`:
```
numpy
onnxruntime
tokenizers
python-docx
openpyxl
fpdf2
```

`tools/make_reference.py`:
```python
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
```

Run:
```bash
python -m venv tools/.venv
tools/.venv/Scripts/python -m pip install -r tools/requirements.txt
tools/.venv/Scripts/python tools/make_reference.py
```
Expected: `inputs=['input_ids', 'attention_mask'] outputs=['last_hidden_state'...]`, `wrote 10 records`. (Python 3.14용 휠이 없어 설치가 실패하면 `py -3.12 -m venv tools/.venv`로 다시 만든다.)

- [ ] **Step 3: 실패하는 패리티 테스트 작성**

`tests/TechSupportReply.Tests/TestSupport/BgeReference.cs`:
```csharp
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TechSupportReply.Tests.TestSupport
{
    internal sealed class ReferenceRecord
    {
        public string Text { get; set; }
        public int[] Ids { get; set; }
        public float[] Embedding { get; set; }
    }

    internal sealed class ReferenceFile
    {
        public string Model { get; set; }
        public List<ReferenceRecord> Records { get; set; }
    }

    internal static class BgeReference
    {
        public static ReferenceFile Load() =>
            JsonSerializer.Deserialize<ReferenceFile>(
                File.ReadAllText(TestPaths.Fixture("bge_m3_reference.json")),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
}
```

`tests/TechSupportReply.Tests/Rag/Embedding/BgeM3TokenizerTests.cs`:
```csharp
using System.IO;
using System.Linq;
using TechSupportReply.Rag.Embedding;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Rag.Embedding
{
    [Trait("Category", "Model")]
    public class BgeM3TokenizerTests
    {
        private static BgeM3Tokenizer Create() =>
            new BgeM3Tokenizer(Path.Combine(TestPaths.ModelDir, "sentencepiece.bpe.model"));

        [SkippableFact]
        public void Encode_MatchesPythonTokenizerIds()
        {
            Skip.IfNot(TestPaths.ModelAvailable, TestPaths.ModelMissingMessage);
            var tokenizer = Create();
            var mismatches = BgeReference.Load().Records
                .Select(r => new { r.Text, Expected = r.Ids, Actual = tokenizer.Encode(r.Text, 512).ToArray() })
                .Where(x => !x.Expected.SequenceEqual(x.Actual))
                .Select(x => $"\"{x.Text}\"\n  기대: {string.Join(",", x.Expected)}\n  실제: {string.Join(",", x.Actual)}")
                .ToList();
            Assert.True(mismatches.Count == 0, "토큰 불일치:\n" + string.Join("\n", mismatches));
        }

        [SkippableFact]
        public void Encode_TruncatesToMaxLength_KeepingClsAndEos()
        {
            Skip.IfNot(TestPaths.ModelAvailable, TestPaths.ModelMissingMessage);
            var ids = Create().Encode(string.Join(" ", Enumerable.Repeat("contact", 2000)), 16);
            Assert.Equal(16, ids.Count);
            Assert.Equal(BgeM3Tokenizer.ClsId, ids[0]);
            Assert.Equal(BgeM3Tokenizer.EosId, ids[15]);
        }

        [SkippableFact]
        public void Encode_EmptyText_ReturnsClsEos()
        {
            Skip.IfNot(TestPaths.ModelAvailable, TestPaths.ModelMissingMessage);
            Assert.Equal(new[] { 0, 2 }, Create().Encode("", 512).ToArray());
        }
    }
}
```

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~BgeM3TokenizerTests"`
Expected: 빌드 실패 — `BgeM3Tokenizer` 형식을 찾을 수 없음(CS0246).

- [ ] **Step 4: 패키지 추가와 토크나이저 구현**

```bash
dotnet add src/TechSupportReply.Rag package Microsoft.ML.Tokenizers
```

`src/TechSupportReply.Rag/Embedding/BgeM3Tokenizer.cs`:
```csharp
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

        public BgeM3Tokenizer(string sentencePieceModelPath)
        {
            using (var stream = File.OpenRead(sentencePieceModelPath))
            {
                _spm = SentencePieceTokenizer.Create(stream, false, false);
            }
        }

        public IReadOnlyList<int> Encode(string text, int maxLength)
        {
            if (maxLength < 2) throw new ArgumentOutOfRangeException(nameof(maxLength));
            IReadOnlyList<int> pieces = _spm.EncodeToIds(text ?? string.Empty, false, false);
            int bodyMax = maxLength - 2;
            var ids = new List<int>(Math.Min(pieces.Count, bodyMax) + 2) { ClsId };
            for (int i = 0; i < pieces.Count && i < bodyMax; i++)
                ids.Add(pieces[i] == 0 ? UnkId : pieces[i] + FairseqOffset);
            ids.Add(EosId);
            return ids;
        }
    }
}
```

- [ ] **Step 5: 테스트 실행**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~BgeM3TokenizerTests"`
Expected: PASS 3. 통과하면 Step 7로 간다.

- [ ] **Step 6: (불일치가 있을 때만) 정규화 보정**

불일치가 전각/특수기호 문장(마지막 2개)에만 있으면 `Encode` 첫 줄을 아래처럼 바꾸고(`using System.Text;` 추가) 다시 실행한다. HF 토크나이저의 `nmt_nfkc` 정규화는 NFKC와 거의 같다.
```csharp
            var normalized = (text ?? string.Empty).Normalize(NormalizationForm.FormKC);
            IReadOnlyList<int> pieces = _spm.EncodeToIds(normalized, false, false);
```
Run: 같은 명령. 여전히 2개 이상 불일치하면 **여기서 중단**하고 불일치 출력 전문을 사용자에게 보고한다.

- [ ] **Step 7: 커밋**

```bash
git add tools/ src/TechSupportReply.Rag tests/TechSupportReply.Tests
git commit -m "feat(rag): bge-m3 토크나이저와 Python 기준값 패리티 테스트"
```

---

### Task 3: [스파이크] ONNX 임베더와 임베딩 패리티

**Files:**
- Create: `src/TechSupportReply.Rag/Embedding/IEmbedder.cs`, `VectorMath.cs`, `OnnxEmbedder.cs`
- Test: `tests/TechSupportReply.Tests/Rag/Embedding/VectorMathTests.cs`, `OnnxEmbedderTests.cs`

**Interfaces:**
- Consumes: `BgeM3Tokenizer` (Task 2).
- Produces: `interface IEmbedder { string ModelId { get; } int Dimension { get; } float[] Embed(string text); }`; `OnnxEmbedder(string modelDir, int maxTokens = 512) : IEmbedder, IDisposable` (ModelId = 폴더 이름); `static class VectorMath { float[] Normalize(float[]); float Dot(float[], float[]); byte[] ToBytes(float[]); float[] FromBytes(byte[]); }`.

**스파이크 판정 기준:** 모든 기준 문장에서 C#↔Python 코사인 ≥ 0.999. 성능 로그로 1,500자 청크 1개 임베딩 시간을 기록해 사용자 보고에 포함한다(중앙 색인 소요 시간 추정용).

- [ ] **Step 1: VectorMath 실패 테스트 작성**

`tests/TechSupportReply.Tests/Rag/Embedding/VectorMathTests.cs`:
```csharp
using System;
using TechSupportReply.Rag.Embedding;
using Xunit;

namespace TechSupportReply.Tests.Rag.Embedding
{
    public class VectorMathTests
    {
        [Fact]
        public void Normalize_ProducesUnitLength()
        {
            var v = VectorMath.Normalize(new float[] { 3, 4 });
            Assert.Equal(0.6f, v[0], 5);
            Assert.Equal(0.8f, v[1], 5);
        }

        [Fact]
        public void Normalize_ZeroVector_ReturnsZeros()
        {
            Assert.Equal(new float[] { 0, 0 }, VectorMath.Normalize(new float[] { 0, 0 }));
        }

        [Fact]
        public void Dot_ComputesSum()
        {
            Assert.Equal(11f, VectorMath.Dot(new float[] { 1, 2 }, new float[] { 3, 4 }));
        }

        [Fact]
        public void Dot_DimensionMismatch_Throws()
        {
            Assert.Throws<ArgumentException>(() => VectorMath.Dot(new float[] { 1 }, new float[] { 1, 2 }));
        }

        [Fact]
        public void Bytes_RoundTrip()
        {
            var v = new[] { 0.25f, -1.5f, 3.125f };
            Assert.Equal(v, VectorMath.FromBytes(VectorMath.ToBytes(v)));
        }
    }
}
```

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~VectorMathTests"`
Expected: 빌드 실패(CS0103 `VectorMath` 없음).

- [ ] **Step 2: IEmbedder, VectorMath 구현**

`src/TechSupportReply.Rag/Embedding/IEmbedder.cs`:
```csharp
namespace TechSupportReply.Rag.Embedding
{
    public interface IEmbedder
    {
        /// <summary>색인 호환성 판정용 모델 식별자(모델 폴더 이름).</summary>
        string ModelId { get; }
        int Dimension { get; }
        /// <summary>L2 정규화된 벡터를 반환한다.</summary>
        float[] Embed(string text);
    }
}
```

`src/TechSupportReply.Rag/Embedding/VectorMath.cs`:
```csharp
using System;

namespace TechSupportReply.Rag.Embedding
{
    public static class VectorMath
    {
        public static float[] Normalize(float[] v)
        {
            double sum = 0;
            foreach (var x in v) sum += (double)x * x;
            var norm = Math.Sqrt(sum);
            var result = new float[v.Length];
            if (norm == 0) return result;
            for (int i = 0; i < v.Length; i++) result[i] = (float)(v[i] / norm);
            return result;
        }

        public static float Dot(float[] a, float[] b)
        {
            if (a.Length != b.Length) throw new ArgumentException("벡터 차원이 다릅니다.");
            float sum = 0;
            for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
            return sum;
        }

        public static byte[] ToBytes(float[] v)
        {
            var bytes = new byte[v.Length * sizeof(float)];
            Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        public static float[] FromBytes(byte[] bytes)
        {
            var v = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, v, 0, v.Length * sizeof(float));
            return v;
        }
    }
}
```

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~VectorMathTests"`
Expected: PASS 5.

- [ ] **Step 3: OnnxEmbedder 실패 테스트 작성**

`tests/TechSupportReply.Tests/Rag/Embedding/OnnxEmbedderTests.cs`:
```csharp
using System.Diagnostics;
using System.Linq;
using TechSupportReply.Rag.Embedding;
using TechSupportReply.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace TechSupportReply.Tests.Rag.Embedding
{
    [Trait("Category", "Model")]
    public class OnnxEmbedderTests
    {
        private readonly ITestOutputHelper _output;

        public OnnxEmbedderTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [SkippableFact]
        public void Embed_MatchesPythonReference()
        {
            Skip.IfNot(TestPaths.ModelAvailable, TestPaths.ModelMissingMessage);
            using (var embedder = new OnnxEmbedder(TestPaths.ModelDir))
            {
                foreach (var r in BgeReference.Load().Records)
                {
                    var cos = VectorMath.Dot(VectorMath.Normalize(r.Embedding), embedder.Embed(r.Text));
                    Assert.True(cos >= 0.999f, $"코사인 {cos:0.00000} < 0.999: \"{r.Text}\"");
                }
            }
        }

        [SkippableFact]
        public void ModelIdAndDimension()
        {
            Skip.IfNot(TestPaths.ModelAvailable, TestPaths.ModelMissingMessage);
            using (var embedder = new OnnxEmbedder(TestPaths.ModelDir))
            {
                Assert.Equal("bge-m3-int8", embedder.ModelId);
                Assert.Equal(1024, embedder.Dimension);
            }
        }

        [SkippableFact]
        public void KoreanQuery_IsCloserToRelatedEnglish_ThanUnrelated()
        {
            Skip.IfNot(TestPaths.ModelAvailable, TestPaths.ModelMissingMessage);
            using (var embedder = new OnnxEmbedder(TestPaths.ModelDir))
            {
                var q = embedder.Embed("접촉 초기 관통 경고");
                var related = VectorMath.Dot(q, embedder.Embed("initial penetration warning in contact definition"));
                var unrelated = VectorMath.Dot(q, embedder.Embed("license server installation guide"));
                Assert.True(related > unrelated, $"related={related:0.000}, unrelated={unrelated:0.000}");
            }
        }

        [SkippableFact]
        public void Performance_LogsChunkEmbeddingTime()
        {
            Skip.IfNot(TestPaths.ModelAvailable, TestPaths.ModelMissingMessage);
            var chunk = string.Concat(Enumerable.Repeat("LS-DYNA 접촉 정의에서 SOFT=2 옵션은 세그먼트 기반 접촉을 사용합니다. ", 30));
            using (var embedder = new OnnxEmbedder(TestPaths.ModelDir))
            {
                embedder.Embed(chunk);
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 10; i++) embedder.Embed(chunk);
                _output.WriteLine($"청크 {chunk.Length}자 임베딩 평균 {sw.ElapsedMilliseconds / 10.0:0} ms");
            }
        }
    }
}
```

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~OnnxEmbedderTests"`
Expected: 빌드 실패(CS0246 `OnnxEmbedder`).

- [ ] **Step 4: 패키지 추가와 OnnxEmbedder 구현**

```bash
dotnet add src/TechSupportReply.Rag package Microsoft.ML.OnnxRuntime
```

`src/TechSupportReply.Rag/Embedding/OnnxEmbedder.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace TechSupportReply.Rag.Embedding
{
    /// <summary>bge-m3 ONNX 임베더: CLS 토큰 은닉 상태를 L2 정규화해 반환한다.</summary>
    public sealed class OnnxEmbedder : IEmbedder, IDisposable
    {
        private readonly InferenceSession _session;
        private readonly BgeM3Tokenizer _tokenizer;
        private readonly int _maxTokens;
        private readonly object _lock = new object();

        public string ModelId { get; }
        public int Dimension { get; }

        public OnnxEmbedder(string modelDir, int maxTokens = 512)
        {
            ModelId = new DirectoryInfo(modelDir).Name;
            _tokenizer = new BgeM3Tokenizer(Path.Combine(modelDir, "sentencepiece.bpe.model"));
            var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            _session = new InferenceSession(Path.Combine(modelDir, "model.onnx"), options);
            _maxTokens = maxTokens;
            Dimension = Embed("dimension probe").Length;
        }

        public float[] Embed(string text)
        {
            var ids = _tokenizer.Encode(text, _maxTokens);
            int n = ids.Count;
            var inputIds = new DenseTensor<long>(new[] { 1, n });
            var mask = new DenseTensor<long>(new[] { 1, n });
            for (int i = 0; i < n; i++)
            {
                inputIds[0, i] = ids[i];
                mask[0, i] = 1;
            }

            var inputs = new List<NamedOnnxValue>();
            foreach (var name in _session.InputMetadata.Keys)
            {
                if (name == "input_ids") inputs.Add(NamedOnnxValue.CreateFromTensor(name, inputIds));
                else if (name == "attention_mask") inputs.Add(NamedOnnxValue.CreateFromTensor(name, mask));
                else if (name == "token_type_ids") inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(new[] { 1, n })));
            }

            lock (_lock)
            {
                using (var results = _session.Run(inputs))
                {
                    var output = results.FirstOrDefault(r => r.Name == "last_hidden_state") ?? results.First();
                    var tensor = output.AsTensor<float>();
                    int dim = tensor.Dimensions[tensor.Dimensions.Length - 1];
                    var vector = new float[dim];
                    for (int d = 0; d < dim; d++)
                        vector[d] = tensor.Dimensions.Length == 3 ? tensor[0, 0, d] : tensor[0, d];
                    return VectorMath.Normalize(vector);
                }
            }
        }

        public void Dispose()
        {
            _session.Dispose();
        }
    }
}
```

- [ ] **Step 5: 테스트 실행**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~OnnxEmbedderTests" --logger "console;verbosity=detailed"`
Expected: PASS 4, 출력에 `청크 ...자 임베딩 평균 N ms`. 이 N 값을 기록해 둔다(스파이크 결과 보고에 포함).

- [ ] **Step 6: 커밋**

```bash
git add src/TechSupportReply.Rag tests/TechSupportReply.Tests
git commit -m "feat(rag): ONNX bge-m3 임베더와 임베딩 패리티 테스트"
```

---

### Task 4: Core 도메인 모델과 메일 인용 분리기

**Files:**
- Create: `src/TechSupportReply.Core/Models/MailSnapshot.cs`, `src/TechSupportReply.Core/Knowledge/KnowledgeTypes.cs`, `src/TechSupportReply.Core/Text/EmailTextCleaner.cs`
- Test: `tests/TechSupportReply.Tests/Core/Text/EmailTextCleanerTests.cs`, `tests/TechSupportReply.Tests/Core/Knowledge/KnowledgeChunkTests.cs`

**Interfaces:**
- Produces: `MailSnapshot { string Subject; string Body; string SenderName; string SenderEmail; DateTime ReceivedAt; List<string> AttachmentNames; string AttachmentText }` (모두 get/set, 문자열 기본값 `""`).
- Produces: `KnowledgeChunk { string ProductId; string SourceFile; string Title; int? Page; string Text; bool IsReplyExample; double Score; string Citation (읽기 전용) }`; `RetrievalResult { List<KnowledgeChunk> References; List<KnowledgeChunk> StyleExamples; List<string> Warnings }` (get-only, 초기화됨); `interface IKnowledgeRetriever { Task<RetrievalResult> RetrieveAsync(string productId, string query, int referenceTopK, int styleTopK, CancellationToken ct); }`.
- Produces: `EmailParts { string Latest; string Quoted }`, `static EmailParts EmailTextCleaner.Split(string body)`.

- [ ] **Step 1: 실패 테스트 작성**

`tests/TechSupportReply.Tests/Core/Text/EmailTextCleanerTests.cs`:
```csharp
using TechSupportReply.Core.Text;
using Xunit;

namespace TechSupportReply.Tests.Core.Text
{
    public class EmailTextCleanerTests
    {
        [Fact]
        public void Split_OutlookEnglishSeparator()
        {
            var body = "Hi,\r\n\r\nPlease check.\r\n\r\n________________________________\r\nFrom: Kim <k@x.com>\r\nSent: Monday, September 1, 2026 10:00 AM\r\nTo: support@x.com\r\nSubject: RE: issue\r\n\r\nOriginal question";
            var parts = EmailTextCleaner.Split(body);
            Assert.Equal("Hi,\n\nPlease check.", parts.Latest);
            Assert.Contains("Original question", parts.Quoted);
        }

        [Fact]
        public void Split_OutlookKoreanHeaders()
        {
            var body = "답변입니다.\n\n보낸 사람: 홍길동 <h@x.com>\n보낸 날짜: 2026년 9월 1일 월요일 오전 10:00\n받는 사람: support@x.com\n제목: 문의\n\n문의 내용";
            var parts = EmailTextCleaner.Split(body);
            Assert.Equal("답변입니다.", parts.Latest);
            Assert.Contains("문의 내용", parts.Quoted);
        }

        [Fact]
        public void Split_GmailWroteLineWithQuoteMarkers()
        {
            var body = "Thanks!\n\nOn Mon, Sep 1, 2026 at 10:00 AM Kim <k@x.com> wrote:\n> Question line\n> second";
            var parts = EmailTextCleaner.Split(body);
            Assert.Equal("Thanks!", parts.Latest);
            Assert.Contains("Question line\nsecond", parts.Quoted);
            Assert.DoesNotContain("> Question", parts.Quoted);
        }

        [Fact]
        public void Split_OriginalMessageMarker()
        {
            var parts = EmailTextCleaner.Split("확인했습니다.\n\n-----Original Message-----\nFrom: a\n이전 내용");
            Assert.Equal("확인했습니다.", parts.Latest);
            Assert.Contains("이전 내용", parts.Quoted);
        }

        [Fact]
        public void Split_InlineQuotedLines_MoveToQuoted()
        {
            var parts = EmailTextCleaner.Split("네 맞습니다.\n> 이렇게 하면 되나요?\n추가 설명");
            Assert.Equal("네 맞습니다.\n추가 설명", parts.Latest);
            Assert.Contains("이렇게 하면 되나요?", parts.Quoted);
        }

        [Fact]
        public void Split_NoQuote_ReturnsWholeTrimmed()
        {
            var parts = EmailTextCleaner.Split("  a\r\n\r\n\r\n\r\nb  ");
            Assert.Equal("a\n\nb", parts.Latest);
            Assert.Equal("", parts.Quoted);
        }

        [Fact]
        public void Split_Null_ReturnsEmpty()
        {
            var parts = EmailTextCleaner.Split(null);
            Assert.Equal("", parts.Latest);
            Assert.Equal("", parts.Quoted);
        }
    }
}
```

`tests/TechSupportReply.Tests/Core/Knowledge/KnowledgeChunkTests.cs`:
```csharp
using TechSupportReply.Core.Knowledge;
using Xunit;

namespace TechSupportReply.Tests.Core.Knowledge
{
    public class KnowledgeChunkTests
    {
        [Fact]
        public void Citation_WithPage()
        {
            var c = new KnowledgeChunk { SourceFile = @"manuals\Keyword_Vol_I.pdf", Page = 12 };
            Assert.Equal("Keyword_Vol_I.pdf p.12", c.Citation);
        }

        [Fact]
        public void Citation_WithoutPage()
        {
            Assert.Equal("faq.md", new KnowledgeChunk { SourceFile = @"faq\faq.md" }.Citation);
        }

        [Fact]
        public void Citation_MissingSource()
        {
            Assert.Equal("(출처 미상)", new KnowledgeChunk().Citation);
        }
    }
}
```

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~Core"`
Expected: 빌드 실패(형식 없음).

- [ ] **Step 2: 구현**

`src/TechSupportReply.Core/Models/MailSnapshot.cs`:
```csharp
using System;
using System.Collections.Generic;

namespace TechSupportReply.Core.Models
{
    /// <summary>Outlook COM에 의존하지 않는 메일 사본. 애드인은 UI 스레드에서 이 객체로 복사한 뒤 COM 객체를 놓는다.</summary>
    public sealed class MailSnapshot
    {
        public string Subject { get; set; } = "";
        public string Body { get; set; } = "";
        public string SenderName { get; set; } = "";
        public string SenderEmail { get; set; } = "";
        public DateTime ReceivedAt { get; set; }
        public List<string> AttachmentNames { get; set; } = new List<string>();
        public string AttachmentText { get; set; } = "";
    }
}
```

`src/TechSupportReply.Core/Knowledge/KnowledgeTypes.cs`:
```csharp
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TechSupportReply.Core.Knowledge
{
    public sealed class KnowledgeChunk
    {
        public string ProductId { get; set; } = "";
        /// <summary>제품 폴더 기준 상대 경로.</summary>
        public string SourceFile { get; set; } = "";
        public string Title { get; set; } = "";
        public int? Page { get; set; }
        public string Text { get; set; } = "";
        public bool IsReplyExample { get; set; }
        public double Score { get; set; }

        public string Citation
        {
            get
            {
                var name = string.IsNullOrEmpty(SourceFile) ? "(출처 미상)" : Path.GetFileName(SourceFile);
                return Page.HasValue ? $"{name} p.{Page.Value}" : name;
            }
        }
    }

    public sealed class RetrievalResult
    {
        public List<KnowledgeChunk> References { get; } = new List<KnowledgeChunk>();
        public List<KnowledgeChunk> StyleExamples { get; } = new List<KnowledgeChunk>();
        public List<string> Warnings { get; } = new List<string>();
    }

    public interface IKnowledgeRetriever
    {
        Task<RetrievalResult> RetrieveAsync(string productId, string query, int referenceTopK, int styleTopK, CancellationToken ct);
    }
}
```

`src/TechSupportReply.Core/Text/EmailTextCleaner.cs`:
```csharp
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TechSupportReply.Core.Text
{
    public sealed class EmailParts
    {
        public string Latest { get; set; } = "";
        public string Quoted { get; set; } = "";
    }

    /// <summary>메일 본문을 최신 작성분과 인용된 이전 스레드로 분리한다.</summary>
    public static class EmailTextCleaner
    {
        private const RegexOptions Ml = RegexOptions.Multiline;
        private const RegexOptions MlIc = RegexOptions.Multiline | RegexOptions.IgnoreCase;

        private static readonly Regex[] SeparatorPatterns =
        {
            new Regex(@"^-{2,}\s*(Original Message|원본 메시지|Forwarded message|전달된 메시지)\s*-{2,}\s*$", MlIc),
            new Regex(@"^_{10,}\s*$", Ml),
            new Regex(@"^(From|보낸 사람|보낸사람)\s*:[^\n]*\n(?:[^\n]*\n){0,2}?(Sent|Date|보낸 날짜|날짜)\s*:", MlIc),
            new Regex(@"^On\s.{5,200}\swrote:\s*$", MlIc),
            new Regex(@"^\d{4}[.\-/]\s?\d{1,2}[.\-/]\s?\d{1,2}.{0,60}작성:\s*$", Ml),
        };

        private static readonly Regex ExcessBlankLines = new Regex(@"\n{3,}");
        private static readonly Regex QuoteMarker = new Regex(@"^[ \t]*(>[ \t]?)+", Ml);

        public static EmailParts Split(string body)
        {
            var text = Normalize(body);
            int cut = text.Length;
            foreach (var pattern in SeparatorPatterns)
            {
                var m = pattern.Match(text);
                if (m.Success && m.Index < cut) cut = m.Index;
            }

            var latestLines = new List<string>();
            var inlineQuoted = new List<string>();
            foreach (var line in text.Substring(0, cut).Split('\n'))
            {
                if (line.TrimStart().StartsWith(">")) inlineQuoted.Add(line);
                else latestLines.Add(line);
            }

            var quoted = string.Join("\n", inlineQuoted.Concat(new[] { text.Substring(cut) }));
            return new EmailParts
            {
                Latest = Tidy(string.Join("\n", latestLines)),
                Quoted = Tidy(QuoteMarker.Replace(quoted, "")),
            };
        }

        private static string Normalize(string body) =>
            (body ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u00A0', ' ');

        private static string Tidy(string text)
        {
            var lines = text.Split('\n').Select(l => l.TrimEnd());
            return ExcessBlankLines.Replace(string.Join("\n", lines), "\n\n").Trim();
        }
    }
}
```

- [ ] **Step 3: 테스트 실행**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~Core"`
Expected: PASS 10.

- [ ] **Step 4: 커밋**

```bash
git add src/TechSupportReply.Core tests/TechSupportReply.Tests
git commit -m "feat(core): MailSnapshot, 지식 청크 모델, 메일 인용 분리기"
```

---

### Task 5: 설정 저장소와 DPAPI 비밀 저장소

**Files:**
- Create: `src/TechSupportReply.Core/IO/AtomicFile.cs`, `src/TechSupportReply.Core/Serialization/JsonDefaults.cs`
- Create: `src/TechSupportReply.Core/Settings/AppSettings.cs`, `SettingsStore.cs`, `SecretStore.cs`
- Test: `tests/TechSupportReply.Tests/Core/Settings/SettingsStoreTests.cs`, `SecretStoreTests.cs`, `tests/TechSupportReply.Tests/Core/IO/AtomicFileTests.cs`

**Interfaces:**
- Produces: `AtomicFile.WriteAllText(string path, string content)`, `AtomicFile.WriteAllBytes(string path, byte[] bytes)`, `AtomicFile.Replace(string source, string destination)`.
- Produces: `JsonDefaults.Options` (camelCase, 들여쓰기, enum 문자열, 대소문자 무시, 한글 비이스케이프).
- Produces: `enum LlmProviderKind { Anthropic, OpenAI }`; `LlmProfile { Id; DisplayName; Provider; Model; BaseUrl; MaxTokens=16000; Effort="medium"; SecretId }`; `UserProfile { Name; Title; Company="KOSTECH"; Tone }`; `AppSettings { SchemaVersion; User; RagRoot; Profiles; DefaultProfileId; ClassifierProfileId; ReferenceTopK=8; StyleExampleTopK=3; MaxMailChars=30000; LlmProfile FindProfile(string id) }`.
- Produces: `SettingsStore(string directory)`, `static string SettingsStore.DefaultDirectory`, `AppSettings Load()`, `void Save(AppSettings)`, `string FilePath`.
- Produces: `SecretStore(string directory)`, `string Get(string id)`(없으면 null), `void Set(string id, string value)`, `void Remove(string id)`, `static string SecretStore.NewId()`.

- [ ] **Step 1: 실패 테스트 작성**

`tests/TechSupportReply.Tests/Core/IO/AtomicFileTests.cs`:
```csharp
using System.IO;
using TechSupportReply.Core.IO;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Core.IO
{
    public class AtomicFileTests
    {
        [Fact]
        public void WriteAllText_CreatesDirectoryAndOverwrites()
        {
            using (var tmp = new TempDir())
            {
                var path = Path.Combine(tmp.Root, "a", "b", "file.json");
                AtomicFile.WriteAllText(path, "첫번째");
                AtomicFile.WriteAllText(path, "두번째");
                Assert.Equal("두번째", File.ReadAllText(path));
                Assert.False(File.Exists(path + ".tmp"));
            }
        }
    }
}
```

`tests/TechSupportReply.Tests/Core/Settings/SettingsStoreTests.cs`:
```csharp
using System.IO;
using System.Linq;
using TechSupportReply.Core.Settings;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Core.Settings
{
    public class SettingsStoreTests
    {
        [Fact]
        public void Load_WhenMissing_ReturnsDefaults()
        {
            using (var tmp = new TempDir())
            {
                var s = new SettingsStore(tmp.Root).Load();
                Assert.Empty(s.Profiles);
                Assert.Equal(8, s.ReferenceTopK);
                Assert.Equal("KOSTECH", s.User.Company);
            }
        }

        [Fact]
        public void SaveThenLoad_RoundTripsProfiles_WithEnumAsString()
        {
            using (var tmp = new TempDir())
            {
                var store = new SettingsStore(tmp.Root);
                var settings = new AppSettings { RagRoot = @"\\server\KB" };
                var profile = new LlmProfile { DisplayName = "Claude 팀 키", Provider = LlmProviderKind.Anthropic, Model = "claude-opus-5", SecretId = "s1" };
                settings.Profiles.Add(profile);
                settings.DefaultProfileId = profile.Id;
                store.Save(settings);

                Assert.Contains("\"Anthropic\"", File.ReadAllText(store.FilePath));
                Assert.Contains("Claude 팀 키", File.ReadAllText(store.FilePath));

                var loaded = store.Load();
                Assert.Equal(@"\\server\KB", loaded.RagRoot);
                var p = loaded.FindProfile(profile.Id);
                Assert.Equal("claude-opus-5", p.Model);
                Assert.Equal(LlmProviderKind.Anthropic, p.Provider);
            }
        }

        [Fact]
        public void Load_WhenCorrupted_BacksUpAndReturnsDefaults()
        {
            using (var tmp = new TempDir())
            {
                var store = new SettingsStore(tmp.Root);
                File.WriteAllText(store.FilePath, "{ not json");
                var s = store.Load();
                Assert.Empty(s.Profiles);
                Assert.True(Directory.GetFiles(tmp.Root, "settings.json.bad-*").Any());
            }
        }
    }
}
```

`tests/TechSupportReply.Tests/Core/Settings/SecretStoreTests.cs`:
```csharp
using System;
using System.IO;
using System.Text;
using TechSupportReply.Core.Settings;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Core.Settings
{
    public class SecretStoreTests
    {
        [Fact]
        public void SetThenGet_RoundTripsAcrossInstances()
        {
            using (var tmp = new TempDir())
            {
                new SecretStore(tmp.Root).Set("k1", "sk-test-123");
                Assert.Equal("sk-test-123", new SecretStore(tmp.Root).Get("k1"));
            }
        }

        [Fact]
        public void File_DoesNotContainPlaintext()
        {
            using (var tmp = new TempDir())
            {
                new SecretStore(tmp.Root).Set("k1", "sk-plaintext-marker");
                var bytes = File.ReadAllBytes(Path.Combine(tmp.Root, SecretStore.FileName));
                Assert.DoesNotContain("sk-plaintext-marker", Encoding.UTF8.GetString(bytes));
            }
        }

        [Fact]
        public void Get_Unknown_ReturnsNull()
        {
            using (var tmp = new TempDir())
            {
                Assert.Null(new SecretStore(tmp.Root).Get("nope"));
                Assert.Null(new SecretStore(tmp.Root).Get(null));
            }
        }

        [Fact]
        public void Remove_DeletesKey()
        {
            using (var tmp = new TempDir())
            {
                var store = new SecretStore(tmp.Root);
                store.Set("k1", "v");
                store.Remove("k1");
                Assert.Null(store.Get("k1"));
            }
        }

        [Fact]
        public void Get_CorruptedFile_ThrowsFriendlyError()
        {
            using (var tmp = new TempDir())
            {
                File.WriteAllBytes(Path.Combine(tmp.Root, SecretStore.FileName), new byte[] { 1, 2, 3, 4 });
                var ex = Assert.Throws<InvalidOperationException>(() => new SecretStore(tmp.Root).Get("k1"));
                Assert.Contains("다시 등록", ex.Message);
            }
        }

        [Fact]
        public void Set_OnCorruptedFile_StartsFresh()
        {
            using (var tmp = new TempDir())
            {
                File.WriteAllBytes(Path.Combine(tmp.Root, SecretStore.FileName), new byte[] { 1, 2, 3, 4 });
                var store = new SecretStore(tmp.Root);
                store.Set("k2", "v2");
                Assert.Equal("v2", store.Get("k2"));
            }
        }
    }
}
```

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~Core.Settings|FullyQualifiedName~Core.IO"`
Expected: 빌드 실패(형식 없음).

- [ ] **Step 2: 패키지 추가와 구현**

```bash
dotnet add src/TechSupportReply.Core package System.Text.Json
dotnet add src/TechSupportReply.Core package System.Security.Cryptography.ProtectedData
```

`src/TechSupportReply.Core/IO/AtomicFile.cs`:
```csharp
using System.IO;
using System.Text;

namespace TechSupportReply.Core.IO
{
    public static class AtomicFile
    {
        public static void WriteAllText(string path, string content) =>
            WriteAllBytes(path, new UTF8Encoding(false).GetBytes(content));

        public static void WriteAllBytes(string path, byte[] bytes)
        {
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            var tmp = full + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            Replace(tmp, full);
        }

        /// <summary>source를 destination으로 교체한다. SMB 등에서 File.Replace가 실패하면 복사 후 삭제로 대체한다.</summary>
        public static void Replace(string source, string destination)
        {
            if (!File.Exists(destination))
            {
                File.Move(source, destination);
                return;
            }
            try
            {
                File.Replace(source, destination, null);
            }
            catch (IOException)
            {
                File.Copy(source, destination, true);
                File.Delete(source);
            }
        }
    }
}
```

`src/TechSupportReply.Core/Serialization/JsonDefaults.cs`:
```csharp
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TechSupportReply.Core.Serialization
{
    public static class JsonDefaults
    {
        public static readonly JsonSerializerOptions Options = Create();

        private static JsonSerializerOptions Create()
        {
            var o = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            o.Converters.Add(new JsonStringEnumConverter());
            return o;
        }
    }
}
```

`src/TechSupportReply.Core/Settings/AppSettings.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace TechSupportReply.Core.Settings
{
    public enum LlmProviderKind
    {
        Anthropic,
        OpenAI,
    }

    public sealed class LlmProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string DisplayName { get; set; } = "";
        public LlmProviderKind Provider { get; set; }
        public string Model { get; set; } = "";
        /// <summary>OpenAI 호환 엔드포인트(선택). 비어 있으면 공급자 기본값.</summary>
        public string BaseUrl { get; set; } = "";
        public int MaxTokens { get; set; } = 16000;
        /// <summary>low | medium | high | max (Anthropic만 사용).</summary>
        public string Effort { get; set; } = "medium";
        public string SecretId { get; set; } = "";
    }

    public sealed class UserProfile
    {
        public string Name { get; set; } = "";
        public string Title { get; set; } = "";
        public string Company { get; set; } = "KOSTECH";
        public string Tone { get; set; } = "정중하고 간결한 기술지원 어조";
    }

    public sealed class AppSettings
    {
        public int SchemaVersion { get; set; } = 1;
        public UserProfile User { get; set; } = new UserProfile();
        public string RagRoot { get; set; } = "";
        public List<LlmProfile> Profiles { get; set; } = new List<LlmProfile>();
        public string DefaultProfileId { get; set; } = "";
        public string ClassifierProfileId { get; set; } = "";
        public int ReferenceTopK { get; set; } = 8;
        public int StyleExampleTopK { get; set; } = 3;
        public int MaxMailChars { get; set; } = 30000;

        public LlmProfile FindProfile(string id) => Profiles.FirstOrDefault(p => p.Id == id);
    }
}
```

`src/TechSupportReply.Core/Settings/SettingsStore.cs`:
```csharp
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using TechSupportReply.Core.IO;
using TechSupportReply.Core.Serialization;

namespace TechSupportReply.Core.Settings
{
    public sealed class SettingsStore
    {
        public const string FileName = "settings.json";

        public SettingsStore(string directory)
        {
            FilePath = Path.Combine(directory, FileName);
        }

        public static string DefaultDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TechSupportReply");

        public string FilePath { get; }

        public AppSettings Load()
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            try
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath, Encoding.UTF8), JsonDefaults.Options)
                       ?? new AppSettings();
            }
            catch (JsonException)
            {
                File.Copy(FilePath, FilePath + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
                return new AppSettings();
            }
        }

        public void Save(AppSettings settings) =>
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonDefaults.Options));
    }
}
```

`src/TechSupportReply.Core/Settings/SecretStore.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TechSupportReply.Core.IO;

namespace TechSupportReply.Core.Settings
{
    /// <summary>API 키를 현재 Windows 사용자 DPAPI로 암호화해 저장한다.</summary>
    public sealed class SecretStore
    {
        public const string FileName = "secrets.dat";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TechSupportReply.secrets.v1");
        private readonly string _path;
        private readonly object _lock = new object();

        public SecretStore(string directory)
        {
            _path = Path.Combine(directory, FileName);
        }

        public static string NewId() => Guid.NewGuid().ToString("N");

        public string Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (_lock)
            {
                return ReadAll(throwOnCorrupt: true).TryGetValue(id, out var value) ? value : null;
            }
        }

        public void Set(string id, string value)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("비밀 id가 비어 있습니다.", nameof(id));
            lock (_lock)
            {
                var all = ReadAll(throwOnCorrupt: false);
                all[id] = value;
                WriteAll(all);
            }
        }

        public void Remove(string id)
        {
            lock (_lock)
            {
                var all = ReadAll(throwOnCorrupt: false);
                if (all.Remove(id)) WriteAll(all);
            }
        }

        private Dictionary<string, string> ReadAll(bool throwOnCorrupt)
        {
            if (!File.Exists(_path)) return new Dictionary<string, string>();
            try
            {
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(plain))
                       ?? new Dictionary<string, string>();
            }
            catch (Exception ex) when (ex is CryptographicException || ex is JsonException)
            {
                if (!throwOnCorrupt) return new Dictionary<string, string>();
                throw new InvalidOperationException(
                    "저장된 API 키를 복호화할 수 없습니다. 다른 Windows 사용자 계정에서 만든 파일이거나 손상되었습니다. 설정에서 키를 다시 등록하세요.", ex);
            }
        }

        private void WriteAll(Dictionary<string, string> all)
        {
            var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(all));
            AtomicFile.WriteAllBytes(_path, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
        }
    }
}
```

- [ ] **Step 3: 테스트 실행**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~Core.Settings|FullyQualifiedName~Core.IO"`
Expected: PASS 10.

- [ ] **Step 4: 커밋**

```bash
git add src/TechSupportReply.Core tests/TechSupportReply.Tests
git commit -m "feat(core): 설정 저장소와 DPAPI API 키 저장소"
```

---

### Task 6: 제품 카탈로그와 키워드 분류기

**Files:**
- Create: `src/TechSupportReply.Core/Products/ProductDefinition.cs`, `ProductCatalog.cs`, `ClassificationResult.cs`, `KeywordClassifier.cs`
- Test: `tests/TechSupportReply.Tests/Core/Products/ProductCatalogTests.cs`, `KeywordClassifierTests.cs`, `tests/TechSupportReply.Tests/TestSupport/Mails.cs`

**Interfaces:**
- Consumes: `MailSnapshot`(Task 4), `AtomicFile`, `JsonDefaults`(Task 5).
- Produces: `ProductDefinition { string Id; string DisplayName; string Folder; List<string> Keywords }`.
- Produces: `ProductCatalog(IEnumerable<ProductDefinition>)`, 상수 `CommonId = "_common"`, `PromptFileName = "_prompt.md"`, `IReadOnlyList<ProductDefinition> Products`, `ProductDefinition Find(string id)`(대소문자 무시, 없으면 null), `ProductDefinition Common`, `static ProductCatalog Load(string productsJsonPath)`(파일 없으면 기본값, 형식 오류면 `InvalidDataException`), `static ProductCatalog CreateDefault()`, `void Save(string path)`.
- Produces: `enum ClassificationSource { Keyword, Llm, Default }`, `ClassificationResult { string ProductId; double Confidence; string Reason; ClassificationSource Source }`.
- Produces: `KeywordScore { string ProductId; double Score; List<string> MatchedKeywords }`, `KeywordClassifier(ProductCatalog)`, `IReadOnlyList<KeywordScore> Score(MailSnapshot)`, `ClassificationResult Classify(MailSnapshot)`.
- Produces(테스트): `Mails.Create(string subject, string body, params string[] attachments)`.

- [ ] **Step 1: 실패 테스트 작성**

`tests/TechSupportReply.Tests/TestSupport/Mails.cs`:
```csharp
using System;
using System.Linq;
using TechSupportReply.Core.Models;

namespace TechSupportReply.Tests.TestSupport
{
    internal static class Mails
    {
        public static MailSnapshot Create(string subject, string body, params string[] attachments) =>
            new MailSnapshot
            {
                Subject = subject,
                Body = body,
                SenderName = "김고객",
                SenderEmail = "customer@example.com",
                ReceivedAt = new DateTime(2026, 9, 1, 10, 0, 0),
                AttachmentNames = attachments.ToList(),
            };
    }
}
```

`tests/TechSupportReply.Tests/Core/Products/ProductCatalogTests.cs`:
```csharp
using System.IO;
using System.Linq;
using TechSupportReply.Core.Products;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Core.Products
{
    public class ProductCatalogTests
    {
        [Fact]
        public void Load_Missing_ReturnsDefault()
        {
            using (var tmp = new TempDir())
            {
                var catalog = ProductCatalog.Load(Path.Combine(tmp.Root, "products.json"));
                Assert.NotNull(catalog.Find("ls-dyna"));
                Assert.NotNull(catalog.Find("ansys-fluent"));
                Assert.Equal("LS-DYNA", catalog.Find("LS-DYNA").DisplayName);
                Assert.NotNull(catalog.Common);
            }
        }

        [Fact]
        public void Load_CustomFile_AddsCommonAutomatically_AndDefaultsFolder()
        {
            using (var tmp = new TempDir())
            {
                var path = tmp.File("products.json", "{\"products\":[{\"id\":\"x\",\"displayName\":\"X 제품\",\"keywords\":[\"xx\"]}]}");
                var catalog = ProductCatalog.Load(path);
                Assert.Equal(new[] { "x", ProductCatalog.CommonId }, catalog.Products.Select(p => p.Id).ToArray());
                Assert.Equal("x", catalog.Find("x").Folder);
            }
        }

        [Fact]
        public void Load_DuplicateIds_Throws()
        {
            using (var tmp = new TempDir())
            {
                var path = tmp.File("products.json", "{\"products\":[{\"id\":\"a\"},{\"id\":\"A\"}]}");
                Assert.Throws<InvalidDataException>(() => ProductCatalog.Load(path));
            }
        }

        [Fact]
        public void Load_InvalidJson_ThrowsWithFileName()
        {
            using (var tmp = new TempDir())
            {
                var path = tmp.File("products.json", "{ broken");
                var ex = Assert.Throws<InvalidDataException>(() => ProductCatalog.Load(path));
                Assert.Contains("products.json", ex.Message);
            }
        }

        [Fact]
        public void SaveThenLoad_RoundTrips()
        {
            using (var tmp = new TempDir())
            {
                var path = Path.Combine(tmp.Root, "products.json");
                ProductCatalog.CreateDefault().Save(path);
                var loaded = ProductCatalog.Load(path);
                Assert.Equal(ProductCatalog.CreateDefault().Products.Count, loaded.Products.Count);
                Assert.Contains("d3hsp", loaded.Find("ls-dyna").Keywords);
            }
        }
    }
}
```

`tests/TechSupportReply.Tests/Core/Products/KeywordClassifierTests.cs`:
```csharp
using TechSupportReply.Core.Products;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Core.Products
{
    public class KeywordClassifierTests
    {
        private readonly KeywordClassifier _classifier = new KeywordClassifier(ProductCatalog.CreateDefault());

        [Fact]
        public void LsDynaMail_ClassifiedAsLsDyna_WithHighConfidence()
        {
            var r = _classifier.Classify(Mails.Create("LS-DYNA 접촉 관통 문의",
                "*CONTACT_AUTOMATIC_SURFACE_TO_SURFACE 사용 중 d3hsp에 경고가 있습니다."));
            Assert.Equal("ls-dyna", r.ProductId);
            Assert.Equal(ClassificationSource.Keyword, r.Source);
            Assert.True(r.Confidence > 0.5, $"confidence={r.Confidence}");
            Assert.Contains("ls-dyna", r.Reason);
        }

        [Fact]
        public void FluentMail_ClassifiedAsFluent()
        {
            Assert.Equal("ansys-fluent", _classifier.Classify(Mails.Create("Fluent 계산 수렴 문제", "residual이 줄지 않습니다")).ProductId);
        }

        [Fact]
        public void LicenseMail_ClassifiedAsCommon()
        {
            Assert.Equal(ProductCatalog.CommonId, _classifier.Classify(Mails.Create("ansyslmd 라이선스 서버 연결 실패", "")).ProductId);
        }

        [Fact]
        public void NoKeywords_DefaultsToCommon()
        {
            var r = _classifier.Classify(Mails.Create("문의드립니다", "The solver works fluently."));
            Assert.Equal(ProductCatalog.CommonId, r.ProductId);
            Assert.Equal(ClassificationSource.Default, r.Source);
            Assert.Equal(0, r.Confidence);
        }

        [Fact]
        public void KeywordBoundaries_AreRespected()
        {
            Assert.Equal("ls-dyna", _classifier.Classify(Mails.Create("R13 문의", "*MAT_ELASTIC 카드 질문")).ProductId);
            Assert.Equal("ls-dyna", _classifier.Classify(Mails.Create("LS-DYNA R13", "")).ProductId);
        }

        [Fact]
        public void Subject_OutweighsBody()
        {
            Assert.Equal("ansys-electronics", _classifier.Classify(Mails.Create("HFSS 포트 설정", "fluent 사용자는 아닙니다")).ProductId);
        }

        [Fact]
        public void AttachmentNames_AreScored()
        {
            Assert.Equal("ls-dyna", _classifier.Classify(Mails.Create("해석 오류", "첨부 확인 부탁드립니다", "d3hsp", "messag")).ProductId);
        }

        [Fact]
        public void TwoProductsTie_LowersConfidence()
        {
            Assert.True(_classifier.Classify(Mails.Create("Fluent와 CFX 비교", "")).Confidence < 0.5);
        }
    }
}
```

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~Core.Products"`
Expected: 빌드 실패(형식 없음).

- [ ] **Step 2: 구현**

`src/TechSupportReply.Core/Products/ProductDefinition.cs`:
```csharp
using System.Collections.Generic;

namespace TechSupportReply.Core.Products
{
    public sealed class ProductDefinition
    {
        public string Id { get; set; } = "";
        public string DisplayName { get; set; } = "";
        /// <summary>RAG 루트 아래의 제품 폴더 이름. 비어 있으면 Id를 사용한다.</summary>
        public string Folder { get; set; } = "";
        public List<string> Keywords { get; set; } = new List<string>();
    }

    internal sealed class ProductCatalogFile
    {
        public List<ProductDefinition> Products { get; set; } = new List<ProductDefinition>();
    }
}
```

`src/TechSupportReply.Core/Products/ClassificationResult.cs`:
```csharp
namespace TechSupportReply.Core.Products
{
    public enum ClassificationSource
    {
        Keyword,
        Llm,
        Default,
    }

    public sealed class ClassificationResult
    {
        public string ProductId { get; set; } = "";
        /// <summary>0~1.</summary>
        public double Confidence { get; set; }
        public string Reason { get; set; } = "";
        public ClassificationSource Source { get; set; }
    }
}
```

`src/TechSupportReply.Core/Products/ProductCatalog.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using TechSupportReply.Core.IO;
using TechSupportReply.Core.Serialization;

namespace TechSupportReply.Core.Products
{
    public sealed class ProductCatalog
    {
        public const string CommonId = "_common";
        public const string PromptFileName = "_prompt.md";

        private readonly List<ProductDefinition> _products;

        public ProductCatalog(IEnumerable<ProductDefinition> products)
        {
            _products = new List<ProductDefinition>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in products)
            {
                if (string.IsNullOrWhiteSpace(p.Id)) throw new InvalidDataException("제품 id가 비어 있습니다.");
                if (!seen.Add(p.Id)) throw new InvalidDataException($"제품 id가 중복되었습니다: {p.Id}");
                if (string.IsNullOrWhiteSpace(p.Folder)) p.Folder = p.Id;
                if (string.IsNullOrWhiteSpace(p.DisplayName)) p.DisplayName = p.Id;
                if (p.Keywords == null) p.Keywords = new List<string>();
                _products.Add(p);
            }
            if (!seen.Contains(CommonId)) _products.Add(CommonDefinition());
        }

        public IReadOnlyList<ProductDefinition> Products => _products;

        public ProductDefinition Common => Find(CommonId);

        public ProductDefinition Find(string id) =>
            string.IsNullOrEmpty(id) ? null : _products.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

        public static ProductCatalog Load(string productsJsonPath)
        {
            if (!File.Exists(productsJsonPath)) return CreateDefault();
            ProductCatalogFile file;
            try
            {
                file = JsonSerializer.Deserialize<ProductCatalogFile>(File.ReadAllText(productsJsonPath, Encoding.UTF8), JsonDefaults.Options);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"products.json 형식 오류: {ex.Message}", ex);
            }
            if (file?.Products == null || file.Products.Count == 0) return CreateDefault();
            return new ProductCatalog(file.Products);
        }

        public void Save(string path) =>
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(new ProductCatalogFile { Products = _products }, JsonDefaults.Options));

        public static ProductCatalog CreateDefault() => new ProductCatalog(new[]
        {
            Def("ls-dyna", "LS-DYNA", "LS-DYNA",
                "ls-dyna", "lsdyna", "ls dyna", "*keyword", "*contact", "*mat_", "*section", "*control_", "*database_",
                "*boundary_", "d3plot", "d3hsp", "messag", "binout", "mpp", "smp"),
            Def("ls-prepost", "LS-PrePost / LS-OPT", "LS-PrePost",
                "ls-prepost", "lsprepost", "lspp", "ls-opt", "lsopt"),
            Def("ansys-mechanical", "Ansys Mechanical", "Ansys-Mechanical",
                "ansys mechanical", "mechanical", "mapdl", "apdl", "workbench", "static structural",
                "transient structural", "modal", "harmonic response", "solve.out", "ds.dat"),
            Def("ansys-fluent", "Ansys Fluent", "Ansys-Fluent",
                "fluent", "udf", "fluent meshing", "residual", "under-relaxation", ".cas", ".dat.h5", "수렴", "발산"),
            Def("ansys-cfx", "Ansys CFX", "Ansys-CFX",
                "cfx", "cfx-pre", "cfd-post", "cfx-solver", ".def", ".res"),
            Def("ansys-electronics", "Ansys Electronics (HFSS/Maxwell)", "Ansys-Electronics",
                "hfss", "maxwell", "aedt", "electronics desktop", "q3d", "siwave", "icepak"),
            Def("ansys-spaceclaim", "Ansys SpaceClaim / Discovery", "Ansys-SpaceClaim",
                "spaceclaim", "discovery", ".scdoc", "geometry repair"),
            CommonDefinition(),
        });

        private static ProductDefinition CommonDefinition() =>
            Def(CommonId, "공통 (라이선스/설치)", "_common",
                "license", "licensing", "라이선스", "라이센스", "ansyslmd", "lmutil", "lmgrd", "flexnet", "flexlm",
                "license manager", "설치", "install");

        private static ProductDefinition Def(string id, string name, string folder, params string[] keywords) =>
            new ProductDefinition { Id = id, DisplayName = name, Folder = folder, Keywords = keywords.ToList() };
    }
}
```

`src/TechSupportReply.Core/Products/KeywordClassifier.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TechSupportReply.Core.Models;

namespace TechSupportReply.Core.Products
{
    public sealed class KeywordScore
    {
        public string ProductId { get; set; } = "";
        public double Score { get; set; }
        public List<string> MatchedKeywords { get; set; } = new List<string>();
    }

    /// <summary>제목(×3)·첨부파일명(×2)·본문(×1)에서 제품 키워드 출현 횟수(필드당 최대 5)를 점수화한다.</summary>
    public sealed class KeywordClassifier
    {
        private const double SubjectWeight = 3;
        private const double AttachmentWeight = 2;
        private const double BodyWeight = 1;
        private const int MaxHitsPerField = 5;

        private readonly ProductCatalog _catalog;
        private readonly Dictionary<string, Regex> _patterns = new Dictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);

        public KeywordClassifier(ProductCatalog catalog)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        public IReadOnlyList<KeywordScore> Score(MailSnapshot mail)
        {
            var subject = mail.Subject ?? "";
            var body = (mail.Body ?? "") + "\n" + (mail.AttachmentText ?? "");
            var attachments = string.Join("\n", mail.AttachmentNames ?? new List<string>());
            var scores = new List<KeywordScore>();
            foreach (var product in _catalog.Products)
            {
                var score = new KeywordScore { ProductId = product.Id };
                foreach (var keyword in product.Keywords.Where(k => !string.IsNullOrWhiteSpace(k)))
                {
                    var re = PatternFor(keyword);
                    double hits = Hits(re, subject) * SubjectWeight + Hits(re, attachments) * AttachmentWeight + Hits(re, body) * BodyWeight;
                    if (hits <= 0) continue;
                    score.Score += hits;
                    score.MatchedKeywords.Add(keyword);
                }
                if (score.Score > 0) scores.Add(score);
            }
            return scores
                .OrderByDescending(s => s.Score)
                .ThenBy(s => s.ProductId == ProductCatalog.CommonId ? 1 : 0)
                .ToList();
        }

        public ClassificationResult Classify(MailSnapshot mail)
        {
            var scores = Score(mail);
            if (scores.Count == 0)
            {
                return new ClassificationResult
                {
                    ProductId = ProductCatalog.CommonId,
                    Confidence = 0,
                    Source = ClassificationSource.Default,
                    Reason = "일치하는 제품 키워드가 없어 공통으로 분류했습니다.",
                };
            }
            var top = scores[0];
            var second = scores.Count > 1 ? scores[1].Score : 0;
            return new ClassificationResult
            {
                ProductId = top.ProductId,
                Confidence = Math.Round(top.Score / (top.Score + second + 1.0), 2),
                Source = ClassificationSource.Keyword,
                Reason = "키워드: " + string.Join(", ", top.MatchedKeywords.Take(5)),
            };
        }

        private Regex PatternFor(string keyword)
        {
            if (_patterns.TryGetValue(keyword, out var cached)) return cached;
            var k = keyword.Trim().ToLowerInvariant();
            var prefix = IsAsciiAlnum(k[0]) ? "(?<![a-z0-9])" : "";
            var suffix = IsAsciiAlnum(k[k.Length - 1]) ? "(?![a-z0-9])" : "";
            var re = new Regex(prefix + Regex.Escape(k) + suffix, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            _patterns[keyword] = re;
            return re;
        }

        private static bool IsAsciiAlnum(char c) => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');

        private static int Hits(Regex re, string text) => Math.Min(MaxHitsPerField, re.Matches(text).Count);
    }
}
```

주의: `KeywordBoundaries_AreRespected`의 `*MAT_ELASTIC`은 `*mat_`(끝이 `_`라 뒤 경계 없음)로, `LS-DYNA R13`은 `ls-dyna`로 매칭된다. `LsDynaMail` 테스트의 Reason 검사는 `ls-dyna` 키워드 문자열이 "키워드: " 목록에 포함되는지 확인한다.

- [ ] **Step 3: 테스트 실행**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~Core.Products"`
Expected: PASS 13.

- [ ] **Step 4: 커밋**

```bash
git add src/TechSupportReply.Core tests/TechSupportReply.Tests
git commit -m "feat(core): 제품 카탈로그와 키워드 분류기"
```

---

### Task 7: LLM 추상화와 LLM+키워드 제품 분류기

**Files:**
- Create: `src/TechSupportReply.Core/Llm/LlmModels.cs`, `ILlmProvider.cs`, `LlmException.cs`
- Create: `src/TechSupportReply.Core/Products/ProductClassifier.cs`
- Create: `tests/TechSupportReply.Tests/TestSupport/FakeLlmProvider.cs`
- Test: `tests/TechSupportReply.Tests/Core/Products/ProductClassifierTests.cs`, `tests/TechSupportReply.Tests/Core/Llm/LlmExceptionTests.cs`

**Interfaces:**
- Consumes: `ProductCatalog`, `KeywordClassifier`, `ClassificationResult`(Task 6), `EmailTextCleaner`(Task 4).
- Produces: `enum LlmRole { User, Assistant }`; `LlmMessage { LlmRole Role; string Content; static User(string); static Assistant(string) }`; `LlmRequest { string CachedSystem; string System; List<LlmMessage> Messages (get-only); int MaxTokens=16000; string Effort; string JsonSchema; string SchemaName="result" }`.
- Produces: `interface ILlmProvider { string DisplayName { get; } Task<string> CompleteAsync(LlmRequest request, CancellationToken ct); Task<string> StreamAsync(LlmRequest request, Action<string> onDelta, CancellationToken ct); }` — `StreamAsync`는 전체 텍스트를 반환한다.
- Produces: `enum LlmErrorKind { Authentication, PermissionDenied, NotFound, RateLimited, Server, Network, Refusal, InvalidRequest, Unknown }`; `LlmException(LlmErrorKind kind, string detail, Exception inner = null)`, `Kind`, `UserMessage`.
- Produces: `ProductClassifier(ProductCatalog catalog, TimeSpan? llmTimeout = null)`, `ClassificationResult ClassifyByKeywords(MailSnapshot)`, `Task<ClassificationResult> ClassifyAsync(MailSnapshot mail, ILlmProvider llm, CancellationToken ct)`; internal `LlmRequest BuildRequest(MailSnapshot, IReadOnlyList<KeywordScore>)`, `ClassificationResult Parse(string json)`.
- Produces(테스트): `FakeLlmProvider { List<LlmRequest> Requests; FakeLlmProvider Enqueue(string); Exception ThrowOnCall; TimeSpan Delay }`.

- [ ] **Step 1: 테스트 대역과 실패 테스트 작성**

`tests/TechSupportReply.Tests/TestSupport/FakeLlmProvider.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.Core.Llm;

namespace TechSupportReply.Tests.TestSupport
{
    internal sealed class FakeLlmProvider : ILlmProvider
    {
        private readonly Queue<string> _responses = new Queue<string>();

        public string DisplayName => "Fake";
        public List<LlmRequest> Requests { get; } = new List<LlmRequest>();
        public Exception ThrowOnCall { get; set; }
        public TimeSpan Delay { get; set; }

        public FakeLlmProvider Enqueue(string response)
        {
            _responses.Enqueue(response);
            return this;
        }

        public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            if (ThrowOnCall != null) throw ThrowOnCall;
            return _responses.Count > 0 ? _responses.Dequeue() : "";
        }

        public async Task<string> StreamAsync(LlmRequest request, Action<string> onDelta, CancellationToken ct)
        {
            var text = await CompleteAsync(request, ct);
            for (int i = 0; i < text.Length; i += 5)
            {
                ct.ThrowIfCancellationRequested();
                onDelta?.Invoke(text.Substring(i, Math.Min(5, text.Length - i)));
            }
            return text;
        }
    }
}
```

`tests/TechSupportReply.Tests/Core/Llm/LlmExceptionTests.cs`:
```csharp
using TechSupportReply.Core.Llm;
using Xunit;

namespace TechSupportReply.Tests.Core.Llm
{
    public class LlmExceptionTests
    {
        [Theory]
        [InlineData(LlmErrorKind.Authentication, "API 키")]
        [InlineData(LlmErrorKind.RateLimited, "한도")]
        [InlineData(LlmErrorKind.Network, "네트워크")]
        [InlineData(LlmErrorKind.Refusal, "거절")]
        [InlineData(LlmErrorKind.NotFound, "모델")]
        public void UserMessage_IsKorean(LlmErrorKind kind, string expected)
        {
            Assert.Contains(expected, new LlmException(kind, "detail").UserMessage);
        }

        [Fact]
        public void UserMessage_InvalidRequest_IncludesDetail()
        {
            Assert.Contains("bad field", new LlmException(LlmErrorKind.InvalidRequest, "bad field").UserMessage);
        }
    }
}
```

`tests/TechSupportReply.Tests/Core/Products/ProductClassifierTests.cs`:
```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Products;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Core.Products
{
    public class ProductClassifierTests
    {
        private static readonly ProductCatalog Catalog = ProductCatalog.CreateDefault();
        private static readonly Core.Models.MailSnapshot DynaMail =
            Mails.Create("LS-DYNA 접촉 문의", "*CONTACT 카드 사용 중 d3hsp 경고");

        [Fact]
        public async Task NoLlm_ReturnsKeywordResult()
        {
            var r = await new ProductClassifier(Catalog).ClassifyAsync(DynaMail, null, CancellationToken.None);
            Assert.Equal("ls-dyna", r.ProductId);
            Assert.Equal(ClassificationSource.Keyword, r.Source);
        }

        [Fact]
        public async Task ValidLlmJson_ReturnsLlmResult()
        {
            var llm = new FakeLlmProvider().Enqueue("{\"productId\":\"ansys-fluent\",\"confidence\":0.9,\"reason\":\"Fluent 수렴 문의\"}");
            var r = await new ProductClassifier(Catalog).ClassifyAsync(DynaMail, llm, CancellationToken.None);
            Assert.Equal("ansys-fluent", r.ProductId);
            Assert.Equal(ClassificationSource.Llm, r.Source);
            Assert.Equal(0.9, r.Confidence);
            Assert.Equal("Fluent 수렴 문의", r.Reason);
        }

        [Fact]
        public async Task PercentConfidence_IsNormalized()
        {
            var llm = new FakeLlmProvider().Enqueue("{\"productId\":\"ls-dyna\",\"confidence\":85,\"reason\":\"x\"}");
            Assert.Equal(0.85, (await new ProductClassifier(Catalog).ClassifyAsync(DynaMail, llm, CancellationToken.None)).Confidence);
        }

        [Theory]
        [InlineData("{\"productId\":\"unknown-product\",\"confidence\":0.9,\"reason\":\"x\"}")]
        [InlineData("not json at all")]
        [InlineData("")]
        public async Task UnusableLlmOutput_FallsBackToKeywords(string output)
        {
            var llm = new FakeLlmProvider().Enqueue(output);
            var r = await new ProductClassifier(Catalog).ClassifyAsync(DynaMail, llm, CancellationToken.None);
            Assert.Equal("ls-dyna", r.ProductId);
            Assert.Equal(ClassificationSource.Keyword, r.Source);
            Assert.Contains("해석할 수 없어", r.Reason);
        }

        [Fact]
        public async Task LlmError_FallsBackWithUserMessage()
        {
            var llm = new FakeLlmProvider { ThrowOnCall = new LlmException(LlmErrorKind.RateLimited, "429") };
            var r = await new ProductClassifier(Catalog).ClassifyAsync(DynaMail, llm, CancellationToken.None);
            Assert.Equal(ClassificationSource.Keyword, r.Source);
            Assert.Contains("한도", r.Reason);
        }

        [Fact]
        public async Task LlmTimeout_FallsBack()
        {
            var llm = new FakeLlmProvider { Delay = TimeSpan.FromSeconds(5) }.Enqueue("{}");
            var r = await new ProductClassifier(Catalog, TimeSpan.FromMilliseconds(100)).ClassifyAsync(DynaMail, llm, CancellationToken.None);
            Assert.Equal(ClassificationSource.Keyword, r.Source);
            Assert.Contains("시간 초과", r.Reason);
        }

        [Fact]
        public async Task UserCancellation_Propagates()
        {
            var llm = new FakeLlmProvider { Delay = TimeSpan.FromSeconds(5) };
            using (var cts = new CancellationTokenSource(50))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    new ProductClassifier(Catalog).ClassifyAsync(DynaMail, llm, cts.Token));
            }
        }

        [Fact]
        public async Task Request_HasSchemaWithAllProductIds_AndKeywordHints()
        {
            var llm = new FakeLlmProvider().Enqueue("{\"productId\":\"ls-dyna\",\"confidence\":1,\"reason\":\"x\"}");
            await new ProductClassifier(Catalog).ClassifyAsync(DynaMail, llm, CancellationToken.None);
            var req = llm.Requests[0];
            foreach (var p in Catalog.Products) Assert.Contains("\"" + p.Id + "\"", req.JsonSchema);
            Assert.Contains("ls-dyna(", req.Messages[0].Content);
            Assert.Equal("low", req.Effort);
            Assert.False(string.IsNullOrWhiteSpace(req.CachedSystem));
        }
    }
}
```

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~ProductClassifierTests|FullyQualifiedName~LlmExceptionTests"`
Expected: 빌드 실패(형식 없음).

- [ ] **Step 2: 구현**

`src/TechSupportReply.Core/Llm/LlmModels.cs`:
```csharp
using System.Collections.Generic;

namespace TechSupportReply.Core.Llm
{
    public enum LlmRole
    {
        User,
        Assistant,
    }

    public sealed class LlmMessage
    {
        public LlmRole Role { get; set; }
        public string Content { get; set; } = "";

        public static LlmMessage User(string content) => new LlmMessage { Role = LlmRole.User, Content = content };
        public static LlmMessage Assistant(string content) => new LlmMessage { Role = LlmRole.Assistant, Content = content };
    }

    public sealed class LlmRequest
    {
        /// <summary>요청마다 바뀌지 않는 시스템 프롬프트 앞부분(프롬프트 캐싱 대상). 비어 있으면 안 된다.</summary>
        public string CachedSystem { get; set; } = "";
        /// <summary>요청마다 바뀔 수 있는 시스템 프롬프트 뒷부분.</summary>
        public string System { get; set; } = "";
        public List<LlmMessage> Messages { get; } = new List<LlmMessage>();
        public int MaxTokens { get; set; } = 16000;
        /// <summary>low | medium | high | max. null이면 프로필 값을 사용한다.</summary>
        public string Effort { get; set; }
        /// <summary>설정 시 해당 JSON 스키마를 따르는 JSON만 출력(구조화 출력).</summary>
        public string JsonSchema { get; set; }
        public string SchemaName { get; set; } = "result";
    }
}
```

`src/TechSupportReply.Core/Llm/ILlmProvider.cs`:
```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TechSupportReply.Core.Llm
{
    public interface ILlmProvider
    {
        string DisplayName { get; }

        Task<string> CompleteAsync(LlmRequest request, CancellationToken ct);

        /// <summary>텍스트 조각이 올 때마다 onDelta를 호출하고, 완료되면 전체 텍스트를 반환한다.</summary>
        Task<string> StreamAsync(LlmRequest request, Action<string> onDelta, CancellationToken ct);
    }
}
```

`src/TechSupportReply.Core/Llm/LlmException.cs`:
```csharp
using System;

namespace TechSupportReply.Core.Llm
{
    public enum LlmErrorKind
    {
        Authentication,
        PermissionDenied,
        NotFound,
        RateLimited,
        Server,
        Network,
        Refusal,
        InvalidRequest,
        Unknown,
    }

    public sealed class LlmException : Exception
    {
        public LlmException(LlmErrorKind kind, string detail, Exception inner = null)
            : base(detail, inner)
        {
            Kind = kind;
        }

        public LlmErrorKind Kind { get; }

        public string UserMessage => Kind switch
        {
            LlmErrorKind.Authentication => "API 키가 올바르지 않거나 등록되지 않았습니다. 설정에서 키를 확인하세요.",
            LlmErrorKind.PermissionDenied => "이 API 키로는 해당 모델을 사용할 권한이 없습니다.",
            LlmErrorKind.NotFound => "모델 이름을 찾을 수 없습니다. 프로필의 모델명을 확인하세요.",
            LlmErrorKind.RateLimited => "요청 한도를 초과했습니다. 잠시 후 다시 시도하세요.",
            LlmErrorKind.Server => "LLM 서비스에 일시적인 오류가 발생했습니다. 잠시 후 다시 시도하세요.",
            LlmErrorKind.Network => "LLM 서비스에 연결할 수 없습니다. 네트워크 연결을 확인하세요.",
            LlmErrorKind.Refusal => "모델이 이 요청에 대한 응답을 거절했습니다. 내용을 수정하거나 다른 프로필로 시도하세요.",
            LlmErrorKind.InvalidRequest => "요청 형식 오류: " + Message,
            _ => "알 수 없는 오류: " + Message,
        };
    }
}
```

`src/TechSupportReply.Core/Products/ProductClassifier.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Models;
using TechSupportReply.Core.Text;

namespace TechSupportReply.Core.Products
{
    /// <summary>LLM 구조화 출력으로 제품군을 분류하고, 실패·시간 초과 시 키워드 결과로 대체한다.</summary>
    public sealed class ProductClassifier
    {
        private const int MaxBodyChars = 4000;
        private readonly ProductCatalog _catalog;
        private readonly KeywordClassifier _keyword;
        private readonly TimeSpan _llmTimeout;

        public ProductClassifier(ProductCatalog catalog, TimeSpan? llmTimeout = null)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _keyword = new KeywordClassifier(catalog);
            _llmTimeout = llmTimeout ?? TimeSpan.FromSeconds(30);
        }

        public ClassificationResult ClassifyByKeywords(MailSnapshot mail) => _keyword.Classify(mail);

        public async Task<ClassificationResult> ClassifyAsync(MailSnapshot mail, ILlmProvider llm, CancellationToken ct)
        {
            var keywordResult = _keyword.Classify(mail);
            if (llm == null) return keywordResult;

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(_llmTimeout);
                try
                {
                    var json = await llm.CompleteAsync(BuildRequest(mail, _keyword.Score(mail)), timeout.Token).ConfigureAwait(false);
                    return Parse(json) ?? WithNote(keywordResult, "LLM 분류 결과를 해석할 수 없어 키워드 결과를 사용합니다.");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return WithNote(keywordResult, "LLM 분류 시간 초과로 키워드 결과를 사용합니다.");
                }
                catch (LlmException ex)
                {
                    return WithNote(keywordResult, $"LLM 분류 실패({ex.UserMessage}) — 키워드 결과를 사용합니다.");
                }
            }
        }

        internal LlmRequest BuildRequest(MailSnapshot mail, IReadOnlyList<KeywordScore> hints)
        {
            var productLines = string.Join("\n", _catalog.Products.Select(p => $"- {p.Id}: {p.DisplayName}"));
            var hintText = hints.Count == 0
                ? "(없음)"
                : string.Join(", ", hints.Take(3).Select(h => $"{h.ProductId}({h.Score:0.#})"));
            var body = EmailTextCleaner.Split(mail.Body).Latest;
            if (body.Length > MaxBodyChars) body = body.Substring(0, MaxBodyChars) + "\n[...생략...]";

            var request = new LlmRequest
            {
                CachedSystem =
                    "당신은 CAE 소프트웨어(LS-DYNA, Ansys 등) 기술지원 메일을 제품군별로 분류합니다. " +
                    "메일이 어떤 제품에 대한 문의인지 아래 목록에서 하나만 고르세요. " +
                    "라이선스·설치·계정처럼 특정 제품의 해석 기능과 무관한 문의는 _common을 고르세요. " +
                    "confidence는 0~1 사이 확신도, reason은 한국어 한 문장입니다.\n\n제품 목록:\n" + productLines,
                MaxTokens = 2000,
                Effort = "low",
                JsonSchema = BuildSchema(),
                SchemaName = "product_classification",
            };
            request.Messages.Add(LlmMessage.User(
                $"키워드 힌트: {hintText}\n\n제목: {mail.Subject}\n첨부: {string.Join(", ", mail.AttachmentNames ?? new List<string>())}\n\n본문:\n{body}"));
            return request;
        }

        internal ClassificationResult Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            int start = json.IndexOf('{');
            int end = json.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            try
            {
                using (var doc = JsonDocument.Parse(json.Substring(start, end - start + 1)))
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("productId", out var idElement) || idElement.ValueKind != JsonValueKind.String) return null;
                    var product = _catalog.Find(idElement.GetString());
                    if (product == null) return null;
                    double confidence = root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0.5;
                    if (confidence > 1 && confidence <= 100) confidence /= 100;
                    confidence = Math.Max(0, Math.Min(1, confidence));
                    var reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : "";
                    return new ClassificationResult
                    {
                        ProductId = product.Id,
                        Confidence = Math.Round(confidence, 2),
                        Reason = reason,
                        Source = ClassificationSource.Llm,
                    };
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private string BuildSchema() => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["productId"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = _catalog.Products.Select(p => p.Id).ToArray() },
                ["confidence"] = new Dictionary<string, object> { ["type"] = "number" },
                ["reason"] = new Dictionary<string, object> { ["type"] = "string" },
            },
            ["required"] = new[] { "productId", "confidence", "reason" },
            ["additionalProperties"] = false,
        });

        private static ClassificationResult WithNote(ClassificationResult source, string note) => new ClassificationResult
        {
            ProductId = source.ProductId,
            Confidence = source.Confidence,
            Source = source.Source,
            Reason = source.Reason + " / " + note,
        };
    }
}
```

- [ ] **Step 3: 테스트 실행**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~ProductClassifierTests|FullyQualifiedName~LlmExceptionTests"`
Expected: PASS 16.

- [ ] **Step 4: 커밋**

```bash
git add src/TechSupportReply.Core tests/TechSupportReply.Tests
git commit -m "feat(core): LLM 공급자 추상화와 LLM+키워드 제품 분류기"
```

---

### Task 8: Anthropic Claude 공급자

**Files:**
- Create: `src/TechSupportReply.Core/Llm/AnthropicProvider.cs`
- Test: `tests/TechSupportReply.Tests/Core/Llm/AnthropicProviderTests.cs`, `tests/TechSupportReply.Tests/Core/Llm/LlmIntegrationTests.cs`

**Interfaces:**
- Consumes: `LlmProfile`(Task 5), `ILlmProvider`, `LlmRequest`, `LlmException`(Task 7).
- Produces: `AnthropicProvider(LlmProfile profile, string apiKey) : ILlmProvider`, `static void EnsureSupportedModel(string model)`(미지원 시 `ArgumentException`), internal `MessageCreateParams BuildParams(LlmRequest)`, internal `static Effort ToEffort(string)`.

**SDK 사용 근거(claude-api 스킬 csharp/):** `client.Messages.Create(params)`, `client.Messages.CreateStreaming(params)` + `RawMessageStreamEvent.TryPickContentBlockDelta` / `delta.Delta.TryPickText` / `TryPickDelta`, `ThinkingConfigAdaptive`, `OutputConfig { Effort, Format = new JsonOutputFormat { Schema = Dictionary<string, JsonElement> } }`, `System = List<TextBlockParam>` + `CacheControlEphemeral`, `response.StopReason == "refusal"`, 예외 `Anthropic.Exceptions.*`, refusal 폴백 `new AnthropicClient { Handlers = [new BetaRefusalFallbackHandler { Fallbacks = [new(Model.ClaudeOpus4_8)] }] }`(`Anthropic.Helpers`). 컴파일 오류로 이름·시그니처(예: `CancellationToken` 인수 위치)가 다르게 나오면 Global Constraints의 `strings` 방법으로 확인해 맞춘다. `BetaRefusalFallbackHandler`가 설치된 SDK에 없으면 `Handlers` 줄을 제거하고, 사용자 보고에 "refusal 폴백 미적용"을 적는다.

- [ ] **Step 1: 실패 테스트 작성**

`tests/TechSupportReply.Tests/Core/Llm/AnthropicProviderTests.cs`:
```csharp
using System;
using Anthropic.Models.Messages;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Settings;
using Xunit;

namespace TechSupportReply.Tests.Core.Llm
{
    public class AnthropicProviderTests
    {
        private static LlmProfile Profile(string model) =>
            new LlmProfile { DisplayName = "t", Provider = LlmProviderKind.Anthropic, Model = model };

        [Theory]
        [InlineData("claude-haiku-4-5")]
        [InlineData("claude-sonnet-4-5")]
        [InlineData("claude-3-5-sonnet-latest")]
        [InlineData("")]
        public void Constructor_RejectsUnsupportedModels(string model)
        {
            Assert.Throws<ArgumentException>(() => new AnthropicProvider(Profile(model), "sk-ant-test"));
        }

        [Theory]
        [InlineData("claude-opus-5")]
        [InlineData("claude-sonnet-5")]
        [InlineData("claude-opus-4-8")]
        public void Constructor_AcceptsSupportedModels(string model)
        {
            Assert.Equal("t", new AnthropicProvider(Profile(model), "sk-ant-test").DisplayName);
        }

        [Fact]
        public void Constructor_EmptyKey_ThrowsAuthentication()
        {
            var ex = Assert.Throws<LlmException>(() => new AnthropicProvider(Profile("claude-opus-5"), " "));
            Assert.Equal(LlmErrorKind.Authentication, ex.Kind);
        }

        [Fact]
        public void BuildParams_RequiresCachedSystem()
        {
            var provider = new AnthropicProvider(Profile("claude-opus-5"), "sk-ant-test");
            var request = new LlmRequest();
            request.Messages.Add(LlmMessage.User("hi"));
            Assert.Throws<ArgumentException>(() => provider.BuildParams(request));
        }

        [Fact]
        public void BuildParams_WithSchema_Builds()
        {
            var provider = new AnthropicProvider(Profile("claude-opus-5"), "sk-ant-test");
            var request = new LlmRequest { CachedSystem = "sys", System = "vol", JsonSchema = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}" };
            request.Messages.Add(LlmMessage.User("hi"));
            Assert.NotNull(provider.BuildParams(request));
        }

        [Theory]
        [InlineData("low")]
        [InlineData("HIGH")]
        [InlineData("max")]
        [InlineData(null)]
        [InlineData("weird")]
        public void ToEffort_MapsValues(string value)
        {
            var expected = value == null || value == "weird" ? Effort.Medium
                : value.ToLowerInvariant() == "low" ? Effort.Low
                : value.ToLowerInvariant() == "high" ? Effort.High
                : Effort.Max;
            Assert.Equal(expected, AnthropicProvider.ToEffort(value));
        }
    }
}
```

`tests/TechSupportReply.Tests/Core/Llm/LlmIntegrationTests.cs` (실제 API 호출 — 환경 변수가 없으면 건너뜀):
```csharp
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Products;
using TechSupportReply.Core.Settings;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Core.Llm
{
    [Trait("Category", "Integration")]
    public class LlmIntegrationTests
    {
        private static string Env(string name)
        {
            var v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        private static LlmRequest Hello()
        {
            var r = new LlmRequest { CachedSystem = "당신은 연결 테스트 도우미입니다.", MaxTokens = 2000, Effort = "low" };
            r.Messages.Add(LlmMessage.User("'연결 성공'이라고만 답하세요."));
            return r;
        }

        private static AnthropicProvider Anthropic(string key) => new AnthropicProvider(new LlmProfile
        {
            DisplayName = "it",
            Provider = LlmProviderKind.Anthropic,
            Model = Env("ANTHROPIC_TEST_MODEL") ?? "claude-opus-5",
        }, key);

        [SkippableFact]
        public async Task Anthropic_Stream_ReturnsText_AndDeltasMatch()
        {
            var key = Env("ANTHROPIC_API_KEY");
            Skip.If(key == null, "ANTHROPIC_API_KEY가 없어 건너뜀");
            var deltas = new StringBuilder();
            var text = await Anthropic(key).StreamAsync(Hello(), d => deltas.Append(d), CancellationToken.None);
            Assert.Contains("연결", text);
            Assert.Equal(text, deltas.ToString());
        }

        [SkippableFact]
        public async Task Anthropic_StructuredClassification_LsDyna()
        {
            var key = Env("ANTHROPIC_API_KEY");
            Skip.If(key == null, "ANTHROPIC_API_KEY가 없어 건너뜀");
            var mail = Mails.Create("해석 중 경고 문의", "*CONTACT_AUTOMATIC_SURFACE_TO_SURFACE에서 초기 관통 경고가 d3hsp에 많이 나옵니다.");
            var r = await new ProductClassifier(ProductCatalog.CreateDefault()).ClassifyAsync(mail, Anthropic(key), CancellationToken.None);
            Assert.Equal(ClassificationSource.Llm, r.Source);
            Assert.Equal("ls-dyna", r.ProductId);
        }

        [SkippableFact]
        public async Task Anthropic_InvalidKey_ThrowsAuthentication()
        {
            Skip.If(Env("ANTHROPIC_API_KEY") == null, "네트워크 테스트는 ANTHROPIC_API_KEY가 있을 때만 실행");
            var ex = await Assert.ThrowsAsync<LlmException>(() => Anthropic("sk-ant-invalid").CompleteAsync(Hello(), CancellationToken.None));
            Assert.Equal(LlmErrorKind.Authentication, ex.Kind);
        }
    }
}
```

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~AnthropicProviderTests"`
Expected: 빌드 실패(`AnthropicProvider` 없음, `Anthropic` 패키지 없음).

- [ ] **Step 2: 패키지 추가와 구현**

```bash
dotnet add src/TechSupportReply.Core package Anthropic
```

`src/TechSupportReply.Core/Llm/AnthropicProvider.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Helpers;
using Anthropic.Models.Messages;
using TechSupportReply.Core.Settings;

namespace TechSupportReply.Core.Llm
{
    /// <summary>
    /// 공식 Anthropic C# SDK 기반 공급자. 적응형 사고 + effort, 시스템 프롬프트 캐싱,
    /// 구조화 출력, 스트리밍, refusal 폴백(claude-opus-4-8)을 사용한다.
    /// </summary>
    public sealed class AnthropicProvider : ILlmProvider
    {
        private static readonly string[] UnsupportedModelPrefixes =
        {
            "claude-3", "claude-haiku-4-5", "claude-sonnet-4-5", "claude-opus-4-5", "claude-opus-4-1",
            "claude-opus-4-0", "claude-sonnet-4-0", "claude-opus-4-2025", "claude-sonnet-4-2025",
        };

        private readonly LlmProfile _profile;
        private readonly AnthropicClient _client;

        public AnthropicProvider(LlmProfile profile, string apiKey)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            EnsureSupportedModel(profile.Model);
            if (string.IsNullOrWhiteSpace(apiKey)) throw new LlmException(LlmErrorKind.Authentication, "API 키가 비어 있습니다.");
            _client = new AnthropicClient
            {
                ApiKey = apiKey,
                Handlers = [new BetaRefusalFallbackHandler { Fallbacks = [new(Model.ClaudeOpus4_8)] }],
            };
        }

        public string DisplayName => _profile.DisplayName;

        public static void EnsureSupportedModel(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("모델명이 비어 있습니다.");
            if (UnsupportedModelPrefixes.Any(p => model.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException(
                    $"'{model}'은(는) 지원하지 않습니다. 적응형 사고를 지원하는 Claude 4.6 이상 모델(예: claude-opus-5, claude-sonnet-5)을 사용하세요.");
        }

        public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            var parameters = BuildParams(request);
            try
            {
                var response = await _client.Messages.Create(parameters, ct).ConfigureAwait(false);
                if (response.StopReason == "refusal") throw new LlmException(LlmErrorKind.Refusal, "stop_reason=refusal");
                return string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
            }
            catch (Exception ex) when (Translate(ex) is LlmException mapped)
            {
                throw mapped;
            }
        }

        public async Task<string> StreamAsync(LlmRequest request, Action<string> onDelta, CancellationToken ct)
        {
            var parameters = BuildParams(request);
            var text = new StringBuilder();
            bool refused = false;
            try
            {
                await foreach (RawMessageStreamEvent ev in _client.Messages.CreateStreaming(parameters, ct).WithCancellation(ct).ConfigureAwait(false))
                {
                    if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var piece))
                    {
                        text.Append(piece.Text);
                        onDelta?.Invoke(piece.Text);
                    }
                    else if (ev.TryPickDelta(out var messageDelta) && messageDelta.Delta.StopReason == "refusal")
                    {
                        refused = true;
                    }
                }
            }
            catch (Exception ex) when (Translate(ex) is LlmException mapped)
            {
                throw mapped;
            }
            if (refused) throw new LlmException(LlmErrorKind.Refusal, "stop_reason=refusal");
            return text.ToString();
        }

        internal MessageCreateParams BuildParams(LlmRequest r)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            if (string.IsNullOrWhiteSpace(r.CachedSystem)) throw new ArgumentException("CachedSystem은 비어 있을 수 없습니다.");

            var system = new List<TextBlockParam>
            {
                new TextBlockParam { Text = r.CachedSystem, CacheControl = new CacheControlEphemeral() },
            };
            if (!string.IsNullOrWhiteSpace(r.System)) system.Add(new TextBlockParam { Text = r.System });

            var messages = r.Messages
                .Select(m => new MessageParam { Role = m.Role == LlmRole.User ? Role.User : Role.Assistant, Content = m.Content })
                .ToList();

            var effort = ToEffort(r.Effort ?? _profile.Effort);
            var outputConfig = string.IsNullOrEmpty(r.JsonSchema)
                ? new OutputConfig { Effort = effort }
                : new OutputConfig { Effort = effort, Format = new JsonOutputFormat { Schema = ParseSchema(r.JsonSchema) } };

            return new MessageCreateParams
            {
                Model = _profile.Model,
                MaxTokens = r.MaxTokens > 0 ? r.MaxTokens : _profile.MaxTokens,
                System = system,
                Messages = messages,
                Thinking = new ThinkingConfigAdaptive(),
                OutputConfig = outputConfig,
            };
        }

        internal static Effort ToEffort(string value)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "low": return Effort.Low;
                case "high": return Effort.High;
                case "max": return Effort.Max;
                default: return Effort.Medium;
            }
        }

        private static Dictionary<string, JsonElement> ParseSchema(string json)
        {
            using (var doc = JsonDocument.Parse(json))
            {
                return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            }
        }

        private static LlmException Translate(Exception ex)
        {
            switch (ex)
            {
                case LlmException _:
                case OperationCanceledException _:
                    return null;
                case AnthropicUnauthorizedException e: return new LlmException(LlmErrorKind.Authentication, e.Message, e);
                case AnthropicForbiddenException e: return new LlmException(LlmErrorKind.PermissionDenied, e.Message, e);
                case AnthropicNotFoundException e: return new LlmException(LlmErrorKind.NotFound, e.Message, e);
                case AnthropicRateLimitException e: return new LlmException(LlmErrorKind.RateLimited, e.Message, e);
                case AnthropicBadRequestException e: return new LlmException(LlmErrorKind.InvalidRequest, e.Message, e);
                case AnthropicUnprocessableEntityException e: return new LlmException(LlmErrorKind.InvalidRequest, e.Message, e);
                case Anthropic5xxException e: return new LlmException(LlmErrorKind.Server, e.Message, e);
                case AnthropicIOException e: return new LlmException(LlmErrorKind.Network, e.Message, e);
                case HttpRequestException e: return new LlmException(LlmErrorKind.Network, e.Message, e);
                case AnthropicApiException e: return new LlmException(LlmErrorKind.Unknown, e.Message, e);
                default: return null;
            }
        }
    }
}
```

- [ ] **Step 3: 컴파일·단위 테스트 실행(이름 불일치 수정 루프)**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~AnthropicProviderTests"`
Expected: PASS 15. 컴파일 오류(CS0117/CS1061/CS1503 등)가 나면 오류가 가리키는 이름만 `strings ~/.nuget/packages/anthropic/*/lib/netstandard2.0/Anthropic.dll | grep -i <이름>`로 확인해 고친다. `IAsyncEnumerable`/`WithCancellation`을 찾지 못하면 `dotnet add src/TechSupportReply.Core package Microsoft.Bcl.AsyncInterfaces`를 실행한다.

- [ ] **Step 4: 통합 테스트 실행(키가 있을 때)**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~LlmIntegrationTests"`
Expected: `ANTHROPIC_API_KEY`가 설정되어 있으면 Anthropic 테스트 3개 PASS. 없으면 3개 Skipped. 이 경우 사용자에게 "`! set ANTHROPIC_API_KEY=...` 후 재실행하면 실제 호출을 검증할 수 있다"고 보고한다.

- [ ] **Step 5: 커밋**

```bash
git add src/TechSupportReply.Core tests/TechSupportReply.Tests
git commit -m "feat(core): Anthropic Claude 공급자(적응형 사고, 캐싱, 구조화 출력, 스트리밍)"
```

---

### Task 9: OpenAI 공급자와 공급자 팩토리

**Files:**
- Create: `src/TechSupportReply.Core/Llm/OpenAiProvider.cs`, `src/TechSupportReply.Core/Llm/LlmProviderFactory.cs`
- Modify: `tests/TechSupportReply.Tests/Core/Llm/LlmIntegrationTests.cs` (OpenAI 테스트 추가)
- Test: `tests/TechSupportReply.Tests/Core/Llm/LlmProviderFactoryTests.cs`

**Interfaces:**
- Consumes: `LlmProfile`, `SecretStore`(Task 5), `AnthropicProvider`(Task 8).
- Produces: `OpenAiProvider(LlmProfile profile, string apiKey) : ILlmProvider` (`BaseUrl`이 있으면 해당 엔드포인트 사용, `Effort`는 무시).
- Produces: `LlmProviderFactory(SecretStore secrets)`, `ILlmProvider Create(LlmProfile profile)`(키 없으면 `LlmException(Authentication)`), `static ILlmProvider Create(LlmProfile profile, string apiKey)`.

- [ ] **Step 1: 실패 테스트 작성**

`tests/TechSupportReply.Tests/Core/Llm/LlmProviderFactoryTests.cs`:
```csharp
using TechSupportReply.Core.Llm;
using TechSupportReply.Core.Settings;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Core.Llm
{
    public class LlmProviderFactoryTests
    {
        [Fact]
        public void Create_ByProviderKind()
        {
            Assert.IsType<AnthropicProvider>(LlmProviderFactory.Create(
                new LlmProfile { Provider = LlmProviderKind.Anthropic, Model = "claude-opus-5" }, "k"));
            Assert.IsType<OpenAiProvider>(LlmProviderFactory.Create(
                new LlmProfile { Provider = LlmProviderKind.OpenAI, Model = "gpt-test" }, "k"));
        }

        [Fact]
        public void Create_OpenAiWithBaseUrl_DoesNotThrow()
        {
            Assert.NotNull(LlmProviderFactory.Create(
                new LlmProfile { Provider = LlmProviderKind.OpenAI, Model = "local-model", BaseUrl = "http://localhost:8000/v1" }, "k"));
        }

        [Fact]
        public void Create_FromSecretStore_UsesStoredKey()
        {
            using (var tmp = new TempDir())
            {
                var secrets = new SecretStore(tmp.Root);
                secrets.Set("s1", "sk-ant-x");
                var provider = new LlmProviderFactory(secrets).Create(
                    new LlmProfile { DisplayName = "팀 키", Provider = LlmProviderKind.Anthropic, Model = "claude-opus-5", SecretId = "s1" });
                Assert.Equal("팀 키", provider.DisplayName);
            }
        }

        [Fact]
        public void Create_MissingSecret_ThrowsAuthenticationWithProfileName()
        {
            using (var tmp = new TempDir())
            {
                var ex = Assert.Throws<LlmException>(() => new LlmProviderFactory(new SecretStore(tmp.Root)).Create(
                    new LlmProfile { DisplayName = "개인 키", Provider = LlmProviderKind.OpenAI, Model = "m", SecretId = "none" }));
                Assert.Equal(LlmErrorKind.Authentication, ex.Kind);
                Assert.Contains("개인 키", ex.Message);
            }
        }

        [Fact]
        public void OpenAi_EmptyModel_Throws()
        {
            Assert.Throws<System.ArgumentException>(() =>
                new OpenAiProvider(new LlmProfile { Provider = LlmProviderKind.OpenAI, Model = "" }, "k"));
        }
    }
}
```

`LlmIntegrationTests.cs` 클래스 끝에 추가:
```csharp
        private static OpenAiProvider OpenAi(string key, string model) => new OpenAiProvider(new LlmProfile
        {
            DisplayName = "it-openai",
            Provider = LlmProviderKind.OpenAI,
            Model = model,
            BaseUrl = Env("OPENAI_TEST_BASE_URL") ?? "",
        }, key);

        [SkippableFact]
        public async Task OpenAi_Stream_ReturnsText_AndDeltasMatch()
        {
            var key = Env("OPENAI_API_KEY");
            var model = Env("OPENAI_TEST_MODEL");
            Skip.If(key == null || model == null, "OPENAI_API_KEY와 OPENAI_TEST_MODEL이 필요합니다");
            var deltas = new StringBuilder();
            var text = await OpenAi(key, model).StreamAsync(Hello(), d => deltas.Append(d), CancellationToken.None);
            Assert.Contains("연결", text);
            Assert.Equal(text, deltas.ToString());
        }

        [SkippableFact]
        public async Task OpenAi_StructuredClassification_Fluent()
        {
            var key = Env("OPENAI_API_KEY");
            var model = Env("OPENAI_TEST_MODEL");
            Skip.If(key == null || model == null, "OPENAI_API_KEY와 OPENAI_TEST_MODEL이 필요합니다");
            var mail = Mails.Create("계산 발산 문의", "Fluent에서 residual이 줄지 않고 계산이 발산합니다.");
            var r = await new ProductClassifier(ProductCatalog.CreateDefault()).ClassifyAsync(mail, OpenAi(key, model), CancellationToken.None);
            Assert.Equal(ClassificationSource.Llm, r.Source);
            Assert.Equal("ansys-fluent", r.ProductId);
        }
```

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~LlmProviderFactoryTests"`
Expected: 빌드 실패(`OpenAiProvider`, `LlmProviderFactory` 없음).

- [ ] **Step 2: 패키지 추가와 구현**

```bash
dotnet add src/TechSupportReply.Core package OpenAI
```

`src/TechSupportReply.Core/Llm/OpenAiProvider.cs`:
```csharp
using System;
using System.ClientModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenAI;
using OpenAI.Chat;
using TechSupportReply.Core.Settings;

namespace TechSupportReply.Core.Llm
{
    /// <summary>공식 OpenAI .NET SDK 기반 공급자. BaseUrl로 OpenAI 호환 엔드포인트도 사용할 수 있다.</summary>
    public sealed class OpenAiProvider : ILlmProvider
    {
        private readonly LlmProfile _profile;
        private readonly ChatClient _chat;

        public OpenAiProvider(LlmProfile profile, string apiKey)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            if (string.IsNullOrWhiteSpace(profile.Model)) throw new ArgumentException("모델명이 비어 있습니다.");
            if (string.IsNullOrWhiteSpace(apiKey)) throw new LlmException(LlmErrorKind.Authentication, "API 키가 비어 있습니다.");
            var options = new OpenAIClientOptions();
            if (!string.IsNullOrWhiteSpace(profile.BaseUrl)) options.Endpoint = new Uri(profile.BaseUrl);
            _chat = new ChatClient(profile.Model, new ApiKeyCredential(apiKey), options);
        }

        public string DisplayName => _profile.DisplayName;

        public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            var (messages, options) = Build(request);
            try
            {
                ClientResult<ChatCompletion> result = await _chat.CompleteChatAsync(messages, options, ct).ConfigureAwait(false);
                var completion = result.Value;
                if (!string.IsNullOrEmpty(completion.Refusal)) throw new LlmException(LlmErrorKind.Refusal, completion.Refusal);
                return string.Concat(completion.Content.Select(p => p.Text));
            }
            catch (Exception ex) when (Translate(ex) is LlmException mapped)
            {
                throw mapped;
            }
        }

        public async Task<string> StreamAsync(LlmRequest request, Action<string> onDelta, CancellationToken ct)
        {
            var (messages, options) = Build(request);
            var text = new StringBuilder();
            var refusal = new StringBuilder();
            try
            {
                await foreach (StreamingChatCompletionUpdate update in _chat.CompleteChatStreamingAsync(messages, options, ct).WithCancellation(ct).ConfigureAwait(false))
                {
                    if (!string.IsNullOrEmpty(update.RefusalUpdate)) refusal.Append(update.RefusalUpdate);
                    foreach (ChatMessageContentPart part in update.ContentUpdate)
                    {
                        if (string.IsNullOrEmpty(part.Text)) continue;
                        text.Append(part.Text);
                        onDelta?.Invoke(part.Text);
                    }
                }
            }
            catch (Exception ex) when (Translate(ex) is LlmException mapped)
            {
                throw mapped;
            }
            if (refusal.Length > 0 && text.Length == 0) throw new LlmException(LlmErrorKind.Refusal, refusal.ToString());
            return text.ToString();
        }

        private (List<ChatMessage> Messages, ChatCompletionOptions Options) Build(LlmRequest r)
        {
            var messages = new List<ChatMessage>();
            var system = string.Join("\n\n", new[] { r.CachedSystem, r.System }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (system.Length > 0) messages.Add(new SystemChatMessage(system));
            foreach (var m in r.Messages)
                messages.Add(m.Role == LlmRole.User ? (ChatMessage)new UserChatMessage(m.Content) : new AssistantChatMessage(m.Content));

            var options = new ChatCompletionOptions { MaxOutputTokenCount = r.MaxTokens > 0 ? r.MaxTokens : _profile.MaxTokens };
            if (!string.IsNullOrEmpty(r.JsonSchema))
                options.ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(r.SchemaName, BinaryData.FromString(r.JsonSchema), jsonSchemaIsStrict: true);
            return (messages, options);
        }

        private static LlmException Translate(Exception ex)
        {
            switch (ex)
            {
                case LlmException _:
                case OperationCanceledException _:
                    return null;
                case ClientResultException e when e.Status == 401: return new LlmException(LlmErrorKind.Authentication, e.Message, e);
                case ClientResultException e when e.Status == 403: return new LlmException(LlmErrorKind.PermissionDenied, e.Message, e);
                case ClientResultException e when e.Status == 404: return new LlmException(LlmErrorKind.NotFound, e.Message, e);
                case ClientResultException e when e.Status == 429: return new LlmException(LlmErrorKind.RateLimited, e.Message, e);
                case ClientResultException e when e.Status >= 500: return new LlmException(LlmErrorKind.Server, e.Message, e);
                case ClientResultException e when e.Status == 0: return new LlmException(LlmErrorKind.Network, e.Message, e);
                case ClientResultException e: return new LlmException(LlmErrorKind.InvalidRequest, e.Message, e);
                case HttpRequestException e: return new LlmException(LlmErrorKind.Network, e.Message, e);
                case IOException e: return new LlmException(LlmErrorKind.Network, e.Message, e);
                default: return null;
            }
        }
    }
}
```

`src/TechSupportReply.Core/Llm/LlmProviderFactory.cs`:
```csharp
using System;
using TechSupportReply.Core.Settings;

namespace TechSupportReply.Core.Llm
{
    public sealed class LlmProviderFactory
    {
        private readonly SecretStore _secrets;

        public LlmProviderFactory(SecretStore secrets)
        {
            _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        }

        public ILlmProvider Create(LlmProfile profile)
        {
            var key = _secrets.Get(profile.SecretId);
            if (string.IsNullOrWhiteSpace(key))
                throw new LlmException(LlmErrorKind.Authentication, $"'{profile.DisplayName}' 프로필에 API 키가 등록되지 않았습니다.");
            return Create(profile, key);
        }

        public static ILlmProvider Create(LlmProfile profile, string apiKey)
        {
            switch (profile.Provider)
            {
                case LlmProviderKind.Anthropic: return new AnthropicProvider(profile, apiKey);
                case LlmProviderKind.OpenAI: return new OpenAiProvider(profile, apiKey);
                default: throw new NotSupportedException($"지원하지 않는 공급자: {profile.Provider}");
            }
        }
    }
}
```

- [ ] **Step 3: 테스트 실행**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~LlmProviderFactoryTests|FullyQualifiedName~LlmIntegrationTests"`
Expected: 팩토리 테스트 PASS 5. 통합 테스트는 환경 변수가 있으면 PASS, 없으면 Skipped. 컴파일 오류가 나면 Task 8 Step 3과 같은 방식으로 `~/.nuget/packages/openai/*/lib/netstandard2.0/OpenAI.dll`에서 이름을 확인한다.

- [ ] **Step 4: 커밋**

```bash
git add src/TechSupportReply.Core tests/TechSupportReply.Tests
git commit -m "feat(core): OpenAI 공급자와 프로필 기반 공급자 팩토리"
```

---

### Task 10: 검색용 용어 정규화기 (한글 bigram)

**Files:**
- Create: `src/TechSupportReply.Rag/Search/SearchTextNormalizer.cs`
- Test: `tests/TechSupportReply.Tests/Rag/Search/SearchTextNormalizerTests.cs`

**Interfaces:**
- Produces: `static IReadOnlyList<string> SearchTextNormalizer.Terms(string text)`, `static string ToIndexText(string text)`(공백 연결), `static string ToMatchQuery(string text, int maxTerms = 64)`(FTS5 MATCH 식, 유효 용어가 없으면 null).

- [ ] **Step 1: 실패 테스트 작성**

`tests/TechSupportReply.Tests/Rag/Search/SearchTextNormalizerTests.cs`:
```csharp
using TechSupportReply.Rag.Search;
using Xunit;

namespace TechSupportReply.Tests.Rag.Search
{
    public class SearchTextNormalizerTests
    {
        [Fact]
        public void Terms_KeywordCard_KeepsFullTokenAndParts()
        {
            var terms = SearchTextNormalizer.Terms("*CONTACT_AUTOMATIC_SURFACE_TO_SURFACE");
            Assert.Contains("contact_automatic_surface_to_surface", terms);
            Assert.Contains("contact", terms);
            Assert.Contains("automatic", terms);
            Assert.Contains("surface", terms);
        }

        [Fact]
        public void Terms_KoreanTwoSyllable()
        {
            Assert.Equal(new[] { "접촉", "관통" }, SearchTextNormalizer.Terms("접촉 관통"));
            Assert.Equal(new[] { "수렴" }, SearchTextNormalizer.Terms("수렴"));
        }

        [Fact]
        public void Terms_KoreanWithParticle_ProducesBigrams()
        {
            Assert.Equal(new[] { "접촉", "촉이" }, SearchTextNormalizer.Terms("접촉이"));
        }

        [Fact]
        public void Terms_FullWidth_IsNormalized()
        {
            Assert.Equal(new[] { "ls", "dyna" }, SearchTextNormalizer.Terms("ＬＳ－ＤＹＮＡ"));
        }

        [Fact]
        public void Terms_DropsSingleLatinLetters()
        {
            Assert.Equal(new[] { "d3hsp" }, SearchTextNormalizer.Terms("a d3hsp"));
        }

        [Fact]
        public void ToMatchQuery_OnlyStopTerms_ReturnsNull()
        {
            Assert.Null(SearchTextNormalizer.ToMatchQuery("안녕하세요 감사합니다"));
            Assert.Null(SearchTextNormalizer.ToMatchQuery(""));
        }

        [Fact]
        public void ToMatchQuery_QuotesAndOrsDistinctTerms()
        {
            Assert.Equal("\"fluent\" OR \"수렴\"", SearchTextNormalizer.ToMatchQuery("Fluent 수렴 fluent"));
        }

        [Fact]
        public void ToMatchQuery_LimitsTermCount()
        {
            var q = SearchTextNormalizer.ToMatchQuery("aa bb cc dd ee", 2);
            Assert.Equal("\"aa\" OR \"bb\"", q);
        }
    }
}
```

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~SearchTextNormalizerTests"`
Expected: 빌드 실패.

- [ ] **Step 2: 구현**

`src/TechSupportReply.Rag/Search/SearchTextNormalizer.cs`:
```csharp
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
```

- [ ] **Step 3: 테스트 실행**

Run: `dotnet test tests/TechSupportReply.Tests --filter "FullyQualifiedName~SearchTextNormalizerTests"`
Expected: PASS 8.

- [ ] **Step 4: 커밋**

```bash
git add src/TechSupportReply.Rag tests/TechSupportReply.Tests
git commit -m "feat(rag): 한글 bigram 기반 검색 용어 정규화기"
```

---

### Task 11+ (작성 예정)

Task 11 이후(로더·청킹·저장소·검색·색인 빌더·게시·동기화·KnowledgeRetriever·PromptBuilder·ReplyGenerator·Indexer CLI)는 Task 1–10 실행 후 이 문서에 이어서 작성한다.
