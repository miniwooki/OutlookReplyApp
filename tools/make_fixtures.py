"""테스트 픽스처(PDF/DOCX/XLSX/EML/CP949)를 생성한다.

사용법:
  python tools/make_fixtures.py            # tests/TechSupportReply.Tests/Fixtures/docs/
  python tools/make_fixtures.py --samples  # samples/kb 의 PDF 매뉴얼
"""
import io
import pathlib
import sys
from email.message import EmailMessage
from email.utils import format_datetime
from datetime import datetime, timezone, timedelta

from docx import Document
from fpdf import FPDF
from openpyxl import Workbook
from PIL import Image, ImageDraw

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / "tests" / "TechSupportReply.Tests" / "Fixtures" / "docs"
FONT = pathlib.Path(r"C:\Windows\Fonts\malgun.ttf")
KST = timezone(timedelta(hours=9))


def new_pdf():
    pdf = FPDF()
    pdf.add_font("malgun", fname=str(FONT))
    pdf.set_font("malgun", size=11)
    return pdf


def write_pages(pdf, pages):
    for text in pages:
        pdf.add_page()
        pdf.multi_cell(0, 7, text)


def make_pdfs():
    pdf = new_pdf()
    write_pages(pdf, [
        "LS-DYNA Keyword Manual 발췌\n*CONTACT_AUTOMATIC_SURFACE_TO_SURFACE 는 세그먼트 기반 접촉을 정의합니다.",
        "SOFT=2 옵션은 초기 관통이 있는 접촉에서 안정적입니다.\nIGNORE=1 로 초기 관통 경고를 무시할 수 있습니다.",
    ])
    pdf.output(str(OUT / "manual.pdf"))

    pdf = new_pdf()
    pdf.set_encryption(owner_password="owner", user_password="user")
    write_pages(pdf, ["암호로 보호된 문서입니다."])
    pdf.output(str(OUT / "encrypted.pdf"))

    img = Image.new("RGB", (600, 200), "white")
    ImageDraw.Draw(img).text((20, 80), "scanned page image only", fill="black")
    buf = io.BytesIO()
    img.save(buf, format="PNG")
    buf.seek(0)
    pdf = FPDF()
    pdf.add_page()
    pdf.image(buf, x=10, y=10, w=180)
    pdf.output(str(OUT / "scanned.pdf"))

    (OUT / "corrupt.pdf").write_bytes(b"%PDF-1.7\n" + bytes(range(256)) * 4)


def make_docx():
    doc = Document()
    doc.add_paragraph("문서 머리말입니다.")
    doc.add_heading("질량 스케일링", level=1)
    doc.add_paragraph("*CONTROL_TIMESTEP 의 DT2MS 로 질량 스케일링을 설정합니다.")
    doc.add_heading("접촉 설정", level=2)
    doc.add_paragraph("접촉 두께는 SST 로 조정합니다.")
    table = doc.add_table(rows=2, cols=2)
    table.cell(0, 0).text = "옵션"
    table.cell(0, 1).text = "설명"
    table.cell(1, 0).text = "SOFT=2"
    table.cell(1, 1).text = "세그먼트 기반"
    doc.save(str(OUT / "guide.docx"))


def make_xlsx():
    wb = Workbook()
    ws = wb.active
    ws.title = "이슈"
    ws.append(["증상", "원인", "조치"])
    ws.append(["음수 부피", "요소 왜곡", "메시 개선"])
    ws.append(["초기 관통", None, "IGNORE=1"])
    ws2 = wb.create_sheet("버전")
    ws2.append(["버전", "비고"])
    ws2.append([13.1, "MPP 권장"])
    wb.save(str(OUT / "issues.xlsx"))


def eml(subject, sender, to, date, text=None, html=None):
    msg = EmailMessage()
    msg["Subject"] = subject
    msg["From"] = sender
    msg["To"] = to
    msg["Date"] = format_datetime(date)
    if text is not None:
        msg.set_content(text)
    if html is not None:
        if text is None:
            msg.set_content(html, subtype="html")
        else:
            msg.add_alternative(html, subtype="html")
    return bytes(msg)


def make_emls():
    reply = (
        "안녕하세요, KOSTECH 기술지원팀입니다.\n\n"
        "초기 관통 경고는 *CONTACT 카드의 SOFT=2 또는 IGNORE=1 옵션으로 해결할 수 있습니다.\n\n"
        "-- \n홍길동 / KOSTECH 기술지원팀\n\n"
        "-----Original Message-----\n"
        "From: 김고객 <customer@example.com>\n"
        "Sent: Monday, September 1, 2026 10:00 AM\n"
        "Subject: 접촉 관통 문의\n\n"
        "LS-DYNA 해석에서 초기 관통 경고가 많이 나옵니다. 어떻게 해야 하나요?\n\n"
        "-----Original Message-----\n"
        "From: 지원팀 <support@kostech.example>\n"
        "Sent: Friday, August 29, 2026 10:00 AM\n\n"
        "더 오래된 스레드 내용입니다.\n"
    )
    date = datetime(2026, 9, 2, 9, 30, tzinfo=KST)
    (OUT / "reply.eml").write_bytes(eml("RE: 접촉 관통 문의", "홍길동 <support@kostech.example>", "customer@example.com", date, text=reply))

    html = (
        "<html><head><style>p{color:red}</style></head><body>"
        "<p>Fluent 수렴 문제는 under-relaxation 계수를 낮추세요.</p>"
        "<p>감사합니다 &amp; 좋은 하루 되세요.</p>"
        "<div>보낸 사람: 이고객 &lt;lee@example.com&gt;<br>보낸 날짜: 2026년 9월 1일 월요일 오전 10:00<br>"
        "받는 사람: support@kostech.example<br>제목: 수렴 문의</div>"
        "<p>계산이 발산합니다.</p></body></html>"
    )
    (OUT / "reply_html.eml").write_bytes(eml("RE: 수렴 문의", "support@kostech.example", "lee@example.com", date, html=html))


def make_cp949():
    (OUT / "notes_cp949.txt").write_bytes("접촉 관통 경고 메모\n수렴 실패 시 조치".encode("cp949"))
    (OUT / "issues_cp949.csv").write_bytes('증상,조치\r\n"수렴 실패, 발산","완화 계수 조정"\r\n'.encode("cp949"))


def make_sample_manuals():
    kb = ROOT / "samples" / "kb"
    pdf = new_pdf()
    write_pages(pdf, [
        "LS-DYNA 접촉 가이드\n\n*CONTACT_AUTOMATIC_SURFACE_TO_SURFACE 에서 초기 관통(initial penetration)이 있으면 "
        "d3hsp 에 경고가 출력됩니다. IGNORE=1 은 초기 관통을 무시하고, SOFT=2 는 세그먼트 기반 접촉으로 "
        "얇은 쉘이나 날카로운 모서리에서 안정적입니다.",
        "음수 부피(negative volume) 오류는 요소가 과도하게 왜곡될 때 발생합니다. "
        "*CONTROL_SOLID 의 ERODE, 메시 품질 개선, 시간 간격 축소(*CONTROL_TIMESTEP TSSFAC)를 검토하세요.",
    ])
    (kb / "LS-DYNA" / "manuals").mkdir(parents=True, exist_ok=True)
    pdf.output(str(kb / "LS-DYNA" / "manuals" / "contact_guide.pdf"))

    pdf = new_pdf()
    write_pages(pdf, [
        "Ansys Fluent 수렴 가이드\n\nresidual 이 줄지 않고 발산하면 under-relaxation 계수(압력 0.3, 운동량 0.7)를 "
        "낮추고, 초기화를 hybrid initialization 으로 바꿔 보세요. 메시 skewness 가 0.95 이상이면 개선이 필요합니다.",
    ])
    (kb / "Ansys-Fluent" / "manuals").mkdir(parents=True, exist_ok=True)
    pdf.output(str(kb / "Ansys-Fluent" / "manuals" / "convergence_guide.pdf"))
    print("wrote sample manuals")


if __name__ == "__main__":
    if "--samples" in sys.argv:
        make_sample_manuals()
    else:
        OUT.mkdir(parents=True, exist_ok=True)
        make_pdfs()
        make_docx()
        make_xlsx()
        make_emls()
        make_cp949()
        print(f"wrote fixtures -> {OUT}")
