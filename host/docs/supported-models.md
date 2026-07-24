# Supported models (master catalog)

Users pick **model ids** in Configuration. The app never asks them to pick a “service” separately.

`PageToMovie.Core.Models.SupportedModelCatalog` is the single source of truth for:

| Field | Purpose |
|--------|---------|
| `Id` | What the user selects / what we send to the API |
| `Capability` | video · image · chat · vision |
| `Provider` | xai · google (family) |
| `ApiBase` | e.g. `https://api.x.ai/v1` |
| `EndpointPath` | e.g. `videos/generations`, `images/generations` |
| `RequiredEnvKeys` | e.g. `XAI_API_KEY` |
| `Enabled` | shown in Configuration when true |

**Voice samples:** not a TTS model. Characters → Play uses short **video** gen + VOICE LOCK, then extracts audio only.

API: `GET /api/models` and `GET /api/models?capability=video`.

On save, Configuration writes `video_provider` / `image_provider` / `qa_provider` derived from the model catalog (for cost reports).

## Adding a model

1. **Wire the client** (endpoint path, auth, request shape).
2. **Add a row** to `SupportedModelCatalog` with correct capability, keys, and endpoint.
3. Ship. Users can then select it.

## Not supported yet (GitHub feature requests)

Do **not** add half-working models to the enabled list.

Open a GitHub feature request for new backends (e.g. Veo, Gemini image client). When the client exists, add the catalog entry and close the issue.

Optional: put the issue URL on a disabled catalog row via `FeatureRequestUrl` so admins can see the roadmap without offering a broken picker.
