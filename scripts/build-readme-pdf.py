"""Render the public quick-start README as one printable page. Requires reportlab and pypdf."""
from pathlib import Path
from xml.sax.saxutils import escape
from reportlab.lib import colors
from reportlab.lib.pagesizes import letter
from reportlab.lib.styles import ParagraphStyle
from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer, KeepTogether
from pypdf import PdfReader

root = Path(__file__).resolve().parents[1]
source = root / "docs" / "READ-ME-FIRST.md"
output = root / "output" / "pdf" / "Alexandra-Read-Me.pdf"
output.parent.mkdir(parents=True, exist_ok=True)
text = source.read_text(encoding="utf-8")
lines = text.splitlines()
title = lines[0].removeprefix("# ")
subtitle = next(line for line in lines[1:] if line.strip())
sections = []
for line in lines:
    if line.startswith("## "):
        sections.append((line[3:], []))
    elif line.startswith("- "):
        sections[-1][1].append(line[2:])

ink = colors.HexColor("#20262E")
orange = colors.HexColor("#C66D00")
muted = colors.HexColor("#525C68")
heading = ParagraphStyle("section", fontName="Helvetica-Bold", fontSize=10.5,
                         leading=12.6, textColor=orange, spaceBefore=7, spaceAfter=3)
bullet = ParagraphStyle("bullet", fontName="Helvetica", fontSize=9.7, leading=12.4,
                        textColor=ink, leftIndent=10, firstLineIndent=-8, spaceAfter=2.5)
small = ParagraphStyle("footer", fontName="Helvetica", fontSize=8, leading=10, textColor=muted)

def chrome(canvas, doc):
    canvas.saveState()
    canvas.setFillColor(ink)
    canvas.rect(0, letter[1] - 85, letter[0], 85, fill=1, stroke=0)
    canvas.setFillColor(colors.HexColor("#FFA000"))
    canvas.setFont("Helvetica-Bold", 25)
    canvas.drawString(42, letter[1] - 41, title.upper())
    canvas.setFillColor(colors.white)
    canvas.setFont("Helvetica", 10)
    canvas.drawString(43, letter[1] - 62, subtitle)
    canvas.setFillColor(orange)
    canvas.rect(42, letter[1] - 87, letter[0] - 84, 2, fill=1, stroke=0)
    canvas.setStrokeColor(colors.HexColor("#D9DEE4"))
    canvas.line(42, 37, letter[0] - 42, 37)
    canvas.setFillColor(muted)
    canvas.setFont("Helvetica", 8)
    canvas.drawString(42, 24, "ALEXANDRA / FRIENDS BETA")
    canvas.drawRightString(letter[0] - 42, 24, "1 / 1")
    canvas.restoreState()

story = []
for name, items in sections:
    # Keep each heading with its first bullet; never leave a heading behind at a break.
    first = [Paragraph(escape(name.upper()), heading), Paragraph("- " + escape(items[0]), bullet)]
    story.append(KeepTogether(first))
    story.extend(Paragraph("- " + escape(item), bullet) for item in items[1:])
story.append(Spacer(1, 5))
url = "https://github.com/robotdestroy/Alexandra-Releases/releases/latest"
story.append(Paragraph(f'<link href="{url}" color="#A95700">Official download and updates: github.com/robotdestroy/Alexandra-Releases</link>', small))
doc = SimpleDocTemplate(str(output), pagesize=letter, leftMargin=42, rightMargin=42,
                        topMargin=99, bottomMargin=44, title="Alexandra Read Me", author="Alexandra",
                        subject="Quick start and features for the Windows friends beta")
doc.build(story, onFirstPage=chrome, onLaterPages=chrome)
reader = PdfReader(str(output))
if len(reader.pages) != 1:
    raise RuntimeError(f"The quick-start guide must remain one page; got {len(reader.pages)}.")
extracted = reader.pages[0].extract_text()
for name, _ in sections:
    if name.upper() not in extracted:
        raise RuntimeError(f"Missing section: {name}")
print(f"Created one-page guide with {sum(len(items) for _, items in sections)} bullets: {output}")
