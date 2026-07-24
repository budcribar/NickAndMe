# Issue 13 — Default JWT signing key committed for production use

| Field | Value |
|-------|-------|
| Severity | suggestion |
| Status | **fixed** |
| Branch | `fix/issue-13-default-jwt-key` |
| Related files | `host/PageToMovie.Core/Options/PageToMovieOptions.cs`; `host/PageToMovie.Api/Program.cs`; `AdminAuthService.cs` |

## Problem

Default JWT signing key was a committed dev constant (`PageToMovie-Dev-Only-Change-Me-32chars!!`). Production without `PageToMovie_JWT_KEY` accepted forged admin tokens if the key was known.

## Fix implemented

1. **`AuthOptions.DefaultDevJwtSigningKey`** + **`IsInsecureDefaultJwtSigningKey`** helper.
2. **Api startup** (after `Build`): outside Development, throw if effective key (env or config) is still the default.
3. **`AdminAuthService.ResolveSigningKey`**: same refusal when issuing/validating tokens outside Development.
4. **appsettings** comment notes the env override.

## Suggested fix (original)

Refuse to start with default key outside Development; require env override.
