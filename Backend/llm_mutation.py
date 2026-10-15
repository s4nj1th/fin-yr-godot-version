"""
Optional LLM-guided semantic mutation. See docs/EVOLUTIONARY_DESIGN.md for
why this exists as an off-by-default, independently ablatable arm rather than
part of the core evolutionary system.

Any LLM output is untrusted: parsed defensively (genome.sanitize_params) and
clamped to the same bounds as a normal mutation before ever being used. On any
failure — disabled, no key, network error, unparseable response — this
returns None and the caller falls back to genome_lib.mutate(parent).
"""

from __future__ import annotations

import json
import re
from typing import Any, Optional

import requests

import config
import genome as genome_lib


def is_available() -> bool:
    if config.LLM_MUTATION_MODE == "off":
        return False
    if config.LLM_PROVIDER == "local":
        return True  # no API key needed; an unreachable Ollama server is handled per-call like any other failure
    return bool(config.LLM_API_KEY)


def _extract_json_object(text: str) -> Optional[dict[str, Any]]:
    text = text.strip()

    fence_match = re.search(r"```(?:json)?\s*(\{.*?\})\s*```", text, re.DOTALL)
    if fence_match:
        text = fence_match.group(1)
    else:
        start = text.find("{")
        end = text.rfind("}")
        if start == -1 or end == -1 or end <= start:
            return None
        text = text[start : end + 1]

    try:
        parsed = json.loads(text)
    except json.JSONDecodeError:
        return None

    return parsed if isinstance(parsed, dict) else None


GENE_DESCRIPTIONS = {
    "enemy_density": "share of rooms that hold a pack of enemies",
    "enemy_difficulty_scale": "multiplier on every enemy's hit points and attack",
    "enemy_tempo": "enemy speed: faster movement and shorter wind-ups and cooldowns",
    "elite_rate": "chance that an enemy is an elite (tougher, hits harder, enrages at half health)",
    "pack_size": "expected number of enemies per pack",
}
BEHAVIOR_DESCRIPTIONS = {
    "melee_chaser": "walks straight at the player and strikes",
    "melee_hit_run": "strikes, then backs off",
    "ranged_kite": "shoots and keeps its distance",
    "ranged_strafe": "shoots while side-stepping",
    "melee_charger": "telegraphed dash along a line; dodgeable, then stunned",
    "ranged_bomber": "lobs a bomb at a telegraphed 3x3 area; dodgeable",
    "melee_shield": "blocks most damage from the front; must be flanked",
}


def _enemy_genes(params: dict[str, Any], space: Optional[genome_lib.GenomeSpace] = None) -> dict[str, Any]:
    """Only the evolved genes; the fixed environment is not the LLM's to change."""
    space = space or genome_lib.get_space()
    params = genome_lib.complete_params(params)
    genes: dict[str, Any] = {key: round(params[key], 3) for key in space.scalars}
    genes["enemy_behavior_mix"] = {b: round(params["enemy_behavior_mix"].get(b, 0.0), 3) for b in space.behaviors}
    return genes


def _build_prompt(parent_params: dict[str, Any], recent_outcomes: list[dict[str, Any]],
                  space: Optional[genome_lib.GenomeSpace] = None) -> str:
    space = space or genome_lib.get_space()
    bounds_desc = "\n".join(f"  - {key} ({GENE_DESCRIPTIONS[key]}): between {lo} and {hi}"
                            for key, (lo, hi) in space.bounds.items())
    behavior_desc = "\n".join(f"  - {b}: {BEHAVIOR_DESCRIPTIONS[b]}" for b in space.behaviors)

    history = recent_outcomes[-config.LLM_MUTATION_HISTORY_SIZE :]
    if history:
        history_lines = []
        for outcome in history:
            status = "PASSED" if outcome["passed"] else f"FAILED ({outcome.get('fail_reason', 'unknown')})"
            history_lines.append(f"  - {status}: {json.dumps(_enemy_genes(outcome['params'], space))}")
        history_desc = "\n".join(history_lines)
    else:
        history_desc = "  (no prior real playthroughs yet)"

    param_keys = ", ".join(space.scalars)

    return f"""You are proposing one mutated genome for a procedural dungeon-crawler game, as part of an evolutionary algorithm. A genome describes the enemy encounter in a fixed dungeon: the numeric genes below and the mix of {len(space.behaviors)} enemy behaviours. The dungeon itself never changes. A real human player will actually play whatever genome you propose next, so make a genuinely targeted change based on the outcome history below — not a random guess.

Current (parent) genome parameters:
{json.dumps(_enemy_genes(parent_params, space), indent=2)}

Parameter bounds (stay within these):
{bounds_desc}

enemy_behavior_mix keys must be exactly these (relative weights, need not sum to 1 — they will be renormalized):
{behavior_desc}

Recent real playthrough outcomes (most recent last):
{history_desc}

Based on this history, propose ONE mutated genome that differs meaningfully from the parent in at least one parameter, in whatever direction you think is more likely to produce an interesting, passable-but-not-trivial encounter for this specific player. Respond with ONLY a single JSON object (no prose, no markdown fences) with exactly these keys: {param_keys}, enemy_behavior_mix."""


def _call_anthropic(prompt: str) -> str:
    response = requests.post(
        "https://api.anthropic.com/v1/messages",
        headers={
            "x-api-key": config.LLM_API_KEY,
            "anthropic-version": "2023-06-01",
            "content-type": "application/json",
        },
        json={
            "model": config.LLM_MODEL_NAME or "claude-haiku-4-5-20251001",
            "max_tokens": 512,
            "messages": [{"role": "user", "content": prompt}],
        },
        timeout=config.LLM_REQUEST_TIMEOUT_SECONDS,
    )
    response.raise_for_status()
    data = response.json()
    return "".join(block.get("text", "") for block in data.get("content", []))


def _call_openai_compatible(base_url: str, model: str, prompt: str, api_key: str = "") -> str:
    """Shared by OpenAI itself and any local OpenAI-compatible server (Ollama
    included — `ollama serve` exposes /v1/chat/completions in this exact shape)."""
    headers = {"content-type": "application/json"}
    if api_key:
        headers["Authorization"] = f"Bearer {api_key}"

    response = requests.post(
        f"{base_url}/v1/chat/completions",
        headers=headers,
        json={
            "model": model,
            "max_tokens": 512,
            "messages": [{"role": "user", "content": prompt}],
        },
        timeout=config.LLM_REQUEST_TIMEOUT_SECONDS,
    )
    response.raise_for_status()
    data = response.json()
    return data["choices"][0]["message"]["content"]


def _call_openai(prompt: str) -> str:
    return _call_openai_compatible(
        "https://api.openai.com", config.LLM_MODEL_NAME or "gpt-4o-mini", prompt, config.LLM_API_KEY
    )


def _call_local(prompt: str) -> str:
    """Uses Ollama's native /api/chat rather than its OpenAI-compatible endpoint:
    hybrid-reasoning models like Qwen3.5 default to a "thinking" pass that can
    eat the whole token budget before producing any actual content, and
    think=false (needed to disable that) is only honored on this endpoint."""
    response = requests.post(
        f"{config.LLM_LOCAL_BASE_URL}/api/chat",
        headers={"content-type": "application/json"},
        json={
            "model": config.LLM_MODEL_NAME or "qwen3.5:4b",
            "think": False,
            "stream": False,
            "messages": [{"role": "user", "content": prompt}],
        },
        timeout=config.LLM_REQUEST_TIMEOUT_SECONDS,
    )
    response.raise_for_status()
    data = response.json()
    return data["message"]["content"]


def propose_mutation(parent: dict[str, Any], recent_outcomes: list[dict[str, Any]],
                     space: Optional[genome_lib.GenomeSpace] = None) -> Optional[dict[str, Any]]:
    """Returns a full sanitized genome dict (same shape as genome_lib.mutate's
    output, tagged mutation_source="llm") on success, or None on any failure —
    the caller (AdaptiveEvolutionManager) is responsible for falling back to
    genome_lib.mutate(parent) and counting the failure for the ablation."""
    if not is_available():
        return None

    prompt = _build_prompt(parent["params"], recent_outcomes, space)

    try:
        if config.LLM_PROVIDER == "openai":
            raw_text = _call_openai(prompt)
        elif config.LLM_PROVIDER == "local":
            raw_text = _call_local(prompt)
        else:
            raw_text = _call_anthropic(prompt)
    except Exception as exc:  # noqa: BLE001 - an untrusted external API call must never crash the evolution loop
        print(f"[llm_mutation] LLM call failed, falling back to random mutation: {exc}")
        return None

    if not raw_text:
        return None

    parsed = _extract_json_object(raw_text)
    if parsed is None:
        print("[llm_mutation] Could not parse JSON from LLM response, falling back to random mutation.")
        return None

    sanitized_params = genome_lib.sanitize_params(parsed, parent["params"], space)

    return {
        "id": genome_lib.new_genome_id(),
        "parent_id": parent["id"],
        "generation": parent["generation"] + 1,
        "params": sanitized_params,
        "mutation_source": "llm",
    }
