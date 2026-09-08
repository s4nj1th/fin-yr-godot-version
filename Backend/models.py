from typing import Literal, Optional

from pydantic import BaseModel, Field


class TelemetryMetrics(BaseModel):
    time_taken_sec: float = 0.0
    damage_taken: int = 0
    health_remaining: int = 0
    deaths_retries: int = 0
    enemies_killed: int = 0
    total_enemies: int = 0
    chests_opened: int = 0
    total_chests: int = 0
    traps_triggered: int = 0
    exploration_percent: float = 0.0
    exit_reached: bool = False
    quit_early: bool = False
    # Per-archetype, per-situation combat counters for this trial. Shape:
    # {behavior_type: {"baseline": {counters}, "adapted": {counters}}},
    # where behavior_type is melee_chaser/melee_hit_run/ranged_kite/
    # ranged_strafe, "adapted" means the specific enemy involved carried an
    # active AdaptationDirector tactical order, and counters is a flat dict
    # (spawned, attacks_on_player, damage_dealt_to_player,
    # hits_taken_from_player, damage_taken_from_player, rear_hits_taken,
    # killed_by_player). Populated by Scripts/AI/EnemyBehaviorTelemetry.cs,
    # attributed per-enemy at the moment of each combat event — not
    # approximated after the fact. Defaults to {} so older clients (and
    # every pre-existing session_log.jsonl entry) stay valid — this is
    # additive, not a breaking schema change. See docs/BEHAVIOR_ANALYTICS.md.
    enemy_behavior_breakdown: dict[str, dict[str, dict[str, int]]] = Field(default_factory=dict)


class ReportOutcomeRequest(BaseModel):
    genome_id: str
    telemetry: TelemetryMetrics
    # "human" (default, and every trial ever reported before this field
    # existed) or "bot" — the optional scripted evaluator described in
    # docs/EVOLUTIONARY_DESIGN.md "Scripted bot evaluator". Bot trials go
    # through the exact same minimal-criterion/Hall-of-Fame/surrogate path
    # as human ones; this is purely a label so they stay filterable
    # (MCCManager.stats, behavior_analytics.py) rather than silently
    # blending into "human" counts.
    evaluator_type: Literal["human", "bot"] = "human"


class GenomeParams(BaseModel):
    map_width: int
    map_height: int
    room_attempts: int
    trap_density: float
    chest_density: float
    heal_density: float
    enemy_density: float
    enemy_behavior_mix: dict[str, float]
    enemy_difficulty_scale: float


class Genome(BaseModel):
    id: str
    parent_id: Optional[str] = None
    generation: int = 0
    params: GenomeParams


class SurrogateEstimate(BaseModel):
    prob: float
    std: float


class NextCandidateResponse(BaseModel):
    genome_id: str = Field(..., description="Echoes genome.id, kept top-level for convenience")
    genome: Genome
    surrogate_estimate: Optional[SurrogateEstimate] = Field(
        None, description="Present once the surrogate has enough labels to have been consulted for this candidate"
    )


class ReportOutcomeResponse(BaseModel):
    passed: bool
    hall_of_fame_size: int
    fail_reason: Optional[str] = None
