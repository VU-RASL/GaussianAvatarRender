#!/usr/bin/env python3
"""Render a measured Quest float32 depth frame; requires only NumPy and Pillow.

Usage:
  python render_depth_snapshot.py depth.f32 metadata.json output_directory --ply

Required metadata: width, height, reprojection (16 row-major values, Unity
world -> SDK depth clip), zBufferParams (x,y,z,w), cameraPosition {x,y,z},
cameraRotation {x,y,z,w}, cameraPoseAvailable=true. Optional matrixInverse has
the same row-major layout.
Raw texture row zero is v=0. The depth-map panel flips rows for v-up display.
The global reprojection matrix is authoritative; separately polled native pose/
FOV descriptors are retained as context, never substituted for that matrix.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
from typing import Any

import numpy as np
from PIL import Image, ImageDraw, ImageFont, PngImagePlugin

VERSION = "1.0"
PALETTE = np.array(
    [[42, 52, 115], [40, 101, 146], [34, 145, 141],
     [95, 177, 117], [180, 197, 83], [246, 211, 83]], dtype=float
)


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def vector(value: Any, names: str, label: str) -> np.ndarray:
    result = np.asarray([value[n] for n in names] if isinstance(value, dict) else value, dtype=np.float64)
    if result.shape != (len(names),) or not np.isfinite(result).all():
        raise ValueError(f"{label} must contain {len(names)} finite values.")
    return result


def matrix(value: Any, label: str) -> np.ndarray:
    result = np.asarray(value, dtype=np.float64)
    if result.size != 16 or not np.isfinite(result).all():
        raise ValueError(f"{label} must contain 16 finite row-major values.")
    return result.reshape(4, 4)


def quaternion_matrix(quaternion: np.ndarray) -> np.ndarray:
    norm = float(np.linalg.norm(quaternion))
    if norm < 1e-10:
        raise ValueError("cameraRotation is a zero quaternion.")
    x, y, z, w = quaternion / norm
    return np.array([
        [1 - 2 * (y*y + z*z), 2 * (x*y - z*w), 2 * (x*z + y*w)],
        [2 * (x*y + z*w), 1 - 2 * (x*x + z*z), 2 * (y*z - x*w)],
        [2 * (x*z - y*w), 2 * (y*z + x*w), 1 - 2 * (x*x + y*y)],
    ])


def reconstruct(raw: np.ndarray, metadata: dict, near: float, far: float) -> dict:
    height, width = raw.shape
    if metadata.get("cameraPoseAvailable") is not True:
        raise ValueError("cameraPoseAvailable must be true for a camera-oriented figure.")
    reprojection = matrix(metadata["reprojection"], "reprojection")
    numerical_inverse = np.linalg.inv(reprojection)
    inverse = matrix(metadata["matrixInverse"], "matrixInverse") if "matrixInverse" in metadata else numerical_inverse
    params = vector(metadata["zBufferParams"], "xyzw", "zBufferParams")
    camera_position = vector(metadata["cameraPosition"], "xyz", "cameraPosition")
    quaternion = vector(metadata["cameraRotation"], "xyzw", "cameraRotation")
    rotation = quaternion_matrix(quaternion)
    y, x = np.indices(raw.shape, dtype=np.float64)
    ndc = np.stack((2 * (x + .5) / width - 1,
                    2 * (y + .5) / height - 1,
                    2 * raw.astype(np.float64) - 1, np.ones_like(x)), axis=-1)
    with np.errstate(divide="ignore", invalid="ignore", over="ignore"):
        homogeneous = ndc @ inverse.T
        world = homogeneous[..., :3] / homogeneous[..., 3, None]
        metric = params[0] / (2 * raw.astype(np.float64) - 1 + params[1])
    finite_raw = np.isfinite(raw)
    finite_metric = np.isfinite(metric)
    raw_in_range = finite_raw & (raw >= 0) & (raw <= 1)
    usable_metric = raw_in_range & finite_metric & (metric > 0) & (metric >= near) & (metric <= far)
    valid = usable_metric & (np.abs(homogeneous[..., 3]) > 1e-10) & np.isfinite(world).all(axis=-1)
    if not np.any(valid):
        raise ValueError("No finite reconstructed points remain within the requested metric depth range.")

    # Unity's quaternion maps local camera axes to world axes. Row vectors use R.
    local = (world - camera_position) @ rotation
    # Plot axes: camera right, camera forward, camera up (vertical in the figure).
    display_points = local[..., [0, 2, 1]]
    valid_points = world[valid]
    projected = np.column_stack((valid_points, np.ones(len(valid_points)))) @ reprojection.T
    with np.errstate(divide="ignore", invalid="ignore"):
        roundtrip = projected[:, :3] / projected[:, 3, None]
    original = ndc[..., :3][valid]
    delta = roundtrip - original
    pixel_error = np.sqrt((delta[:, 0] * width * .5)**2 + (delta[:, 1] * height * .5)**2)
    depth_error = np.abs(delta[:, 2]) * .5
    roundtrip_finite = np.isfinite(pixel_error).all() and np.isfinite(depth_error).all()
    roundtrip_ok = bool(roundtrip_finite and np.max(pixel_error) <= .1 and np.max(depth_error) <= 1e-4)
    inverse_residual = float(np.max(np.abs(reprojection @ inverse - np.eye(4))))
    report = {
        "rawPixelCount": int(raw.size),
        "finiteRawCount": int(finite_raw.sum()),
        "rawUnitIntervalCount": int(raw_in_range.sum()),
        "finiteRawOutsideUnitIntervalCount": int((finite_raw & ((raw < 0) | (raw > 1))).sum()),
        "metricDepthInRangeCount": int(usable_metric.sum()),
        "validWorldPointCount": int(valid.sum()),
        "validFraction": float(valid.mean()),
        "retainedMetricDepthMeters": {
            "minimum": float(metric[valid].min()), "median": float(np.median(metric[valid])),
            "maximum": float(metric[valid].max()),
        },
        "worldBoundsMeters": {"minimum": valid_points.min(axis=0).tolist(), "maximum": valid_points.max(axis=0).tolist()},
        "roundtrip": {
            "passed": roundtrip_ok,
            "maximumPixelError": float(pixel_error.max()) if roundtrip_finite else None,
            "rmsPixelError": float(np.sqrt(np.mean(pixel_error**2))) if roundtrip_finite else None,
            "maximumRawDepthError": float(depth_error.max()) if roundtrip_finite else None,
            "inverseResidualMaxAbs": inverse_residual,
            "inverseSource": "metadata.matrixInverse" if "matrixInverse" in metadata else "numpy.linalg.inv(reprojection)",
            "limitation": "Numerical consistency only; this does not independently validate sensor calibration or time alignment.",
        },
        "cameraQuaternionOriginalNorm": float(np.linalg.norm(quaternion)),
    }
    if not roundtrip_ok:
        raise ValueError("Reprojection roundtrip failed (tolerance 0.1 pixel / 1e-4 raw depth). Check matrix layout or inverse.")
    return {"world": world, "display": display_points, "metric": metric, "valid": valid,
            "crop": [near, far], "report": report}


def build_mesh(data: dict, step: int, jump_base: float = .12, jump_relative: float = .02) -> dict:
    height, width = data["valid"].shape
    ys = np.unique(np.r_[np.arange(0, height, step), height - 1])
    xs = np.unique(np.r_[np.arange(0, width, step), width - 1])
    yy, xx = np.meshgrid(ys, xs, indexing="ij")
    world = data["world"][yy, xx].reshape(-1, 3)
    display = data["display"][yy, xx].reshape(-1, 3)
    depth = data["metric"][yy, xx].ravel()
    valid = data["valid"][yy, xx].ravel()
    indices = np.arange(len(depth)).reshape(len(ys), len(xs))
    a, b, c, d = indices[:-1, :-1].ravel(), indices[:-1, 1:].ravel(), indices[1:, :-1].ravel(), indices[1:, 1:].ravel()
    candidates = np.concatenate((np.column_stack((a, b, c)), np.column_stack((b, d, c))))
    finite_faces = valid[candidates].all(axis=1)
    jump_ok = np.ones(len(candidates), dtype=bool)
    edge_ok = np.ones(len(candidates), dtype=bool)
    # Sanity bound on Euclidean edges, in addition to the stricter axial jump gate.
    edge_factor = max(1.0, step / 3.0)
    with np.errstate(invalid="ignore", over="ignore"):
        for i, j in ((0, 1), (1, 2), (2, 0)):
            p, q = candidates[:, i], candidates[:, j]
            shallow = np.minimum(depth[p], depth[q])
            jump_ok &= np.abs(depth[p] - depth[q]) <= jump_base + jump_relative * shallow
            edge_ok &= np.linalg.norm(world[p] - world[q], axis=1) <= (.12 + .08 * shallow) * edge_factor
    keep = finite_faces & jump_ok & edge_ok
    faces = candidates[keep]
    if not len(faces):
        raise ValueError("No surface triangles survive the validity and discontinuity gates. No surface image was invented.")
    used = np.unique(faces)
    compact = np.full(len(world), -1, dtype=np.int64)
    compact[used] = np.arange(len(used))
    report = {
        "gridStridePixels": step, "sampledGridShape": [len(ys), len(xs)],
        "candidateTriangles": int(len(candidates)), "triangles": int(len(faces)),
        "vertices": int(len(used)), "rejectedInvalidVertexTriangles": int((~finite_faces).sum()),
        "rejectedAxialJumpTriangles": int((finite_faces & ~jump_ok).sum()),
        "rejectedWorldEdgeTrianglesAfterAxialGate": int((finite_faces & jump_ok & ~edge_ok).sum()),
        "axialJumpRuleMeters": f"abs(z1-z2) <= {jump_base:g} + {jump_relative:g} * min(z1,z2)",
        "worldEdgeRuleMeters": f"edge <= (0.12 + 0.08 * min(z1,z2)) * {edge_factor:g}",
        "topology": "Only triangles between adjacent sampled texture-grid vertices; holes and rejected boundaries remain open.",
        "surfaceLimitation": "Measured samples joined locally for visualization; no hole filling, shape completion, or claim about unseen surfaces.",
    }
    return {"world": world[used], "display": display[used], "depth": depth[used],
            "faces": compact[faces], "report": report}


def colors(depth: np.ndarray, low: float, high: float) -> np.ndarray:
    t = np.clip((np.asarray(depth) - low) / max(high - low, 1e-8), 0, 1) * (len(PALETTE) - 1)
    t = np.nan_to_num(t, nan=0, posinf=len(PALETTE)-1, neginf=0)
    i = np.minimum(t.astype(int), len(PALETTE) - 2)
    fraction = t - i
    return np.clip(PALETTE[i] * (1 - fraction[..., None]) + PALETTE[i + 1] * fraction[..., None], 0, 255).astype(np.uint8)


def font(size: int, bold: bool = False):
    choices = [
        "C:/Windows/Fonts/seguisb.ttf" if bold else "C:/Windows/Fonts/segoeui.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf" if bold else "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
    ]
    for candidate in choices:
        if Path(candidate).exists():
            return ImageFont.truetype(candidate, size)
    return ImageFont.load_default(size=size)


def nice_length(value: float) -> float:
    scale = 10 ** math.floor(math.log10(max(value, 1e-6)))
    return max(v * scale for v in (1, 2, 5, 10) if v * scale <= value * 1.001)


def render_png(mesh: dict, data: dict, metadata: dict, output: Path, synthetic: bool, raw_hash: str, meta_hash: str) -> dict:
    # Supersampled Pillow rendering avoids requiring a GUI or a plotting backend.
    supersample = 2
    width, height = 2400, 1500
    canvas = Image.new("RGB", (width * supersample, height * supersample), "white")
    draw = ImageDraw.Draw(canvas)
    ink, muted, rule = (23, 42, 62), (83, 99, 115), (222, 229, 235)
    def text(x, y, value, size=28, bold=False, fill=ink):
        draw.text((round(x*supersample), round(y*supersample)), str(value),
                  font=font(round(size*supersample), bold), fill=fill)
    def line(points, fill=rule, thickness=1):
        draw.line([(round(x*supersample), round(y*supersample)) for x, y in points],
                  fill=fill, width=max(1, round(thickness*supersample)))
    title = "Synthetic validation — not a headset capture" if synthetic else "Quest environmental depth"
    text(78, 38, title, 55, True)
    text(80, 115, "Single-frame surface reconstruction  ·  Measured samples only  ·  Discontinuities kept open", 27, fill=muted)
    line([(80, 168), (2320, 168)])
    text(80, 195, "Oblique surface wireframe", 33, True)
    text(80, 243, "Equal metric scale · camera right / forward / up", 25, fill=muted)
    text(1780, 198, "Axial depth", 33, True)
    text(1780, 244, "Texture coordinates · v increases upward", 22, fill=muted)

    points = mesh["display"]
    center = (points.min(axis=0) + points.max(axis=0)) * .5
    azimuth, elevation = np.radians(-66), np.radians(23)
    eye = np.array([np.cos(elevation)*np.cos(azimuth), np.cos(elevation)*np.sin(azimuth), np.sin(elevation)])
    right = np.cross([0., 0., 1.], eye)
    right /= np.linalg.norm(right)
    up = np.cross(eye, right)
    view = np.column_stack(((points-center) @ right, (points-center) @ up))
    low_xy, high_xy = view.min(axis=0), view.max(axis=0)
    plot = (95, 310, 1690, 1260)
    scale = min((plot[2]-plot[0]) / max(high_xy[0]-low_xy[0], 1e-5),
                (plot[3]-plot[1]) / max(high_xy[1]-low_xy[1], 1e-5)) * .93
    view_center = (low_xy + high_xy) * .5
    screen_center = np.array([(plot[0]+plot[2])*.5, (plot[1]+plot[3])*.5])
    screen = (view-view_center) * np.array([scale, -scale]) + screen_center
    low = float(data["metric"][data["valid"]].min())
    high = float(data["metric"][data["valid"]].max())
    if high-low < .01:
        low, high = max(0, low-.05), high+.05
    face_depth = mesh["depth"][mesh["faces"]].mean(axis=1)
    face_colors = colors(face_depth, low, high)
    # Back-to-front, faint filled measured triangles with a data-colored wireframe.
    order = np.argsort(((points-center) @ eye)[mesh["faces"]].mean(axis=1))
    pixel_points = np.rint(screen * supersample).astype(int)
    for index in order:
        polygon = [tuple(p) for p in pixel_points[mesh["faces"][index]]]
        color = face_colors[index].astype(float)
        fill = tuple((255 * .93 + color * .07).astype(int))
        edge = tuple((255 * .35 + color * .65).astype(int))
        draw.polygon(polygon, fill=fill)
        draw.line(polygon + polygon[:1], fill=edge, width=supersample)

    # Orientation glyph is explicitly camera-relative, not a gravity estimate.
    origin = np.array([205., 1275.])
    for vector_axis, label, tint in (
        (np.array([1., 0., 0.]), "Right", (182, 70, 57)),
        (np.array([0., 1., 0.]), "Forward", (36, 126, 130)),
        (np.array([0., 0., 1.]), "Up", (52, 85, 166)),
    ):
        direction = np.array([vector_axis @ right, -(vector_axis @ up)])
        endpoint = origin + direction * 85
        line([origin, endpoint], tint, 3)
        unit = direction / max(np.linalg.norm(direction), 1e-8)
        perpendicular = np.array([-unit[1], unit[0]])
        arrow = np.array([endpoint, endpoint-unit*12+perpendicular*5, endpoint-unit*12-perpendicular*5]) * supersample
        draw.polygon([tuple(p) for p in arrow], fill=tint)
        text(endpoint[0]+8, endpoint[1]-10, label, 21, fill=tint)
    bar_meters = nice_length(240 / scale)
    bar_pixels = bar_meters * scale
    line([(1180, 1320), (1180+bar_pixels, 1320)], ink, 4)
    line([(1180, 1311), (1180, 1329)], ink, 2)
    line([(1180+bar_pixels, 1311), (1180+bar_pixels, 1329)], ink, 2)
    text(1180, 1336, f"{bar_meters:g} m", 25)

    # The displayed row flip is required because the raw texture has increasing v rows.
    depth_rgb = colors(np.where(data["valid"], data["metric"], low), low, high)
    depth_rgb[~data["valid"]] = [234, 239, 243]
    depth_image = Image.fromarray(np.flipud(depth_rgb), "RGB")
    panel_width = 540
    panel_height = min(540, round(panel_width * depth_image.height / depth_image.width))
    panel_width = min(540, round(panel_height * depth_image.width / depth_image.height))
    panel_x, panel_y = 1780+(540-panel_width)//2, 300
    canvas.paste(depth_image.resize((panel_width*supersample, panel_height*supersample), Image.Resampling.NEAREST),
                 (panel_x*supersample, panel_y*supersample))
    line([(panel_x, panel_y), (panel_x+panel_width, panel_y), (panel_x+panel_width, panel_y+panel_height),
          (panel_x, panel_y+panel_height), (panel_x, panel_y)], rule, 1)
    gradient = np.linspace(low, high, 540)[None, :]
    gradient_image = Image.fromarray(np.repeat(colors(gradient, low, high), 20, axis=0), "RGB")
    bar_y = 870
    canvas.paste(gradient_image.resize((540*supersample, 20*supersample)), (1780*supersample, bar_y*supersample))
    text(1780, bar_y+30, f"{low:.2f} m", 25, fill=muted)
    text(2220, bar_y+30, f"{high:.2f} m", 25, fill=muted)
    text(1780, 950, "Gray: invalid or out-of-range samples", 23, fill=muted)
    text(1780, 1003, f"{data['report']['validWorldPointCount']:,} valid depth samples", 25)
    text(1780, 1045, f"{len(mesh['world']):,} surface vertices", 25)
    text(1780, 1087, f"{len(mesh['faces']):,} retained triangles", 25)
    text(1780, 1137, f"Sampling grid: every {mesh['report']['gridStridePixels']} pixels", 23, fill=muted)
    text(1780, 1177, "No filled holes or inferred hidden geometry", 23, fill=muted)
    text(1780, 1221, f"Displayed depth crop: {data['crop'][0]:g}–{data['crop'][1]:g} m", 23, fill=muted)
    line([(80, 1400), (2320, 1400)])
    stamp = str(metadata.get("utc", metadata.get("captureUtc", "Timestamp not supplied")))
    text(80, 1422, stamp, 23, fill=muted)
    text(1030, 1422, "Camera-relative up; not gravity-aligned. Oblique view is orthographic.", 23, fill=muted)
    png_info = PngImagePlugin.PngInfo()
    png_info.add_text("Description", "Measured depth-grid triangles. Synthetic validation." if synthetic else
                      "Measured Quest depth-grid triangles; no inferred or completed geometry.")
    png_info.add_text("RawDepthSHA256", raw_hash)
    png_info.add_text("MetadataSHA256", meta_hash)
    png_info.add_text("SyntheticValidation", str(synthetic).lower())
    canvas.resize((width, height), Image.Resampling.LANCZOS).save(output, pnginfo=png_info, dpi=(220, 220))
    return {
        "file": str(output.resolve()), "width": width, "height": height,
        "view": "Orthographic oblique view in camera right/forward/up coordinates",
        "azimuthDegrees": -66, "elevationDegrees": 23, "equalAxisScale": True,
        "depthInset": "Raw texture rows flipped once so texture v increases upward",
        "colorRangeAxialMeters": [low, high],
    }


def write_ply(path: Path, mesh: dict, low: float, high: float) -> None:
    rgb = colors(mesh["depth"], low, high)
    with path.open("w", encoding="ascii", newline="\n") as stream:
        stream.write("ply\nformat ascii 1.0\ncomment Measured Quest depth; Unity world coordinates in meters\n")
        stream.write(f"element vertex {len(mesh['world'])}\nproperty float x\nproperty float y\nproperty float z\n")
        stream.write("property uchar red\nproperty uchar green\nproperty uchar blue\n")
        stream.write(f"element face {len(mesh['faces'])}\nproperty list uchar int vertex_indices\nend_header\n")
        for point, tint in zip(mesh["world"], rgb):
            stream.write(f"{point[0]:.9g} {point[1]:.9g} {point[2]:.9g} {tint[0]} {tint[1]} {tint[2]}\n")
        for face in mesh["faces"]:
            stream.write(f"3 {face[0]} {face[1]} {face[2]}\n")


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("raw", type=Path, help="One tightly packed float32 depth slice; little-endian by default.")
    parser.add_argument("metadata", type=Path, help="Capture metadata JSON with authoritative global reprojection.")
    parser.add_argument("output_dir", type=Path)
    parser.add_argument("--step", type=int, default=3)
    parser.add_argument("--min-depth", type=float, default=.15, help="Minimum retained axial depth, meters.")
    parser.add_argument("--max-depth", type=float, default=8, help="Maximum retained axial depth, meters.")
    parser.add_argument("--byte-order", choices=("little", "big"), default="little")
    parser.add_argument("--ply", action="store_true", help="Also export the retained measured mesh in Unity world coordinates.")
    args = parser.parse_args(argv)
    if args.step < 1 or not (math.isfinite(args.min_depth) and math.isfinite(args.max_depth)
                            and 0 <= args.min_depth < args.max_depth):
        parser.error("Require step >= 1 and finite 0 <= min-depth < max-depth.")
    metadata = json.loads(args.metadata.read_text(encoding="utf-8-sig"))
    width, height = int(metadata["width"]), int(metadata["height"])
    if width < 2 or height < 2:
        parser.error("Capture dimensions must both be at least two pixels.")
    expected_bytes = width * height * 4
    if args.raw.stat().st_size != expected_bytes:
        raise ValueError(f"Raw size mismatch: expected {expected_bytes} bytes for {width}x{height}, got {args.raw.stat().st_size}.")
    raw = np.fromfile(args.raw, dtype="<f4" if args.byte_order == "little" else ">f4").reshape(height, width)
    data = reconstruct(raw, metadata, args.min_depth, args.max_depth)
    mesh = build_mesh(data, args.step)
    args.output_dir.mkdir(parents=True, exist_ok=True)
    synthetic = bool(metadata.get("synthetic", False))
    filename = "synthetic-validation.png" if synthetic else "quest-depth-snapshot.png"
    raw_hash, meta_hash = sha256(args.raw), sha256(args.metadata)
    figure = render_png(mesh, data, metadata, args.output_dir / filename, synthetic, raw_hash, meta_hash)
    ply_path = None
    if args.ply:
        ply_path = args.output_dir / ("synthetic-validation.ply" if synthetic else "quest-depth-surface.ply")
        write_ply(ply_path, mesh, *figure["colorRangeAxialMeters"])
    report = {
        "scriptVersion": VERSION, "syntheticValidationOnly": synthetic,
        "input": {"raw": str(args.raw.resolve()), "metadata": str(args.metadata.resolve()),
                  "rawSha256": raw_hash, "metadataSha256": meta_hash,
                  "width": width, "height": height, "rawFormat": f"{args.byte_order}-endian float32",
                  "rowConvention": "raw row y corresponds to increasing texture v"},
        "filters": {"rawDepthUnitInterval": [0, 1], "strictlyPositiveFiniteAxialDepth": True,
                    "minimumAxialMeters": args.min_depth, "maximumAxialMeters": args.max_depth},
        "reconstruction": data["report"], "mesh": mesh["report"], "figure": figure,
        "captureContext": {key: metadata[key] for key in ("utc", "eye", "eyeIndex", "cameraPoseAvailable", "removeHandsRequested",
                           "trackedHandReplacementActive", "nativeFovTangents", "nativeCreatePoseLocation",
                           "nativeCreatePoseRotation", "trackingSpaceLocalToWorld") if key in metadata},
        "matrixAuthority": "metadata.reprojection; separately polled native descriptors are context only",
        "ply": str(ply_path.resolve()) if ply_path else None,
        "plyCoordinateSystem": "Unity world XYZ in meters; no axis reflection or centering applied",
    }
    (args.output_dir / "summary.json").write_text(json.dumps(report, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print(json.dumps({"png": figure["file"], "summary": str((args.output_dir / "summary.json").resolve()),
                      "triangles": mesh["report"]["triangles"], "syntheticValidationOnly": synthetic}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
