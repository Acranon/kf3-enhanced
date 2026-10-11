import os, json, sys
from PIL import Image
import numpy as np
D='tims_item'
fs=sorted(os.listdir(D))
def ink(f):
    a=np.array(Image.open(f'{D}/{f}').convert('RGBA')).astype(int)
    return (a[:,:,3]>0) & (a[:,:,:3].sum(2)>200)
def lines(m):
    rows=m.any(1); out=[]; y=0
    while y<len(rows):
        if rows[y]:
            y0=y
            while y<len(rows) and rows[y]: y+=1
            out.append((y0,y))
        else: y+=1
    return out
def glyphs(m,y0,y1):
    cols=m[y0:y1].any(0); out=[]; x=0
    while x<len(cols):
        if cols[x]:
            x0=x
            while x<len(cols) and cols[x]: x+=1
            out.append((x0,x))
        else: x+=1
    return out
if __name__=='__main__':
    lo,hi=int(sys.argv[1]),int(sys.argv[2])
    shapes={}; order=[]
    for f in fs[lo:hi]:
        m=ink(f)
        for (y0,y1) in lines(m):
            for (x0,x1) in glyphs(m,y0,y1):
                g=m[y0:y1,x0:x1]
                # trim vertically within glyph
                k=(g.shape, g.tobytes(), )
                if k not in shapes: shapes[k]=len(shapes); order.append((f,y0,y1,x0,x1,g))
    print('unique',len(shapes))
    # line heights
    import collections
    hs=collections.Counter()
    for f in fs[lo:hi]:
        for (y0,y1) in lines(ink(f)): hs[y1-y0]+=1
    print(hs.most_common(8))
