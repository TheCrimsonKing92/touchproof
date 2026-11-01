# Goal Objective

# Worker context packages carry inline text raw instead of as JSON strings, cutting about 13% of inline bytes

## Measured premise

Measured 2026-11-01 at main f1a3b449a.

Over two days, the 743 prompts in `.orchestrator/prompts` totalled 42 MB, and the Worker Context Package section was 75.5% of that. Its inline items held 24.0 MB of decoded content but took 27.6 MB as rendered, **15.2% more**. That came from 793,309 `\u00XX` escapes (for `<`, `>`, `` ` ``, `'` and `&`) plus `\r\n` and `\"` escapes, because every inline item is a JSON string literal on one line.

Code (`src/Mcg.AgentOrchestrator.Execution/Workers/WorkerContextPackageBuilder.cs`):
- `RenderArtifact` (lines 240-262) writes `INLINE FULL: identity=...; sha256=...; contract=...; encoding=utf8-json`, then a newline, then `JsonSerializer.Serialize(text)`. Bytes that are not UTF-8 use `encoding=base64`.
- `RehydrateArtifact` (lines 264-306) finds exactly one header line and decodes the single next line with `RecoverInlineBytes(payload, encoding)` (lines 142-148). It assumes the payload is one line.
- `WorkerRetryFeedbackPromptGuard.Validate` checks that the prompt contains `RenderArtifact(artifact)` exactly, so it follows whatever `RenderArtifact` emits.
- `CreateReceipt` records each section's rendered length and the package's `ModelInputTokenEstimate`.

Tests that name the encoding:
- `BrokerDeliveryTestFixture.cs:213-214` asserts `encoding=utf8-json` and calls `RecoverInlineBytes(..., "utf8-json")`;
- `WorkerDispatchTestsReviewFindingFormatRepair.cs:91` asserts a header ending `encoding=utf8-json`;
- `WorkerContextPackageTests.cs:195` round-trips `RecoverInlineBytes(encoded, "utf8-json")`;
- seven test references to `INLINE FULL` exist.

## What to build

1. **New raw encoding.** For UTF-8 text artifacts, render `INLINE FULL: identity=...; sha256=...; contract=...; encoding=utf8-raw; chars=<n>`, a newline, the text exactly as it is, a newline, then the line `END INLINE <sha256>`. The sha is the artifact's own content hash, already in the header. Keep `base64` for non-UTF-8 bytes. Keep reading `utf8-json`, for packages already rendered and persisted.
2. **Rehydration.** For `utf8-raw`, take the text between the header line and the matching `END INLINE <sha256>` line. Normalise only the line endings the renderer added. Confirm the recovered text has exactly `chars` characters and hashes to `sha256`. Otherwise throw `WorkerContextPreparationException` with a new reason `inline-raw-mismatch`. A content line that equals the end marker is impossible in practice, because it would have to contain its own hash; the hash check still guards it.
3. **Line endings.** Render the text with its own line endings unchanged. Prove that a payload containing `\r\n`, a lone `\n` and no trailing newline round-trips byte for byte.
4. **Receipts.** The section receipt's rendered length and the `ModelInputTokenEstimate` reflect the new, smaller rendering, with no new fields.
5. **Tests that change.** In the three test files above, update only the assertions that name `utf8-json` for a newly rendered UTF-8 artifact to `utf8-raw`. Keep a test that still decodes a stored `utf8-json` payload.
6. **Structure.** Put the raw encode and decode in a new type beside the builder, `WorkerContextInlinePayload`, with no new partial type.

## Acceptance criteria

1. A committed test class `WorkerContextInlinePayloadTests` covers:
   - a text with `<`, `>`, backticks, quotes, `\r\n`, lone `\n` and no trailing newline round-trips byte for byte through render and rehydrate;
   - the rendered payload contains no `\u00` escape for those characters;
   - a tampered payload (one character changed) fails with `inline-raw-mismatch`;
   - a stored `utf8-json` payload still rehydrates;
   - non-UTF-8 bytes still use `base64`;
   - for a realistic sample (one of the repository's own goal briefs), the rendered section is at least 10% smaller than the `utf8-json` rendering of the same text.

   Tester owns; Acceptance executes. TEST-VERIFIABLE.
2. The existing context-package, broker-delivery and retry-feedback guard test classes pass, with only the `utf8-json` assertions for newly rendered artifacts changed to `utf8-raw`. That covers WorkerContextPackageTests, BrokerDeliveryTestFixture users, WorkerDispatchTestsReviewFindingFormatRepair and WorkerDispatchTestsSubscriptionPreflight. The Developer reports `tests: deferred - ` followed, directly after the hyphen and comma-separated, by every test class it touched or added. Developer owns; Acceptance executes. TEST-VERIFIABLE.
3. The Reviewer confirms from the diff that:
   - `utf8-json` is still accepted on read;
   - rehydration verifies both the character count and the hash;
   - `WorkerRetryFeedbackPromptGuard` needed no logic change;
   - only encoding-name assertions changed in existing tests;
   - no new partial type was added.

   Reviewer owns; Reviewer executes. TEST-VERIFIABLE.

## Scope

In scope:
- `src/Mcg.AgentOrchestrator.Execution/Workers/WorkerContextPackageBuilder.cs` and the new `WorkerContextInlinePayload.cs`
- the three test files named above (encoding-name assertions only)
- new tests under `tests/`

The Developer writes the tests and lists them in `tests: deferred - `. The conductor (Tester evidence and the gate) runs all tests. The Developer runs only the worker build check. The Reviewer judges from the diff. The Tester owns criterion 1, Acceptance runs criteria 1-2, and the Reviewer owns criterion 3.

Out of scope:
- which artifacts are inline versus on demand
- retry feedback content
- review-finding history projection

Goal id: fd1d4f721a104977b278920dc679da81
Goal status: Active