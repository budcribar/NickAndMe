# Issue 15 — SupportedModelCatalog comments lag multi-provider wiring

| Field | Value |
|-------|-------|
| Severity | nit |
| Status | **fixed** |
| Branch | `fix/issue-15-catalog-stale-comments` |
| Related files | `host/FilmStudio.Core/Models/SupportedModelCatalog.cs` |

## Problem

Comments still said Google/Anthropic "reserved; not fully wired" / "No client wired yet" while Gemini*Client / AnthropicChatClient and multi-provider dispatch exist. Stale docs mislead operators and agents.

## Fix implemented

1. **`ModelProviderFamily`** — document real clients and remaining gaps (Veo no extend/refs; OCR/cast Grok-only).
2. **`GoogleApiBase` / `AnthropicApiBase`** — replace "No client wired yet" with actual client names and gaps.
3. **Grok vision entry Notes** — describe OCR / cast classify / frame review (not "when wired").

Per-model Notes for Veo / Claude / Gemini chat-image-vision were already accurate and left intact.
