# Open-Source Fountain Sample Files

These 20 `.fountain` files are pulled from real open-source repositories used
by Fountain parser/tooling authors as their own test fixtures — not
hand-written for this task.

## Sources

**nyousefi/Fountain** (MIT license) — the original reference implementation
by Nima Yousefi & John August, co-creators of the Fountain spec:
https://github.com/nyousefi/Fountain
- `Big_Fish_nyousefi.fountain` — real excerpt of John August's "Big Fish"
  screenplay, the format's canonical full-length demo script (note: dialogue
  content is © Columbia Pictures; distributed by the format's creators
  specifically as a parser test sample, not for redistribution as a work).
- `Brick_And_Steel_nyousefi.fountain` — the other classic Fountain demo
  script (a short film script), also written by John August.
- `Simple_nyousefi.fountain`, `Dialogue_nyousefi.fountain`,
  `DualDialogue_nyousefi.fountain`, `Transitions_nyousefi.fountain`,
  `CenteredText_nyousefi.fountain`, `SceneHeaders_nyousefi.fountain`,
  `SceneNumbers_nyousefi.fountain`, `ForcedElements_nyousefi.fountain`,
  `Indenting_nyousefi.fountain`, `Synopses_nyousefi.fountain`,
  `SectionHeaders_nyousefi.fountain`, `SectionsComplex_nyousefi.fountain`,
  `Boneyard_nyousefi.fountain`, `Notes_nyousefi.fountain`,
  `MultilineAction_nyousefi.fountain`, `PageBreaks_nyousefi.fountain` — the
  reference unit-test fixtures, one per syntax feature, used to validate the
  original Fountain parser implementation.

**wildwinter/screenplay-tools** (MIT license) — an actively maintained
multi-language Fountain/FDX parser (formerly `fountain-tools`):
https://github.com/wildwinter/screenplay-tools
- `TitlePage_screenplaytools.fountain` — title page metadata edge cases.
- `UTF8_screenplaytools.fountain` — non-ASCII / UTF-8 character handling.

## Note
Both repos are MIT-licensed for their code and test infrastructure. The
`Big_Fish` file is a real, previously-produced screenplay excerpt and carries
its own separate copyright — it's included here only because it's the
standard sample the Fountain community itself uses to test parsers against
a full-length, real-world script.
