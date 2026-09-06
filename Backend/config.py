"""
Phase-1 tuning knobs for the human-coupled MCC backend.

Nothing in this file is load-bearing science — these are starting-point
constants the Idea.md doc explicitly calls out as needing empirical
iteration once real playtesting starts (see Idea/Idea.md section 5.3).
Adjust freely; nothing else in the codebase assumes specific values here.
"""

# --- Minimal-criterion pass/fail thresholds ---
MIN_TRIAL_SECONDS = 15.0   # faster than this -> trivially easy, fail
MAX_TRIAL_SECONDS = 240.0  # slower than this without reaching the exit -> abandoned, fail
MAX_DEATHS_ALLOWED = 3     # more deaths than this in one trial -> fail, even if the exit was reached

# --- Genome parameter bounds (used for random init + mutation clamping) ---
GENOME_BOUNDS = {
    "map_width": (80, 200),
    "map_height": (80, 200),
    "room_attempts": (150, 350),
    "trap_density": (0.0, 0.5),
    "chest_density": (0.0, 0.5),
    "heal_density": (0.0, 0.5),
    "enemy_density": (0.0, 0.5),
    "enemy_difficulty_scale": (0.6, 1.6),
}

# Behavior-mix weight keys (kept separate since they're normalized as a group, not clamped individually)
BEHAVIOR_TYPES = ["melee_chaser", "melee_hit_run", "ranged_kite", "ranged_strafe"]

# Gaussian mutation sigma per field, expressed as a fraction of that field's bound range
MUTATION_SIGMA_FRACTION = 0.15

# Probability that a given scalar gene is perturbed at all during mutation
MUTATION_RATE = 0.6

DATA_DIR = "data"
STATE_FILE = f"{DATA_DIR}/mcc_state.json"
SESSION_LOG_FILE = f"{DATA_DIR}/session_log.jsonl"
