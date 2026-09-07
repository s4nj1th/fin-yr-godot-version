from typing import Optional

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


class ReportOutcomeRequest(BaseModel):
    genome_id: str
    telemetry: TelemetryMetrics


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
