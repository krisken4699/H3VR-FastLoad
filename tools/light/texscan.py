import sys, collections
import UnityPy

path = sys.argv[1]
env = UnityPy.load(path)
print("file", path)
for name, f in env.files.items():
    sig = getattr(f, "signature", "?")
    ver = getattr(f, "version_engine", "?")
    flags = getattr(f, "dataflags", getattr(f, "_data_flags", "?"))
    print("container", type(f).__name__, sig, ver, "flags", flags)
    try:
        for n, sub in f.files.items():
            size = getattr(sub, "Length", None)
            print("   inner", n, type(sub).__name__)
    except Exception as e:
        print("   inner list failed", e)

by_fmt = collections.Counter()
bytes_fmt = collections.Counter()
total = 0
light = 0
n = 0
stream = 0
readable = 0
biggest = []
for obj in env.objects:
    if obj.type.name != "Texture2D":
        continue
    d = obj.read()
    n += 1
    fmt = str(d.m_TextureFormat).split(".")[-1]
    w, h, mips = d.m_Width, d.m_Height, d.m_MipCount
    size = d.m_CompleteImageSize if hasattr(d, "m_CompleteImageSize") else 0
    sd = getattr(d, "m_StreamData", None)
    if sd is not None and getattr(sd, "path", ""):
        stream += 1
        size = size or sd.size
    if getattr(d, "m_IsReadable", False):
        readable += 1
    by_fmt[fmt] += 1
    bytes_fmt[fmt] += size
    total += size
    # what stays when the three biggest mips go (each mip is a quarter of the one above)
    drop = min(3, max(0, mips - 1))
    light += size / (4 ** drop) if mips > 1 else size
    biggest.append((size, d.m_Name, w, h, mips, fmt))
biggest.sort(reverse=True)
print("textures", n, "total MB", round(total / 1048576), "in separate stream", stream, "readable", readable)
for fmt, c in by_fmt.most_common():
    print("  ", fmt, c, "textures", round(bytes_fmt[fmt] / 1048576), "MB")
print("with the 3 biggest mips dropped: MB", round(light / 1048576))
for b in biggest[:6]:
    print("  big:", round(b[0] / 1048576, 1), "MB", b[1], b[2], "x", b[3], "mips", b[4], b[5])
single = [b for b in biggest if b[4] <= 1]
print("textures without mips:", len(single), "MB", round(sum(b[0] for b in single) / 1048576))
