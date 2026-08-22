# Roadmap / long-term ideas

Not commitments, not scheduled — a catalogue of things worth doing eventually so they don't get
lost. Move an item out of here and into an actual commit/PR when it's picked up.

## LLM connectivity

- **Native per-provider API support**, instead of always speaking OpenAI's Chat Completions
  format through whatever compatibility layer a provider offers. Real motivating case: Gemini's
  OpenAI-compatible endpoint hard-rejects unknown fields (broke our `reasoning` field) and hides
  "thinking" token cost with no way to disable it through that layer — talking to Gemini's actual
  native API might expose real controls the compatibility shim doesn't. Bigger lift: a
  protocol-specific request/response builder per provider (Gemini `generateContent`, Anthropic
  Messages, Ollama native, etc.) instead of one shared code path. Not urgent — the current
  provider-allowlist workaround (`LLMClient.BaseUrlSupportsReasoningField`) already neutralizes the
  one concrete problem this caused. Worth doing if more compatibility-layer quirks turn up, or if
  reliable control over Gemini's thinking-token budget becomes important.
- **Smarter rate-limit handling**: honor a provider's actual `Retry-After` header on 429/5xx
  responses instead of always using the fixed 3s/6s/9s backoff regardless of what the server says.
- **Model-cycling on rate-limit**: if the configured model gets rate-limited, automatically fall
  back to the next model in a player-ranked list rather than just failing/retrying the same model.
  OpenRouter's own "model fallbacks" feature (send a `models` array instead of one `model`) may
  cover this cheaply when the player's on OpenRouter; a fully general cross-provider version is a
  bigger build.
- **Free-tier model research for players**: Gemini Flash is the current top pick for a genuinely
  free (no payment) option to point new players at — real OpenAI-compatible endpoint, generous
  daily volume, no credit card, trusted brand. Not yet the shipped default; needs real Firefly
  prompts (especially the larger JSON-array-producing calls like World Seed) run against it before
  that's safe to flip. Groq and NVIDIA NIM are secondary candidates. See project history for the
  full provider survey.

## Cost / pacing control

- **Tiered event-frequency presets**, XML-driven: tag each LLM-triggering event by importance tier
  (essential/significant/routine/ambient-ish), let a player-facing preset (e.g. Lite/Standard/
  Frequent) apply a tier-wide multiplier to how often that tier actually triggers a call. One clean
  dial for total LLM call volume, no code changes to retune. (Idea from scouting PawnDiary's
  `DiaryFrequencyPresetDefs.xml` pattern.)

## Quality / process

- **A small automated-testing project** (`Firefly.PureTests` or similar) covering logic that
  doesn't need RimWorld running at all — JSON response parsing/repair (`JsonResponseReader`),
  retry/backoff timing math, Event Decider response parsing, thread staleness/revision
  calculations. Firefly has zero automated tests today; start with one small project, not a big
  testing framework.
- **Client-side diagnostic report button**: a "copy diagnostic info" action that scrubs secrets/
  names and fingerprints recent errors for the player to paste when reporting a bug — without
  standing up any actual backend/telemetry service. (Idea from scouting PawnDiary's local
  diagnostics code, explicitly minus their hosted error-reporting server.)
- **One committed engineering guide** capturing conventions currently living only in Claude's
  private memory (build workflow, Codex collaboration pattern, naming conventions, etc.) — Firefly
  already has a minimal `AGENTS.md` (output style only), this would be a fuller one.

## Architecture

- **Colony / Event Decider / World module split** — still an open design conversation, agreed to
  build the Event Decider feature first and let real module boundaries reveal themselves before
  splitting, rather than guessing boundaries upfront.
- **Event Decider next steps**: second incident-family adapter that exercises an actual cross-type
  swap (not just same-type parameter nudging like the disease adapter), then migrate the existing
  raid narrative intercept onto the new generalized Event Decider pattern last, once proven
  elsewhere.

## Explicitly parked, not forgotten

- **"Fillion-as-full-director"** (LLM owns event timing/selection outright, replacing the
  storyteller entirely) — reverses the deliberate 2026-08-08 pivot to being a layer on top of the
  player's chosen vanilla storyteller, doesn't solve RimWar-style mods that bypass the storyteller
  pipeline entirely, and is a huge systems undertaking. Not the current direction.
