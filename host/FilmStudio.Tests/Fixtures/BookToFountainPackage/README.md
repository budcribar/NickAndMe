# Book-to-Fountain Test Package

Two folders:

## /source_texts/ — real public-domain source texts (7 of 10)

Downloaded as-is from **GITenberg** (github.com/GITenberg), a project that
mirrors genuinely public-domain Project Gutenberg books as git repos. These
are the real, unabridged original novels/stories, not adaptations:

- Alices_Adventures_in_Wonderland.txt (Lewis Carroll, 1865)
- A_Christmas_Carol.txt (Charles Dickens, 1843)
- Dracula.txt (Bram Stoker, 1897)
- Frankenstein.txt (Mary Shelley, 1818)
- The_Jungle_Book.txt (Rudyard Kipling, 1894)
- The_Yellow_Wallpaper.txt (Charlotte Perkins Gilman, 1892)
- The_Gift_of_the_Magi.txt (O. Henry, 1905)

**Not included as source text:** "The Tell-Tale Heart" and "The Raven"
(Poe) aren't published standalone on Gutenberg — they're bundled inside a
multi-hundred-page "Complete Works" volume, so pulling a clean individual
text file wasn't practical here. You can get clean standalone copies at:
- Tell-Tale Heart: https://www.gutenberg.org/ebooks/2148 (Poe, Vol. 2)
- The Raven: https://www.gutenberg.org/ebooks/25525 (Raven Edition, Vol. 1)

**"The Lottery" (Shirley Jackson) is NOT included** — it was published in
1948 and is still under copyright (protected until ~2043 in the US), so I
couldn't download or adapt it. I substituted **"The Monkey's Paw"** by
W.W. Jacobs (1902), which is genuinely public domain.

## /fountain_adaptations/ — original screenplay adaptations (10 of 10)

These are original scenes I wrote myself, adapting the public-domain
plots/characters/dialogue beats into proper Fountain syntax. They are
**not** transcriptions of the source text — they're new scene-by-scene
adaptations exercising different parser features:

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

All ten include a title page block, and lean on standard Fountain syntax
(scene headings, action, dialogue, parentheticals, transitions, V.O./O.S.,
CONT'D). Good luck with the parser!
