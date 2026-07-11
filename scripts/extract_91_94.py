from pypdf import PdfReader

reader = PdfReader("Nickandme.PDF")

# Print pages 91 to 94 (0-indexed page 90 to 93)
for i in range(90, 94):
    if i < len(reader.pages):
        print(f"--- PAGE {i+1} ---")
        print(reader.pages[i].extract_text()[:4000])
