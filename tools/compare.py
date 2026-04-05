#!/usr/bin/env python3
"""
Compare two PNG screenshots pixel by pixel.

Step 1: Save a screenshot from Godot (press F12 in game → saves to /tmp/godot_capture_*.png)
Step 2: Save a screenshot from Flash (manually crop to 742x432 game area)
Step 3: python3 tools/compare.py /tmp/godot.png /tmp/flash.png

Or auto-crop mode — give it two full screenshots and it will template-match to align:
  python3 tools/compare.py screenshot_godot.png screenshot_flash.png --align
"""

import sys
import numpy as np
from PIL import Image
from math import log10


def template_match(haystack, needle):
    hh, hw = haystack.shape[:2]
    nh, nw = needle.shape[:2]
    best_score, best_x, best_y = float('inf'), 0, 0
    hay_f = haystack.astype(np.float32)
    nee_f = needle.astype(np.float32)

    for y in range(0, hh - nh, 4):
        for x in range(0, hw - nw, 4):
            score = np.mean(np.abs(hay_f[y:y+nh, x:x+nw] - nee_f))
            if score < best_score:
                best_score, best_x, best_y = score, x, y

    for y in range(max(0, best_y-6), min(hh-nh, best_y+7)):
        for x in range(max(0, best_x-6), min(hw-nw, best_x+7)):
            score = np.mean(np.abs(hay_f[y:y+nh, x:x+nw] - nee_f))
            if score < best_score:
                best_score, best_x, best_y = score, x, y

    return best_x, best_y, best_score


def analyze(img1, img2, output_path):
    h, w = img1.shape[:2]
    total = w * h
    diff = np.abs(img1.astype(float) - img2.astype(float))
    per_pixel = np.max(diff, axis=2)
    mean_diff = float(np.mean(diff))
    mse = float(np.mean(diff**2))
    psnr = 10 * log10(255**2 / mse) if mse > 0 else float('inf')
    similarity = 1.0 - (mean_diff / 255.0)

    print(f"\n{'='*55}")
    print(f"  RESULTS ({w}x{h})")
    print(f"{'='*55}")
    for t in [0, 1, 2, 3, 5, 10, 15, 20, 30]:
        c = int(np.sum(per_pixel <= t))
        bar = "█" * int(c / total * 40)
        print(f"    ±{t:2d}: {c/total*100:6.2f}%  {bar}")
    print(f"\n  Mean diff:   {mean_diff:.2f}/255 ({mean_diff/255*100:.2f}%)")
    print(f"  PSNR:        {psnr:.1f} dB")
    print(f"  Identical:   {np.sum(per_pixel==0)/total*100:.1f}%")
    print(f"\n  ★ SIMILARITY: {similarity*100:.1f}%")
    print(f"{'='*55}")

    diff_vis = (per_pixel * 4).clip(0, 255).astype(np.uint8)
    diff_rgb = np.zeros((h, w, 3), dtype=np.uint8)
    diff_rgb[:,:,0] = diff_vis
    sep = np.ones((h, 2, 3), dtype=np.uint8) * 128
    combined = np.concatenate([img1, sep, img2, sep, diff_rgb], axis=1)
    Image.fromarray(combined).save(output_path)
    print(f"\n  Saved: {output_path}")
    print(f"  Layout: Image1 | Image2 | Diff")


def main():
    if len(sys.argv) < 3:
        print("Usage: python3 compare.py <image1.png> <image2.png> [--align]")
        print("\nTo get Godot screenshot: press F12 in the game")
        print("To get Flash screenshot: take a screenshot and crop to the game area")
        sys.exit(1)

    img1 = np.array(Image.open(sys.argv[1]))[:,:,:3]
    img2 = np.array(Image.open(sys.argv[2]))[:,:,:3]
    do_align = "--align" in sys.argv

    print(f"  Image 1: {sys.argv[1]} ({img1.shape[1]}x{img1.shape[0]})")
    print(f"  Image 2: {sys.argv[2]} ({img2.shape[1]}x{img2.shape[0]})")

    if do_align:
        # Use smaller image as needle, larger as haystack
        if img1.shape[0] * img1.shape[1] < img2.shape[0] * img2.shape[1]:
            needle_img, haystack_img = img1, img2
            needle_name, haystack_name = sys.argv[1], sys.argv[2]
        else:
            needle_img, haystack_img = img2, img1
            needle_name, haystack_name = sys.argv[2], sys.argv[1]

        # Take center 100x100 patch from needle
        nh, nw = needle_img.shape[:2]
        ps = min(100, nh//3, nw//3)
        py, px = nh//2, nw//2
        patch = needle_img[py:py+ps, px:px+ps]

        print(f"\n  Aligning: patch {ps}x{ps} from {needle_name} center")
        print(f"  Searching in {haystack_name}...")
        mx, my, score = template_match(haystack_img, patch)
        print(f"  Match at ({mx},{my}) score={score:.1f}")

        # Compute crop: needle is fully used, haystack is cropped to match
        # needle(px,py) = haystack(mx,my)
        hx0 = mx - px
        hy0 = my - py
        crop_w = min(nw, haystack_img.shape[1] - max(0, hx0))
        crop_h = min(nh, haystack_img.shape[0] - max(0, hy0))

        nx0 = max(0, -hx0)
        ny0 = max(0, -hy0)
        hx0 = max(0, hx0)
        hy0 = max(0, hy0)
        crop_w = min(crop_w, nw - nx0)
        crop_h = min(crop_h, nh - ny0)

        img1 = needle_img[ny0:ny0+crop_h, nx0:nx0+crop_w]
        img2 = haystack_img[hy0:hy0+crop_h, hx0:hx0+crop_w]
    else:
        # Just crop to same size
        h = min(img1.shape[0], img2.shape[0])
        w = min(img1.shape[1], img2.shape[1])
        img1 = img1[:h, :w]
        img2 = img2[:h, :w]

    analyze(img1, img2, "/tmp/compare_result.png")


if __name__ == "__main__":
    main()
