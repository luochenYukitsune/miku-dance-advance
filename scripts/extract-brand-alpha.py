"""Extract this white-matted brand asset without regenerating its artwork.

Requires Pillow, NumPy and SciPy. Uses SciPy's BSD-licensed nearest-feature
transform; no GEGL/GIMP implementation is copied. The white-matte equation
C = alpha * F + (1-alpha) * white recovers anti-aliased edge colours.
This deliberately scoped method assumes saturated artwork on near-white.
"""
import argparse
import hashlib
import json
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage


def extract(source: Path, destination: Path, proof: Path):
    if source.resolve() == destination.resolve():
        raise ValueError("The reference must remain read-only")
    original_hash = hashlib.sha256(source.read_bytes()).hexdigest()
    rgb = np.asarray(Image.open(source).convert("RGB"), dtype=np.float64)
    chroma = rgb.max(axis=2) - rgb.min(axis=2)
    foreground = (chroma >= 6) & (rgb.min(axis=2) < 250)
    strong = (chroma > 80) & (rgb.min(axis=2) < 100)
    core = ndimage.binary_erosion(strong, structure=np.ones((3, 3)))
    if not core.any():
        raise ValueError("No opaque saturated artwork found")
    distance, indices = ndimage.distance_transform_edt(~core, return_indices=True)
    # Preserve very thin coloured strokes if they have no eroded interior nearby.
    core |= strong & (distance > 3)
    _, indices = ndimage.distance_transform_edt(~core, return_indices=True)
    estimated_colour = rgb[tuple(indices)]
    direction = 255 - estimated_colour
    delta = 255 - rgb
    alpha = np.sum(delta * direction, axis=2) / np.maximum(np.sum(direction**2, axis=2), 1)
    # This bound permits exact white recomposition without negative RGB channels.
    alpha = np.maximum(alpha, delta.max(axis=2) / 255)
    alpha = np.clip(alpha, 0, 1)
    alpha[core] = 1
    alpha[~foreground] = 0
    recovered = 255 - delta / np.maximum(alpha[..., None], 1e-9)
    recovered = np.clip(np.rint(recovered), 0, 255).astype(np.uint8)
    alpha8 = np.rint(alpha * 255).astype(np.uint8)
    recovered[alpha8 == 0] = 0
    result = Image.fromarray(np.dstack((recovered, alpha8)))
    destination.parent.mkdir(parents=True, exist_ok=True)
    result.save(destination)
    proof.mkdir(parents=True, exist_ok=True)
    for name, colour in (("dark", (35, 37, 41, 255)), ("white", (255, 255, 255, 255))):
        background = Image.new("RGBA", result.size, colour)
        Image.alpha_composite(background, result).convert("RGB").save(proof / f"logo-{name}.png")
    yy, xx = np.indices(alpha8.shape)
    checker = np.where(((xx // 20 + yy // 20) % 2)[..., None], 205, 245)
    checker = np.broadcast_to(checker, rgb.shape).astype(np.uint8)
    Image.alpha_composite(Image.fromarray(checker).convert("RGBA"), result).save(proof / "logo-checker.png")
    recompose = recovered.astype(float) * (alpha8[..., None] / 255) + 255 - alpha8[..., None]
    error = np.abs(recompose - rgb)
    assert hashlib.sha256(source.read_bytes()).hexdigest() == original_hash
    assert np.array_equal(recovered[core], rgb[core].astype(np.uint8))
    assert np.all(recovered[alpha8 == 0] == 0)
    report = dict(sourceSha256=original_hash, outputSha256=hashlib.sha256(destination.read_bytes()).hexdigest(),
                  size=list(result.size), transparentPixels=int((alpha8 == 0).sum()),
                  opaquePixels=int((alpha8 == 255).sum()), antialiasedPixels=int(((alpha8 > 0) & (alpha8 < 255)).sum()),
                  opaqueCorePixelsUnchanged=int(core.sum()),
                  whiteRecompositionMaxError=float(error[foreground].max()),
                  originalUnchanged=True, transparentRgbZero=True)
    (proof / "logo-alpha-verification.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path)
    parser.add_argument("proof", type=Path)
    args = parser.parse_args()
    extract(args.source, args.destination, args.proof)
