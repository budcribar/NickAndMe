from pypdf import PdfReader

reader = PdfReader("Nickandme.PDF")
print(f"Number of pages: {len(reader.pages)}")
print("--- PAGE 94 FULL TEXT ---")
print(reader.pages[93].extract_text())
