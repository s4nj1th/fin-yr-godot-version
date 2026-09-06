"""
Phase-1 tuning knobs for the human-coupled MCC backend.

Nothing in this file is load-bearing science — these are starting-point
constants the Idea.md doc explicitly calls out as needing empirical
iteration once real playtesting starts (see Idea/Idea.md section 5.3).
Adjust freely; nothing else in the codebase assumes specific values here.
"""

import os

from dotenv import load_dotenv

# Load Backend/.env if present, then fall back to the repo-root .env (shared
# with the Godot client's LLM_SERVER_URL/.env.example convention) for
# anything not already set. Both are optional — everything works without them.
_BACKEND_DIR = os.path.dirname(os.path.abspath(__file__))
load_dotenv(os.path.join(_BACKEND_DIR, ".env"))
load_dotenv(os.path.join(_BACKEND_DIR, "..", ".env"))

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

# --- Phase 2: online surrogate + uncertainty-gated selection (Idea.md 5.4/5.5) ---
SURROGATE_MIN_LABELS = 5           # don't trust/use the surrogate until at least this many real human outcomes exist
SURROGATE_UNCERTAINTY_BAND = (0.4, 0.6)  # predicted prob inside this band -> spend a real playthrough
SURROGATE_STD_THRESHOLD = 0.25     # predicted std above this -> spend a real playthrough regardless of prob
SURROGATE_MAX_PROPOSAL_ATTEMPTS = 25  # safety cap so a saturated surrogate can't loop forever without progress

# --- Phase 3: LLM-guided semantic mutation (Idea.md 5.7) ---
# Ablation-critical: LLM_MUTATION_MODE must stay togglable so "LLM-guided vs
# random mutation" is a measurable comparison, not an assumed improvement.
#   "auto" (default) -> use the LLM if an API key is configured, else random
#   "on"              -> always attempt the LLM; falls back to random per-call on failure
#   "off"             -> never use the LLM, even if a key is configured (the ablation control)
LLM_MUTATION_MODE = os.getenv("LLM_MUTATION_MODE", "auto").lower()
LLM_PROVIDER = os.getenv("LLM_PROVIDER", "anthropic").lower()  # "anthropic" | "openai" | "local"
LLM_MODEL_NAME = os.getenv("LLM_MODEL_NAME", "")  # blank -> provider-specific default below
LLM_API_KEY = os.getenv("LLM_API_KEY", "")
if LLM_API_KEY == "your_api_key_here":  # the literal placeholder from .env.example — treat as "not set"
    LLM_API_KEY = ""
# "local" -> an Ollama (or any OpenAI-compatible) server, no API key needed.
LLM_LOCAL_BASE_URL = os.getenv("LLM_LOCAL_BASE_URL", "http://localhost:11434")
LLM_MUTATION_HISTORY_SIZE = 10  # how many recent outcomes to show the LLM as context
LLM_REQUEST_TIMEOUT_SECONDS = 30  # local inference on a laptop GPU is slower than a hosted API

DATA_DIR = "data"
STATE_FILE = f"{DATA_DIR}/mcc_state.json"
SESSION_LOG_FILE = f"{DATA_DIR}/session_log.jsonl"
