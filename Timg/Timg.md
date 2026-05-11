## TIMG 格式研究

### 固定头（0xA0 bytes）
```
+0x00: magic 'TIMG' (4B)
+0x04: version (u32)
+0x08: flags (u32)
+0x0C: header_size (u32)
+0x10: filename (64B ASCII, null-padded)
+0x54: pixel_format (u32)
+0x90: width (u16)
+0x92: height (u16)
+0x94: crop_width (u16)
+0x96: crop_height (u16)
```

### NW4C Chunks（0xA0 之后）
- `nw4c_tfm` — 纹理格式名（如 "La4", "L8", "Etc1"）
- `nw4c_txd` — 瓦片排列的像素数据
- `nw4c_gnm` — 字形映射表（可能为空）
- `nw4c_psh` — 调色板着色器
- `nw4c_end` — 结束标记

### 瓦片排列
3DS 使用 8×8 瓦片的 Morton/Z-order 排列。`TILE_ORDER` 表定义了 tile 内64像素的重排顺序。纹理宽度/高度必须对齐到8的幂次方次方。