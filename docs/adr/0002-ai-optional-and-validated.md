# ADR 0002: The local AI is optional and its output is validated

- Status: accepted
- Date: 2026-10-23

## Context

Language models produce convincing but sometimes wrong statements. A diagnostic tool that invents causes or numbers
is worse than none. Not every user can or wants to run a local model.

## Decision

- All diagnosis is done by deterministic rules. The AI only explains and ranks existing findings.
- The AI is off by default and only runs when the user clicks *Explain*.
- Requests use Ollama structured outputs (JSON schema) and `keep_alive: 0`.
- `ExplanationValidator` removes items that reference unknown findings or evidence, or contain numbers that do not
  appear in the facts (with a small rounding tolerance). One retry with feedback; otherwise the answer is rejected.
- Ollama cloud models are refused.

## Consequences

- The AI cannot add new knowledge (e.g. "this driver version is buggy"). That is intentional for v0.1.
- Small models sometimes fail validation; the UI then says so instead of showing unverified text.
