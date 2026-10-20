"""
Tuning knobs for the adaptive enemy-encounter evolution backend.

Most values here are starting-point constants that need empirical iteration
once real playtesting starts. The defaults that are not arbitrary (enemy-only
genome, fixed environment, epistemic gate, random bootstrap) are backed by
the simulation study in the research paper and explained next to each setting.
See docs/EVOLUTIONARY_DESIGN.md for the full design rationale.
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

# --- What evolves: enemy encounters only ---
# Only the enemies evolve. Evolving the map at the same time would mean the
# environment keeps changing under the search, so a failed trial could not be
# attributed to the enemies (panel review, Phase 2). These are the evolved
# scalar genes; the behaviour mix below is the grouped gene. Scripts/AI/
# EnemyArchetypes.cs applies them in the client:
#   enemy_density           share of rooms that hold a pack of enemies
#   enemy_difficulty_scale  multiplier on every enemy's hit points and attack
#   enemy_tempo             faster movement, shorter wind-ups and cooldowns
#   elite_rate              chance that an enemy is an elite variant
#   pack_size               expected enemies per pack (1 to 2.5)
# With the 114 eligible rooms of the fixed map, the most crowded design
# (density 0.5, pack size 2.5) spawns about 143 enemies, under the client's
# cap of 160.
GENOME_BOUNDS = {
    "enemy_density": (0.0, 0.5),
    "enemy_difficulty_scale": (0.6, 1.6),
    "enemy_tempo": (0.75, 1.5),
    "elite_rate": (0.0, 0.4),
    "pack_size": (1.0, 2.5),
}

# --- What stays fixed: the environment ---
# Sent with every genome so the client builds the same dungeon every trial.
# Resource densities sum to 0.45, which leaves room for an enemy share up to
# 0.55 in the client's cumulative per-room roll (trap, chest, heal, enemy),
# above the 0.5 bound of enemy_density -- so every value of enemy_density is
# expressed. (At the old default of 0.25 each, any enemy_density above 0.25
# produced the same dungeon.) map_seed fixes the layout itself; 0 means "a
# new random layout every trial". Seed 123 was chosen by playing the bot
# through candidate seeds (docs/INTEGRATION_TESTING.md): its start-to-exit
# route passes enough rooms that damage taken grows with encounter
# difficulty, and even the easiest crossing takes longer than
# MIN_TRIAL_SECONDS. On some other seeds the route is so short that every
# design is "too fast", or the enemies are never met at all.
FIXED_ENVIRONMENT = {
    "map_width": 160,
    "map_height": 160,
    "room_attempts": 280,
    "trap_density": 0.15,
    "chest_density": 0.15,
    "heal_density": 0.15,
    "map_seed": int(os.getenv("MAP_SEED", "123")),
}

# Behavior-mix weight keys (kept separate since they're normalized as a group,
# not clamped individually), in the order of Scripts/AI/EnemyArchetypes.cs.
BEHAVIOR_TYPES = ["melee_chaser", "melee_hit_run", "ranged_kite", "ranged_strafe",
                  "melee_charger", "ranged_bomber", "melee_shield"]

# The original six-gene encounter space (two scalar genes, four archetypes),
# kept as an option so the two spaces can be compared (research paper,
# scaling study). In it the new genes stay at their neutral values.
LEGACY_SCALAR_GENES = ["enemy_density", "enemy_difficulty_scale"]
LEGACY_BEHAVIOR_TYPES = ["melee_chaser", "melee_hit_run", "ranged_kite", "ranged_strafe"]
# "full" (default) or "legacy".
GENOME_SPACE = os.getenv("GENOME_SPACE", "full").lower()

# Gaussian mutation sigma per field, expressed as a fraction of that field's bound range
MUTATION_SIGMA_FRACTION = 0.15

# Probability that a given scalar gene is perturbed at all during mutation
MUTATION_RATE = 0.6

# --- Online surrogate + uncertainty-gated selection (see docs/EVOLUTIONARY_DESIGN.md) ---
SURROGATE_MIN_LABELS = 5           # don't trust/use the surrogate until at least this many real human outcomes exist
SURROGATE_UNCERTAINTY_BAND = (0.4, 0.6)  # predicted prob inside this band -> spend a real playthrough
SURROGATE_STD_THRESHOLD = 0.25     # predicted std above this -> spend a real playthrough regardless of prob
SURROGATE_MAX_PROPOSAL_ATTEMPTS = 25  # safety cap so a saturated surrogate can't loop forever without progress
# Which part of the surrogate's uncertainty the gate compares with
# SURROGATE_STD_THRESHOLD:
#   "epistemic" (default) -> uncertainty from lack of data only, which shrinks
#                            as the player plays
#   "total"               -> epistemic + fitted outcome noise (the original v1
#                            behaviour)
# Pass/fail outcomes near a player's limit are close to coin flips, so the
# fitted noise grows as the gate does its job; with "total" the gate then
# stops auto-deciding altogether. See the research paper, Sec. V-A.
SURROGATE_GATE_UNCERTAINTY = os.getenv("SURROGATE_GATE_UNCERTAINTY", "epistemic").lower()
# Length scale of the surrogate's RBF kernel: "isotropic" (one length scale
# for every gene) or "ard" (one per gene, so genes that barely matter can be
# ignored).
SURROGATE_KERNEL = os.getenv("SURROGATE_KERNEL", "isotropic").lower()
# The surrogate model:
#   "regressor" (default) -> a GP regressor on the 0/1 labels with a white-noise
#                            term; its epistemic std is the predictive std minus
#                            the fitted noise
#   "classifier"          -> GP classification (Laplace approximation): the
#                            outcome noise is the Bernoulli likelihood, and the
#                            epistemic std is the std of the pass probability
#                            under the latent posterior
SURROGATE_MODEL = os.getenv("SURROGATE_MODEL", "regressor").lower()
# Stretch fallback: a player who clears every played design gives the
# surrogate only one outcome, so it never trains and the gate never acts.
# After this many clears in a row with no failure yet, the gated condition
# serves one stretched design (an archived design with every scalar gene
# moved toward its upper bound: halfway, then 3/4, 7/8, ... of the way for
# later stretches). 0 = off.
STRETCH_AFTER_CLEARS = int(os.getenv("STRETCH_AFTER_CLEARS", "8"))
# Train the surrogate on only the most recent N real outcomes (0 = all).
# A small window lets it track a player whose skill changes mid-session.
SURROGATE_WINDOW = int(os.getenv("SURROGATE_WINDOW", "0"))
# With a windowed surrogate, also skip archive parents it now predicts the
# player would fail (only consulted once the surrogate is ready).
FILTER_PARENTS_BY_SURROGATE = os.getenv("FILTER_PARENTS_BY_SURROGATE", "0") == "1"

# --- Optional remedies (research paper, Sec. "Remedies"); both off by default ---
# Lower bound on challenge: a cleared trial in which the player took less
# than this much damage fails as "too_easy", so the archive does not fill up
# with comfortable encounters. 0 = off.
MIN_DAMAGE_TAKEN = float(os.getenv("MIN_DAMAGE_TAKEN", "0"))
# Feasibility-biased probing: an uncertain candidate is played only if the
# surrogate's predicted pass probability is at least this; otherwise it is
# skipped (neither played nor archived). 0 = off.
PLAY_THRESHOLD = float(os.getenv("PLAY_THRESHOLD", "0"))
# Per-archetype criterion (selection that sees composition). Each player's
# per-archetype damage per enemy is pooled over their played trials from the
# telemetry the game reports (enemy_behavior_breakdown). An archetype becomes
# the player's *nemesis* once, over at least ARCHETYPE_MIN_OBSERVED enemies,
# it has cost them more than ARCHETYPE_COST_RATIO times their average damage
# per enemy. A design in which nemeses make up more than NEMESIS_MAX_SHARE of
# the behaviour mix is no longer proposed, is not used as a parent, and
# leaves the archive in force. 0 = off.
# ARCHETYPE_RULE says how such a design is treated when it was played:
#   "filter" (default) -> composition is never a verdict on a trial: the trial
#                         passes or fails on survival alone, and the surrogate
#                         learns survival; composition only filters proposals,
#                         parents and the archive in force, since it is a known
#                         function of the design and the player's nemeses
#   "verdict"          -> a cleared trial fails ("archetype_overwhelming") under
#                         the nemeses known after it, and the surrogate learns
#                         that verdict (the first version of the criterion)
ARCHETYPE_COST_RATIO = float(os.getenv("ARCHETYPE_COST_RATIO", "0"))
ARCHETYPE_RULE = os.getenv("ARCHETYPE_RULE", "filter").lower()
ARCHETYPE_MIN_OBSERVED = 15
NEMESIS_MAX_SHARE = 0.15

# --- Cold start ---
# What to serve while a player's archive is still empty:
#   "random" (default) -> a fresh random design each time, until one passes
#   "default"          -> the fixed default design every time (v1 behaviour)
# The default design can be far too hard for a weak player, who would then
# be served the same encounter over and over; see the research paper, Sec. V-C.
BOOTSTRAP_STRATEGY = os.getenv("BOOTSTRAP_STRATEGY", "random").lower()

# --- Optional LLM-guided mutation (off unless explicitly configured) ---
# Ablation-critical: LLM_MUTATION_MODE must stay togglable so "LLM-guided vs
# random mutation" is a measurable comparison, not an assumed improvement.
# The core evolutionary system (archive, minimal criterion, surrogate,
# default mutation) never depends on this — see docs/EVOLUTIONARY_DESIGN.md.
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

# --- Outlier / threshold analysis (Backend/behavior_analytics.py) ---
# Domain thresholds for flagging a trial or an archetype's outcome in one
# trial as crossing a game-design-meaningful boundary — distinct from
# statistical outlier detection (behavior_analytics.find_iqr_outliers),
# which needs no fixed number, just relative spread within the data
# collected so far. Same status as every other constant in this file:
# starting points, not load-bearing science — tune once real trial volume
# exists. See docs/BEHAVIOR_ANALYTICS.md "Outliers and thresholds".
THRESHOLDS = {
    # The main dungeon caps spawns at 160 (Scripts/Core/GameManager.cs
    # MaxEnemies); a trial spawning more than 60 is a crowded/high-density
    # outlier worth a second look. 2 or fewer is the
    # opposite extreme — barely any encounter happened at all.
    "high_enemy_count": 60,
    "low_enemy_count": 2,
    # One archetype dealing more than this much damage per enemy spawned
    # (baseline+adapted combined) in a single trial is disproportionate —
    # either a genuinely overtuned archetype/difficulty combination or a
    # telemetry bug worth checking before trusting the chart it feeds.
    "high_damage_per_spawn": 40.0,
    # More than half of hits taken coming from the rear suggests an
    # archetype that's trivially exploitable once a player/bot finds the
    # blind spot, not just ordinary variance.
    "high_rear_hit_rate": 0.5,
    # Near-invincible or paper-thin are both balance red flags.
    "low_kill_rate": 0.05,
    "high_kill_rate": 0.9,
    # Mirrors MAX_DEATHS_ALLOWED above but as an early-warning threshold —
    # a trial with this many deaths still passed the minimal criterion but
    # is right at the edge of failing it.
    "near_max_deaths": MAX_DEATHS_ALLOWED - 1,
    # How much an archetype's aggregate behavior is allowed to shift
    # between situations (baseline vs. adapted — see
    # EnemyBehaviorTelemetry.cs / AdaptationDirector) before it's flagged
    # as a meaningful situational difference rather than ordinary noise.
    # 0.5 = a 50% change in either direction. This is the primary knob for
    # "how does behavior differ across situations" (behavior_analytics.
    # flag_situational_shift) — see docs/BEHAVIOR_ANALYTICS.md.
    "high_situational_shift_pct": 0.5,
}

# --- Players ---
# Each player gets their own archive and surrogate: designs evolved for one
# player do not suit another (research paper, Sec. V-E), and the scripted bot plays as
# its own player so its trials never train a human player's surrogate.
DEFAULT_PLAYER_ID = "default"
PLAYER_ID_PATTERN = r"^[A-Za-z0-9_-]{1,40}$"

# Overridable so a demo or integration run can write somewhere disposable.
DATA_DIR = os.getenv("EVOLUTION_DATA_DIR", os.path.join(_BACKEND_DIR, "data"))
PLAYERS_DIR = os.path.join(DATA_DIR, "players")
SESSION_LOG_FILE = os.path.join(DATA_DIR, "session_log.jsonl")
# v1 of the backend kept a single archive in data/mcc_state.json. It is not
# loaded: those designs were judged on maps that changed from trial to trial,
# so their pass/fail outcomes do not carry over to the fixed environment.
