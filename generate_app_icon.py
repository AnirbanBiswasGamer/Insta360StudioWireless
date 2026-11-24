import math
import os
from PIL import Image, ImageDraw, ImageFilter

def create_logo(size=512):
    # Create canvas with alpha
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    # 1. Base Squircle / Rounded Rectangle
    corner_radius = int(size * 0.22)
    padding = int(size * 0.04)
    box = [padding, padding, size - padding, size - padding]

    # Gradient backdrop simulation
    base = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    base_draw = ImageDraw.Draw(base)
    base_draw.rounded_rectangle(box, radius=corner_radius, fill=(18, 19, 24, 255))

    # Outer border
    base_draw.rounded_rectangle(box, radius=corner_radius, outline=(48, 50, 60, 255), width=int(size * 0.015))
    img = Image.alpha_composite(img, base)

    # 2. Wireless Waves (Cyan / Electric Blue) in upper right
    cx, cy = size // 2, size // 2
    lens_r = int(size * 0.28)

    wave_layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    w_draw = ImageDraw.Draw(wave_layer)
    for wave_dist, wave_alpha, wave_w in [(0.38, 160, 0.018), (0.43, 100, 0.014)]:
        wr = int(size * wave_dist)
        # top right arc
        w_box = [cx - wr, cy - wr, cx + wr, cy + wr]
        w_draw.arc(w_box, start=-75, end=-15, fill=(66, 133, 244, wave_alpha), width=max(2, int(size * wave_w)))
    img = Image.alpha_composite(img, wave_layer)

    # 3. Outer Lens Housing (Dark Titanium)
    lens_layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    l_draw = ImageDraw.Draw(lens_layer)
    
    # Outer ring
    l_draw.ellipse([cx - lens_r, cy - lens_r, cx + lens_r, cy + lens_r], fill=(28, 30, 38, 255), outline=(55, 58, 70, 255), width=max(2, int(size * 0.015)))

    # 4. Signature Insta360 Yellow/Gold Accent Ring
    accent_r = int(size * 0.235)
    accent_layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    a_draw = ImageDraw.Draw(accent_layer)
    a_draw.ellipse([cx - accent_r, cy - accent_r, cx + accent_r, cy + accent_r], 
                   outline=(255, 204, 0, 255), width=max(3, int(size * 0.024)))
    lens_layer = Image.alpha_composite(lens_layer, accent_layer)

    # 5. Inner Camera Lens (Deep Glass Blue / Black)
    glass_r = int(size * 0.19)
    g_layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    g_draw = ImageDraw.Draw(g_layer)
    g_draw.ellipse([cx - glass_r, cy - glass_r, cx + glass_r, cy + glass_r], fill=(10, 16, 28, 255))

    # Aperture element inside
    core_r = int(size * 0.11)
    g_draw.ellipse([cx - core_r, cy - core_r, cx + core_r, cy + core_r], fill=(4, 8, 16, 255), outline=(30, 45, 75, 255), width=max(1, int(size * 0.01)))

    # Lens reflection highlight (White / Cyan arc)
    glint_box = [cx - int(glass_r * 0.8), cy - int(glass_r * 0.8), cx + int(glass_r * 0.8), cy + int(glass_r * 0.8)]
    g_draw.arc(glint_box, start=200, end=270, fill=(255, 255, 255, 180), width=max(2, int(size * 0.022)))

    # Smaller soft specular reflection dot
    refl_x, refl_y = cx - int(glass_r * 0.4), cy - int(glass_r * 0.4)
    refl_r = int(size * 0.025)
    g_draw.ellipse([refl_x - refl_r, refl_y - refl_r, refl_x + refl_r, refl_y + refl_r], fill=(255, 255, 255, 220))

    lens_layer = Image.alpha_composite(lens_layer, g_layer)
    img = Image.alpha_composite(img, lens_layer)

    return img

def main():
    out_dir = r"D:\Insta360\Insta360StudioWireless\Assets"
    os.makedirs(out_dir, exist_ok=True)

    logo_512 = create_logo(512)
    png_path = os.path.join(out_dir, "app_logo.png")
    logo_512.save(png_path, "PNG")
    print(f"Saved logo PNG: {png_path}")

    # Generate multi-resolution ICO
    ico_path = os.path.join(out_dir, "app.ico")
    icon_sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)]
    logo_512.save(ico_path, format="ICO", sizes=icon_sizes)
    print(f"Saved ICO: {ico_path}")

if __name__ == "__main__":
    main()
