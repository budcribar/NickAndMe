# Issue 17 — Video download falls back across providers on any error

| Field | Value |
|-------|-------|
| Severity | suggestion |
| Status | **fixed** |
| Branch | `fix/issue-17-multiprovider-download-fallback` |
| Related files | `host/PageToMovie.Engine/MultiProviderVideoClient.cs` |

## Problem

Download tried Grok first and fell back to Gemini on **any** catch when Gemini was configured. A transient Grok failure downloading a Grok URL could hit Gemini (wrong auth/host) and obscure the real error.

## Fix implemented

1. **Route download by URL host** (`InferProviderFromDownloadUrl`): x.ai → Grok; googleapis / googleusercontent → Gemini.
2. **No cross-provider fallback** on failure — single client attempt.
3. Unknown host: use Grok if configured, else Gemini (still one attempt).

## Suggested fix (original)

Route download by tagged request id / URL host, or only fallback on 401/403.
