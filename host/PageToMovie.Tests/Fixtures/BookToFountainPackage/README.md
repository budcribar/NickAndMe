# Book-to-Fountain Test Package

## Source texts → repo root `books/`

Public-domain source texts (and the Buster picture-book PDF) live at:

```text
books/   # repo root, not under Fixtures
```

Contents currently include:

- `Alices_Adventures_in_Wonderland.txt` (Lewis Carroll, 1865)
- `A_Christmas_Carol.txt` (Charles Dickens, 1843)
- `Dracula.txt` (Bram Stoker, 1897)
- `Frankenstein.txt` (Mary Shelley, 1818)
- `The_Jungle_Book.txt` (Rudyard Kipling, 1894)
- `The_Yellow_Wallpaper.txt` (Charlotte Perkins Gilman, 1892)
- `The_Gift_of_the_Magi.txt` (O. Henry, 1905)
- `The_Tell-Tale_Heart.txt` (Edgar Allan Poe)
- `Buster_Noodle_Head_Dog_Goes_to_Bed_merged.pdf` (sample picture book)

Most plain-text titles were downloaded as-is from **GITenberg** (github.com/GITenberg),
mirroring public-domain Project Gutenberg books.

**Not included as source text:** "The Raven" isn't published as a clean standalone
on Gutenberg the same way — see https://www.gutenberg.org/ebooks/25525.

**"The Lottery" (Shirley Jackson) is NOT included** — still under copyright
(US ~2043). The package substitutes **"The Monkey's Paw"** by W.W. Jacobs (1902)
as a fountain adaptation only.

## /fountain_adaptations/ — original screenplay adaptations (10 of 10)

These are original scenes adapting public-domain plots/characters into Fountain
syntax for parser tests. They are **not** transcriptions of the source text:

| File | Notable for parser testing |
|---|---|
| 01_Alices_Adventures_in_Wonderland.fountain | Many characters, surreal scene shifts, V.O. |
| 02_A_Christmas_Carol.fountain | Heavy transitions (CUT TO/DISSOLVE), ghost scenes |
| 03_Dracula.fountain | Long, multi-location, epistolary V.O. narration |
| 04_Frankenstein.fountain | Nested flashback (dissolve into memory), emotional beats |
| 05_The_Jungle_Book.fountain | Many named characters, animal dialogue |
| 06_The_Gift_of_the_Magi.fountain | Short, dialogue + narrator wraparound |
| 07_The_Tell-Tale_Heart.fountain | Monologue-heavy, single narrator, minimal cast |
| 08_The_Yellow_Wallpaper.fountain | Psychological V.O. narration, sparse action |
| 09_The_Raven.fountain | Poem-to-screenplay, unusual pacing, single location |
| 10_The_Monkeys_Paw.fountain | Substituted for "The Lottery" (see above) |

All ten include a title page block and standard Fountain syntax
(scene headings, action, dialogue, parentheticals, transitions, V.O./O.S.,
CONT'D).
