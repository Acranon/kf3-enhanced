import pickle, numpy as np, collections, sys, json, re, os
from ocr import ink, glyphs, fs
T = pickle.load(open('templates.pkl', 'rb'))
TT = sorted(T.items(), key=lambda kv: -kv[1].shape[1])
H = 13
SP = 5

def decode_cluster(g):
    # a quote mark the line band cut down to a pixel or two at its top
    ys, xs = np.nonzero(g)
    if 0 < len(ys) <= 3 and ys.max() <= 2: return ''
    for dy in (0, -1, 1, -2, 2):
        r = decode_shift(np.roll(g, dy, axis=0) if dy else g)
        if r is not None: return r
    return None

def decode_shift(g):
    out = ''; x = 0; W = g.shape[1]
    while x < W:
        if not g[:, x].any(): x += 1; continue
        best = None
        for c, t in TT:
            w = t.shape[1]
            if x + w <= W and np.array_equal(g[:, x:x + w], t):
                best = (c, w); break
        if not best: return None
        out += best[0]; x += best[1]
    return out

def fixI(s):
    # capital I and lowercase l share a shape
    s = re.sub(r"(?<![A-Za-z'])l(?=[tcnfsmdgrvTN]|\b|')", "I", s)
    s = re.sub(r"(?<![A-Za-z])l(?=[A-Z])", "I", s)
    s = re.sub(r"(\d)\. (\d)", r"\1.\2", s)
    return s

UNK = None
def decode_band(band, where=None):
    cl = glyphs(band, 0, H); s = ''; prev = None; pw = 9; bad = 0
    for (x0, x1) in cl:
        if prev is not None:
            gap = x0 - prev
            gap -= (2 if pw <= 2 else 0) + (2 if x1 - x0 <= 2 else 0)
            last = s[-1:] if s else ''
            if (gap >= SP and last != "'") or (last == "'" and gap >= 8) or (last in '.,:;!?' and gap >= 3 and not s.endswith(' ')):
                s += ' '
        d = decode_cluster(band[:, x0:x1])
        if d is None:
            d = '¤'; bad += 1
            if UNK is not None and where is not None:
                g = band[:, x0:x1]; k = (g.shape, g.tobytes()); UNK.setdefault(k, []).append(where)
        s += d; prev = x1; pw = x1 - x0
    return s, bad, len(cl)

def decode(f):
    m = ink(f)
    best = None
    for pitch in range(13, 19):
        for phase in range(pitch):
            lines = []; bad = 0; good = 0
            y = phase
            if phase > 0 and m[:phase].any(): continue
            while y < m.shape[0]:
                y1 = min(y + H, m.shape[0])
                if m[y:y1].any():
                    band = np.zeros((H, m.shape[1]), bool); band[:y1 - y] = m[y:y1]
                    s, b, n = decode_band(band); bad += b; good += n - b; lines.append(s)
                y += pitch
            score = good - 3 * bad
            if best is None or score > best[0]: best = (score, bad, lines, pitch, phase)
        if best and best[1] == 0: break
    if UNK is not None and best[1]:
        y = best[4]
        while y < m.shape[0]:
            y1 = min(y + H, m.shape[0])
            if m[y:y1].any():
                band = np.zeros((H, m.shape[1]), bool); band[:y1 - y] = m[y:y1]; decode_band(band, (f, y))
            y += best[3]
    return [fixI(l).replace('``', '"').replace('`', "'") for l in best[2]], best[1]

if __name__ == '__main__':
    lo, hi = int(sys.argv[1]), int(sys.argv[2])
    res = {}; badn = 0
    for i in range(lo, hi):
        lines, bad = decode(fs[i]); res[i] = lines; badn += bad > 0
    json.dump(res, open(f'dec2_{lo}_{hi}.json', 'w', encoding='utf-8'), indent=0, ensure_ascii=False)
    print('images with unknowns', badn)
