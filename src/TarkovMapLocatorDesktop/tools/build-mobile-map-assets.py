#!/usr/bin/env python3
"""Build adaptive WebP map assets for the phone/tablet map client."""

from pathlib import Path
from PIL import Image


PROJECT = Path(__file__).resolve().parents[1]
SOURCE = PROJECT / "assets" / "maps" / "web"
TARGET = PROJECT / "assets" / "maps" / "mobile-web"
TIERS = (2048, 3072)


def build_variant(source: Path, target: Path, maximum_dimension: int) -> None:
    if target.exists() and target.stat().st_mtime_ns >= source.stat().st_mtime_ns:
        return
    target.parent.mkdir(parents=True, exist_ok=True)
    with Image.open(source) as image:
        image.load()
        width, height = image.size
        scale = min(1.0, maximum_dimension / max(width, height))
        if scale < 1.0:
            image = image.resize(
                (max(1, round(width * scale)), max(1, round(height * scale))),
                Image.Resampling.LANCZOS,
            )
        image.save(target, "WEBP", quality=88, method=6)


def main() -> None:
    sources = sorted(SOURCE.rglob("*.png"))
    for tier in TIERS:
        for source in sources:
            relative = source.relative_to(SOURCE).with_suffix(".webp")
            build_variant(source, TARGET / str(tier) / relative, tier)
    print(f"Built {len(sources)} maps in {len(TIERS)} tiers under {TARGET}")


if __name__ == "__main__":
    main()
