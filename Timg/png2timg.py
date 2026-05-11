import sys
import struct
from pathlib import Path

import numpy as np

try:
    from PIL import Image
except ImportError:
    print("Pillow is required. Install it with: pip install Pillow")
    sys.exit(1)

TILE_ORDER = [0, 1, 4, 5, 2, 3, 6, 7, 8, 9, 12, 13, 10, 11, 14, 15]

FORMAT_NAMES = {
    0: "RGBA8", 1: "RGB8", 2: "RGBA5551", 3: "RGB565",
    4: "RGBA4", 5: "LA8", 6: "HILO8", 7: "L8",
    8: "A8", 9: "LA4", 10: "L4", 11: "A4",
    12: "ETC1", 13: "ETC1A4"
}

TfmNames = {
    0: "Rgba8", 1: "Rgb8", 2: "Rgba5551", 3: "Rgb565",
    4: "Rgba4", 5: "La8", 6: "Hilo8", 7: "L8",
    8: "A8", 9: "La4", 10: "L4", 11: "A4",
    12: "Etc1", 13: "Etc1a4"
}


def next_pow2(n):
    if n <= 0:
        return 1
    p = 1
    while p < n:
        p <<= 1
    return p


def detect_png_format(img):
    mode = img.mode
    if mode in ('L', 'LA', '1', 'P'):
        return 9
    else:
        return 0


def image_to_raw(img, fmt, padded_w, padded_h):
    if fmt == 9:
        canvas = Image.new('LA', (padded_w, padded_h), (255, 0))
        la_img = img.convert('LA')
        canvas.paste(la_img, (0, 0))
        arr = np.array(canvas, dtype=np.uint8)
        l4 = (arr[:, :, 0] >> 4).astype(np.uint8)
        a4 = (arr[:, :, 1] >> 4).astype(np.uint8)
        la4 = ((l4 << 4) | a4).flatten()
        return la4.tobytes(), 8
    elif fmt == 0:
        canvas = Image.new('RGBA', (padded_w, padded_h), (0, 0, 0, 0))
        rgba_img = img.convert('RGBA')
        canvas.paste(rgba_img, (0, 0))
        return canvas.tobytes(), 32
    else:
        raise ValueError(f"Unsupported output format: {fmt}")


def encode_tile_texture(raw_data, width, height, bpp):
    if bpp == 8:
        src = np.frombuffer(raw_data, dtype=np.uint8).reshape(height, width)
    elif bpp == 32:
        src = np.frombuffer(raw_data, dtype=np.uint8).reshape(height, width, 4)
    else:
        raise ValueError(f"Unsupported bpp: {bpp}")

    dst_idx = np.zeros(64, dtype=np.int32)
    for i in range(64):
        x = i % 8
        y = i // 8
        dst_idx[i] = TILE_ORDER[x % 4 + y % 4 * 4] + 16 * (x // 4) + 32 * (y // 4)

    if bpp == 8:
        blocks = np.lib.stride_tricks.sliding_window_view(src, (8, 8))
        blocks = blocks[::8, ::8].reshape(-1, 64)
        output = np.zeros((len(blocks), 64), dtype=np.uint8)
        output[:, dst_idx] = blocks
        return output.tobytes()
    else:
        blocks = np.lib.stride_tricks.sliding_window_view(src, (8, 8, 4))
        blocks = blocks[::8, ::8].reshape(-1, 64, 4)
        output = np.zeros((len(blocks), 64, 4), dtype=np.uint8)
        output[:, dst_idx] = blocks
        return output.tobytes()


def build_timg(filename, width, height, crop_w, crop_h, fmt, txd_data):
    header = bytearray(0xA0)

    header[0:4] = b'TIMG'
    struct.pack_into('<I', header, 0x04, 0x01)
    struct.pack_into('<I', header, 0x08, 0x00010103)
    struct.pack_into('<I', header, 0x0C, 0xA0)

    name_bytes = filename.encode('ascii')[:63]
    header[0x10:0x10 + len(name_bytes)] = name_bytes

    struct.pack_into('<I', header, 0x54, fmt)
    struct.pack_into('<H', header, 0x90, width)
    struct.pack_into('<H', header, 0x92, height)
    struct.pack_into('<H', header, 0x94, crop_w)
    struct.pack_into('<H', header, 0x96, crop_h)

    output = bytearray(header)

    tfm_name = TfmNames[fmt]
    tfm_data = tfm_name.encode('ascii')
    tfm_size = 12 + len(tfm_data)
    output.extend(b'nw4c_tfm')
    output.extend(struct.pack('<I', tfm_size))
    output.extend(tfm_data)

    txd_size = 12 + len(txd_data)
    output.extend(b'nw4c_txd')
    output.extend(struct.pack('<I', txd_size))
    output.extend(txd_data)

    output.extend(b'nw4c_gnm')
    output.extend(struct.pack('<I', 12))

    output.extend(b'nw4c_psh')
    output.extend(struct.pack('<I', 12))

    output.extend(b'nw4c_end')
    output.extend(struct.pack('<I', 12))

    return bytes(output)


def collect_png_files(paths):
    files = []
    for p in paths:
        pp = Path(p)
        if pp.is_dir():
            for f in sorted(pp.rglob('*.png')):
                files.append(f)
        elif pp.suffix.lower() == '.png':
            files.append(pp)
    return files


def main():
    args = sys.argv[1:]
    if not args:
        script_dir = Path(__file__).resolve().parent
        files = list(sorted(script_dir.rglob('*.png')))
        print(f"No arguments. Scanning {script_dir} recursively...")
    else:
        files = collect_png_files(args)

    if not files:
        print("No .png files found.")
        return

    print(f"Found {len(files)} file(s).")
    for f in files:
        try:
            img = Image.open(str(f))
            orig_w, orig_h = img.size

            padded_w = next_pow2(orig_w)
            padded_h = next_pow2(orig_h)

            fmt = detect_png_format(img)
            fmt_name = FORMAT_NAMES[fmt]

            raw_data, bpp = image_to_raw(img, fmt, padded_w, padded_h)
            tiled = encode_tile_texture(raw_data, padded_w, padded_h, bpp)

            filename = f.stem[:63]
            timg_data = build_timg(filename, padded_w, padded_h, orig_w, orig_h, fmt, tiled)

            out = f.with_suffix('.timg')
            out.write_bytes(timg_data)
            print(f"  OK  {f.name} ({orig_w}x{orig_h}) -> {out.name} [{fmt_name} {padded_w}x{padded_h}]")
        except Exception as e:
            print(f"  ERR {f.name}: {e}")


if __name__ == '__main__':
    main()