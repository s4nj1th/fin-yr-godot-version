"""
Captures the research paper's game screenshots (gameplay.png,
dungeon_overview.png and enemy_gallery.png, written to Review_2/paper/figures/
— the paper is kept with the Phase 2 review materials, outside this
repository) from the real game.

Serves one fixed encounter design on the fixed map (seed 123) from a small
stand-in for the backend, lets the bot play, saves an overhead view of the
dungeon and frames of the player's view (tools/capture_screenshots.gd), and
keeps the frame with the most enemies near the player.

    python tools/capture_screenshots.py --density 0.35 --scale 1.0 --tempo 1.0 \
        --elite 0.2 --pack 2.0 --mix 1 1 1 1 1 1 1

The enemy gallery is rendered from Scenes/EnemyGallery.tscn. The paper's two
close-up crops (gameplay_pack.png, gameplay_charge.png) were cut by hand from
the captured frames, which the script keeps.

Needs Godot 4 .NET with a display (the frames are rendered on the GPU),
Pillow and NumPy (both installed with the backend's analysis requirements),
and a free port 8001.
"""

from __future__ import annotations

import argparse
import glob
import os
import shutil
import subprocess
import sys
import tempfile

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from scan_map_seeds import ENVIRONMENT, ROOT, serve  # noqa: E402

BEHAVIORS = ["melee_chaser", "melee_hit_run", "ranged_kite", "ranged_strafe",
             "melee_charger", "ranged_bomber", "melee_shield"]
FIGURES = os.path.join(ROOT, "Review_2", "paper", "figures")


def enemies_near_player(path: str) -> int:
    """Counts saturated, enemy-coloured blobs in the centre of a frame (the
    floor, walls and fog are dark and desaturated)."""
    from scipy import ndimage

    a = np.asarray(Image.open(path).convert("HSV"))[270:1170, 580:1980].astype(int)
    mask = (a[..., 1] > 120) & (a[..., 2] > 120)
    labels, _ = ndimage.label(mask)
    count = 0
    for i, sl in enumerate(ndimage.find_objects(labels), start=1):
        area = (labels[sl] == i).sum()
        if 150 < area < 4000:
            count += 1
    return count


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--density", type=float, required=True)
    parser.add_argument("--scale", type=float, required=True)
    parser.add_argument("--tempo", type=float, default=1.0)
    parser.add_argument("--elite", type=float, default=0.0)
    parser.add_argument("--pack", type=float, default=1.0)
    parser.add_argument("--mix", type=float, nargs=7, required=True,
                        metavar=("CHASE", "HITRUN", "KITE", "STRAFE", "CHARGE", "BOMB", "SHIELD"))
    parser.add_argument("--bot-dodge", type=float, default=0.5)
    parser.add_argument("--seed", type=int, default=123)
    parser.add_argument("--frames", type=int, default=6000)
    parser.add_argument("--godot", default=os.getenv("GODOT", shutil.which("godot-mono") or shutil.which("godot") or "godot"))
    args = parser.parse_args()

    total = sum(args.mix) or 1.0
    params = {**ENVIRONMENT, "map_seed": args.seed, "enemy_density": args.density,
              "enemy_difficulty_scale": args.scale, "enemy_tempo": args.tempo, "elite_rate": args.elite,
              "pack_size": args.pack, "enemy_behavior_mix": {b: w / total for b, w in zip(BEHAVIORS, args.mix)}}
    work = tempfile.mkdtemp(prefix="screenshots-")
    server = serve(params, [])
    try:
        subprocess.run([args.godot, "--path", ROOT, "--script", os.path.join("tools", "capture_screenshots.gd"),
                        "--", f"--out={work}", f"--frames={args.frames}", "--autoplay", "--autoplay-timescale=1.0",
                        f"--bot-dodge={args.bot_dodge}"],
                       capture_output=True, text=True, timeout=3600)
    finally:
        server.shutdown()
        server.server_close()

    frames = sorted(glob.glob(os.path.join(work, "g_*.png")))
    if not frames or not os.path.exists(os.path.join(work, "overview.png")):
        print(f"No frames captured; see {work}")
        sys.exit(1)
    best = max(frames, key=enemies_near_player)
    print(f"{len(frames)} frames; best {os.path.basename(best)} with {enemies_near_player(best)} enemies near the player")

    frame = Image.open(best)
    w, h = frame.size
    frame.crop(((w - 1600) // 2, (h - 900) // 2, (w + 1600) // 2, (h + 900) // 2)).save(
        os.path.join(FIGURES, "gameplay.png"))
    overview = Image.open(os.path.join(work, "overview.png")).convert("RGB")
    background = np.asarray(overview)[5, 5]
    rows = np.where((np.abs(np.asarray(overview).astype(int) - background).sum(-1) > 30).any(1))[0]
    cols = np.where((np.abs(np.asarray(overview).astype(int) - background).sum(-1) > 30).any(0))[0]
    overview.crop((cols[0], rows[0], cols[-1] + 1, rows[-1] + 1)).resize((1600, 1600)).save(
        os.path.join(FIGURES, "dungeon_overview.png"))
    gallery = os.path.join(work, "gallery.png")
    subprocess.run([args.godot, "--path", ROOT, "res://Scenes/EnemyGallery.tscn", "--", f"--gallery-out={gallery}"],
                   capture_output=True, text=True, timeout=300)
    if os.path.exists(gallery):
        image = Image.open(gallery).convert("RGB")
        arr = np.asarray(image).astype(int)
        background = arr[5, 5]
        rows = np.where((np.abs(arr - background).sum(-1) > 30).any(1))[0]
        cols = np.where((np.abs(arr - background).sum(-1) > 30).any(0))[0]
        image.crop((cols[0], rows[0], cols[-1] + 1, rows[-1] + 1)).save(os.path.join(FIGURES, "enemy_gallery.png"))
    print(f"Wrote {FIGURES}/gameplay.png, dungeon_overview.png and enemy_gallery.png (frames kept in {work})")


if __name__ == "__main__":
    main()
