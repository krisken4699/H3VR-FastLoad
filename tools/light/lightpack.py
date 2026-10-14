"""Prototype: a light copy of a part pack. Same file, same objects, every block-compressed or plain texture
without its biggest mips. No pixels are re-encoded: the smaller mips are already in the data, the big ones are cut off."""
import sys, os, time
import UnityPy

src, dst, drop = sys.argv[1], sys.argv[2], int(sys.argv[3])
t0 = time.time()
env = UnityPy.load(src)
BLOCK = {10: 8, 12: 16}      # DXT1, DXT5: bytes per 4x4 block
PLAIN = {3: 3, 4: 4, 5: 4}   # RGB24, RGBA32, ARGB32: bytes per pixel


def mip_size(fmt, w, h):
    if fmt in BLOCK:
        return max(1, (w + 3) // 4) * max(1, (h + 3) // 4) * BLOCK[fmt]
    return w * h * PLAIN[fmt]


cut = kept = skipped = 0
before = after = 0
for obj in env.objects:
    if obj.type.name != "Texture2D":
        continue
    d = obj.read()
    fmt = int(d.m_TextureFormat)
    data = bytes(d.image_data)
    before += len(data)
    w, h, mips = d.m_Width, d.m_Height, d.m_MipCount
    n = min(drop, mips - 1)
    if (fmt not in BLOCK and fmt not in PLAIN) or n <= 0 or getattr(d, "m_ImageCount", 1) != 1:
        skipped += 1
        after += len(data)
        continue
    sizes = []
    mw, mh = w, h
    for i in range(mips):
        sizes.append(mip_size(fmt, mw, mh))
        mw, mh = max(1, mw // 2), max(1, mh // 2)
    if sum(sizes) != len(data):
        skipped += 1
        after += len(data)
        continue
    # keep a multiple of 4 on block formats
    while n > 0 and fmt in BLOCK and (max(1, w >> n) % 4 or max(1, h >> n) % 4):
        n -= 1
    if n <= 0:
        kept += 1
        after += len(data)
        continue
    rest = data[sum(sizes[:n]):]
    d.m_Width, d.m_Height, d.m_MipCount = max(1, w >> n), max(1, h >> n), mips - n
    d.m_CompleteImageSize = len(rest)
    d.image_data = rest
    d.save()
    cut += 1
    after += len(rest)

os.makedirs(os.path.dirname(dst), exist_ok=True)
with open(dst, "wb") as f:
    f.write(env.file.save(packer="lz4"))
print("cut", cut, "left as they are", kept, "not touched (crunched / other)", skipped)
print("pixel data MB", round(before / 1048576), "->", round(after / 1048576))
print("file MB", round(os.path.getsize(src) / 1048576), "->", round(os.path.getsize(dst) / 1048576), "in", round(time.time() - t0, 1), "s")
