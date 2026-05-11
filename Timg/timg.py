#!/usr/bin/env python3
"""Convert Nintendo 3DS TIMG texture files to PNG format."""

import argparse
import struct
import sys
from pathlib import Path

try:
    from PIL import Image
except ImportError:
    sys.exit("Error: Pillow is required. Install with: pip install Pillow")

TILE_ORDER = [0, 1, 4, 5, 2, 3, 6, 7, 8, 9, 12, 13, 10, 11, 14, 15]

ETC1_MODIFIERS = [
    [2, 8], [5, 17], [9, 29], [13, 42],
    [18, 60], [24, 80], [33, 106], [47, 183]
]

FORMAT_NAMES = {
    0: "RGBA8", 1: "RGB8", 2: "RGBA5551", 3: "RGB565",
    4: "RGBA4", 5: "LA8", 6: "HILO8", 7: "L8",
    8: "A8", 9: "LA4", 10: "L4", 11: "A4",
    12: "ETC1", 13: "ETC1A4"
}

FORMAT_NAME_TO_NUM = {v: k for k, v in FORMAT_NAMES.items()}

TFM_FORMAT_MAP = {
    "La4": 9, "L8": 7, "A8": 8, "L4": 10, "A4": 11,
    "La8": 5, "Rgb8": 1, "Rgba8": 0, "Rgb565": 3,
    "Rgba5551": 2, "Rgba4": 4, "Etc1": 12, "Etc1a4": 13,
    "Hilo8": 6
}

BPP = {
    0: 32, 1: 24, 2: 16, 3: 16, 4: 16, 5: 16, 6: 16,
    7: 8, 8: 8, 9: 8, 10: 4, 11: 4, 12: 4, 13: 8
}


def next_pow2(n):
    if n <= 0:
        return 1
    p = 1
    while p < n:
        p <<= 1
    return p


def sign_extend(val, bits):
    if val & (1 << (bits - 1)):
        val -= 1 << bits
    return val


def clamp(val, lo=0, hi=255):
    if val < lo:
        return lo
    if val > hi:
        return hi
    return val


def tile_position(x, y):
    return TILE_ORDER[x % 4 + y % 4 * 4] + 16 * (x // 4) + 32 * (y // 4)


def parse_timg(data):
    if len(data) < 0xA0:
        raise ValueError("File too small to be a valid TIMG file")

    magic = data[0:4]
    if magic != b'TIMG':
        raise ValueError(f"Invalid magic: expected b'TIMG', got {magic!r}")

    version = struct.unpack_from('<I', data, 0x04)[0]
    flags = struct.unpack_from('<I', data, 0x08)[0]
    header_size = struct.unpack_from('<I', data, 0x0C)[0]
    filename = data[0x10:0x50].split(b'\x00')[0].decode('ascii', errors='replace')
    pixel_format = struct.unpack_from('<I', data, 0x54)[0]
    width = struct.unpack_from('<H', data, 0x90)[0]
    height = struct.unpack_from('<H', data, 0x92)[0]
    crop_width = struct.unpack_from('<H', data, 0x94)[0]
    crop_height = struct.unpack_from('<H', data, 0x96)[0]

    return {
        'version': version,
        'flags': flags,
        'header_size': header_size,
        'filename': filename,
        'pixel_format': pixel_format,
        'width': width,
        'height': height,
        'crop_width': crop_width,
        'crop_height': crop_height,
    }


def parse_chunks(data, offset):
    chunks = {}
    pos = offset
    while pos + 12 <= len(data):
        sig = data[pos:pos + 8].decode('ascii', errors='replace')
        size = struct.unpack_from('<I', data, pos + 8)[0]
        if size < 12:
            break
        chunk_data = data[pos + 12:pos + size]
        chunks[sig] = chunk_data
        pos += size
        if sig == 'nw4c_end':
            break
    return chunks


def detect_format(header, chunks):
    fmt = None

    if 'nw4c_tfm' in chunks:
        tfm_data = chunks['nw4c_tfm']
        for i in range(len(tfm_data)):
            for key, val in TFM_FORMAT_MAP.items():
                key_bytes = key.encode('ascii')
                if i + len(key_bytes) <= len(tfm_data) and tfm_data[i:i + len(key_bytes)] == key_bytes:
                    fmt = val
                    break
            if fmt is not None:
                break

    if fmt is None:
        fmt = header['pixel_format']

    if fmt is None or fmt not in FORMAT_NAMES:
        fmt = 7

    return fmt


def decode_texture(tex_data, width, height, fmt):
    padded_w = next_pow2(width)
    padded_h = next_pow2(height)
    tiles_x = padded_w // 8
    tiles_y = padded_h // 8

    img = Image.new('RGBA', (padded_w, padded_h), (0, 0, 0, 0))
    pixels = img.load()

    if fmt in (12, 13):
        decode_etc(tex_data, pixels, tiles_x, tiles_y, padded_w, padded_h, fmt)
    else:
        bpp = BPP.get(fmt, 8)
        decode_tiled(tex_data, pixels, tiles_x, tiles_y, padded_w, padded_h, fmt, bpp)

    return img


def decode_tiled(tex_data, pixels, tiles_x, tiles_y, padded_w, padded_h, fmt, bpp):
    tile_bytes = 64 * bpp // 8
    data_len = len(tex_data)

    for ty in range(tiles_y):
        for tx in range(tiles_x):
            tile_offset = (ty * tiles_x + tx) * tile_bytes

            for y in range(8):
                for x in range(8):
                    gx = tx * 8 + x
                    gy = ty * 8 + y

                    if gx >= padded_w or gy >= padded_h:
                        continue

                    pos = tile_position(x, y)

                    if fmt == 7:
                        off = tile_offset + pos
                        if off >= data_len:
                            continue
                        l = tex_data[off]
                        pixels[gx, gy] = (l, l, l, 255)

                    elif fmt == 8:
                        off = tile_offset + pos
                        if off >= data_len:
                            continue
                        a = tex_data[off]
                        pixels[gx, gy] = (255, 255, 255, a)

                    elif fmt == 9:
                        off = tile_offset + pos
                        if off >= data_len:
                            continue
                        b = tex_data[off]
                        l = (b >> 4) * 0x11
                        a = (b & 0xF) * 0x11
                        pixels[gx, gy] = (l, l, l, a)

                    elif fmt == 10:
                        byte_idx = pos // 2
                        shift = (pos & 1) * 4
                        off = tile_offset + byte_idx
                        if off >= data_len:
                            continue
                        l = ((tex_data[off] >> shift) & 0xF) * 0x11
                        pixels[gx, gy] = (l, l, l, 255)

                    elif fmt == 11:
                        byte_idx = pos // 2
                        shift = (pos & 1) * 4
                        off = tile_offset + byte_idx
                        if off >= data_len:
                            continue
                        a = ((tex_data[off] >> shift) & 0xF) * 0x11
                        pixels[gx, gy] = (255, 255, 255, a)

                    elif fmt == 3:
                        off = tile_offset + pos * 2
                        if off + 1 >= data_len:
                            continue
                        val = tex_data[off] | (tex_data[off + 1] << 8)
                        r = ((val >> 11) & 0x1F) << 3
                        g = ((val >> 5) & 0x3F) << 2
                        b = (val & 0x1F) << 3
                        pixels[gx, gy] = (r, g, b, 255)

                    elif fmt == 2:
                        off = tile_offset + pos * 2
                        if off + 1 >= data_len:
                            continue
                        val = tex_data[off] | (tex_data[off + 1] << 8)
                        a = ((val >> 15) & 1) * 255
                        r = ((val >> 10) & 0x1F) << 3
                        g = ((val >> 5) & 0x1F) << 3
                        b = (val & 0x1F) << 3
                        pixels[gx, gy] = (r, g, b, a)

                    elif fmt == 4:
                        off = tile_offset + pos * 2
                        if off + 1 >= data_len:
                            continue
                        val = tex_data[off] | (tex_data[off + 1] << 8)
                        r = ((val >> 12) & 0xF) * 0x11
                        g = ((val >> 8) & 0xF) * 0x11
                        b = ((val >> 4) & 0xF) * 0x11
                        a = (val & 0xF) * 0x11
                        pixels[gx, gy] = (r, g, b, a)

                    elif fmt == 0:
                        off = tile_offset + pos * 4
                        if off + 3 >= data_len:
                            continue
                        r = tex_data[off]
                        g = tex_data[off + 1]
                        b = tex_data[off + 2]
                        a = tex_data[off + 3]
                        pixels[gx, gy] = (r, g, b, a)

                    elif fmt == 1:
                        off = tile_offset + pos * 3
                        if off + 2 >= data_len:
                            continue
                        r = tex_data[off]
                        g = tex_data[off + 1]
                        b = tex_data[off + 2]
                        pixels[gx, gy] = (r, g, b, 255)

                    elif fmt == 5:
                        off = tile_offset + pos * 2
                        if off + 1 >= data_len:
                            continue
                        l = tex_data[off]
                        a = tex_data[off + 1]
                        pixels[gx, gy] = (l, l, l, a)

                    elif fmt == 6:
                        off = tile_offset + pos * 2
                        if off + 1 >= data_len:
                            continue
                        hi = tex_data[off]
                        lo = tex_data[off + 1]
                        pixels[gx, gy] = (lo, lo, lo, hi)


def decode_etc(tex_data, pixels, tiles_x, tiles_y, padded_w, padded_h, fmt):
    has_alpha = (fmt == 13)
    tile_bytes = 64 if has_alpha else 32
    data_len = len(tex_data)

    sub_block_offsets = [(0, 0), (0, 4), (4, 0), (4, 4)]

    for ty in range(tiles_y):
        for tx in range(tiles_x):
            tile_offset = (ty * tiles_x + tx) * tile_bytes

            for sb_idx, (sy, sx) in enumerate(sub_block_offsets):
                sb_offset = tile_offset + sb_idx * (16 if has_alpha else 8)

                if has_alpha:
                    if sb_offset + 16 > data_len:
                        continue
                    alpha_val = struct.unpack_from('<Q', tex_data, sb_offset)[0]
                    color_val = struct.unpack_from('<Q', tex_data, sb_offset + 8)[0]
                else:
                    if sb_offset + 8 > data_len:
                        continue
                    alpha_val = None
                    color_val = struct.unpack_from('<Q', tex_data, sb_offset)[0]

                block_pixels = decode_etc1_block(color_val, alpha_val)

                for y in range(4):
                    for x in range(4):
                        gx = tx * 8 + sx + x
                        gy = ty * 8 + sy + y

                        if gx >= padded_w or gy >= padded_h:
                            continue

                        pixels[gx, gy] = block_pixels[y * 4 + x]


def decode_etc1_block(color_val, alpha_val=None):
    result = [None] * 16

    diffbit = (color_val >> 33) & 1
    flipbit = (color_val >> 32) & 1

    if diffbit:
        r_base = (color_val >> 59) & 0x1F
        g_base = (color_val >> 51) & 0x1F
        b_base = (color_val >> 43) & 0x1F

        r1 = (r_base << 3) | (r_base >> 2)
        g1 = (g_base << 3) | (g_base >> 2)
        b1 = (b_base << 3) | (b_base >> 2)

        r2_base = r_base + sign_extend((color_val >> 56) & 0x7, 3)
        g2_base = g_base + sign_extend((color_val >> 48) & 0x7, 3)
        b2_base = b_base + sign_extend((color_val >> 40) & 0x7, 3)

        r2_base = max(0, min(31, r2_base))
        g2_base = max(0, min(31, g2_base))
        b2_base = max(0, min(31, b2_base))

        r2 = (r2_base << 3) | (r2_base >> 2)
        g2 = (g2_base << 3) | (g2_base >> 2)
        b2 = (b2_base << 3) | (b2_base >> 2)
    else:
        r1 = ((color_val >> 60) & 0xF) * 0x11
        g1 = ((color_val >> 52) & 0xF) * 0x11
        b1 = ((color_val >> 44) & 0xF) * 0x11
        r2 = ((color_val >> 56) & 0xF) * 0x11
        g2 = ((color_val >> 48) & 0xF) * 0x11
        b2 = ((color_val >> 40) & 0xF) * 0x11

    table1 = (color_val >> 37) & 0x7
    table2 = (color_val >> 34) & 0x7

    for y in range(4):
        for x in range(4):
            idx = x * 4 + y
            lsb = (color_val >> idx) & 1
            msb = (color_val >> (idx + 16)) & 1
            neg = msb == 1

            if (flipbit and y < 2) or (not flipbit and x < 2):
                add = ETC1_MODIFIERS[table1][lsb] * (-1 if neg else 1)
                r = clamp(r1 + add)
                g = clamp(g1 + add)
                b = clamp(b1 + add)
            else:
                add = ETC1_MODIFIERS[table2][lsb] * (-1 if neg else 1)
                r = clamp(r2 + add)
                g = clamp(g2 + add)
                b = clamp(b2 + add)

            if alpha_val is not None:
                a = ((alpha_val >> (idx * 4)) & 0xF) * 0x11
            else:
                a = 255

            result[y * 4 + x] = (r, g, b, a)

    return result


def main():
    parser = argparse.ArgumentParser(
        description='Convert Nintendo 3DS TIMG texture files to PNG')
    parser.add_argument('input', help='Input TIMG file path')
    parser.add_argument('-o', '--output', help='Output PNG path (default: input with .png)')
    parser.add_argument('-f', '--format', help='Override pixel format (e.g. L8, LA4, ETC1A4)')
    parser.add_argument('--no-crop', action='store_true',
                        help='Disable cropping to crop dimensions')
    parser.add_argument('--all-formats', action='store_true',
                        help='Try all formats and save multiple PNGs')

    args = parser.parse_args()

    input_path = Path(args.input)
    if not input_path.exists():
        sys.exit(f"Error: Input file not found: {input_path}")

    data = input_path.read_bytes()

    header = parse_timg(data)

    chunk_offset = 0xA0
    chunks = parse_chunks(data, chunk_offset)

    tex_data = chunks.get('nw4c_txd', b'')

    if args.format:
        fmt_name = args.format
        if fmt_name in FORMAT_NAME_TO_NUM:
            fmt = FORMAT_NAME_TO_NUM[fmt_name]
        else:
            try:
                fmt = int(fmt_name)
                if fmt not in FORMAT_NAMES:
                    sys.exit(f"Error: Unknown format number: {fmt}")
            except ValueError:
                sys.exit(f"Error: Unknown format name: {fmt_name}")
    else:
        fmt = detect_format(header, chunks)

    width = header['width']
    height = header['height']
    crop_w = header['crop_width']
    crop_h = header['crop_height']

    print(f"File: {header['filename']}")
    print(f"Dimensions: {width}x{height}")
    print(f"Crop: {crop_w}x{crop_h}")
    print(f"Format: {FORMAT_NAMES.get(fmt, f'Unknown({fmt})')} ({fmt})")
    print(f"Texture data size: {len(tex_data)} bytes")

    if args.all_formats:
        for try_fmt in range(14):
            try:
                img = decode_texture(tex_data, width, height, try_fmt)
                if not args.no_crop and crop_w > 0 and crop_h > 0:
                    cw = min(crop_w, img.width)
                    ch = min(crop_h, img.height)
                    img = img.crop((0, 0, cw, ch))
                out_name = f"{input_path.stem}_{FORMAT_NAMES[try_fmt]}.png"
                out_path = input_path.parent / out_name
                img.save(str(out_path))
                print(f"Saved: {out_path}")
            except Exception as e:
                print(f"Failed format {FORMAT_NAMES[try_fmt]}: {e}")
    else:
        img = decode_texture(tex_data, width, height, fmt)

        if not args.no_crop and crop_w > 0 and crop_h > 0:
            cw = min(crop_w, img.width)
            ch = min(crop_h, img.height)
            img = img.crop((0, 0, cw, ch))

        if args.output:
            out_path = Path(args.output)
        else:
            out_path = input_path.with_suffix('.png')

        img.save(str(out_path))
        print(f"Saved: {out_path}")


if __name__ == '__main__':
    main()
