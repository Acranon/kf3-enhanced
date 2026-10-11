import struct, os, sys
from PIL import Image
def c15(v):
    return ((v&31)*255//31, ((v>>5)&31)*255//31, ((v>>10)&31)*255//31, 0 if v==0 else 255)
def parse(b,o):
    if b[o:o+4]!=b'\x10\0\0\0': return None
    fl=struct.unpack_from('<I',b,o+4)[0]
    if fl not in (0,1,2,3,8,9): return None
    p=o+8; pal=None
    if fl&8:
        ln,x,y,w,h=struct.unpack_from('<IHHHH',b,p)
        if ln<12 or w==0 or h==0 or w>256 or h>512 or ln!=12+w*h*2: return None
        pal=[struct.unpack_from('<H',b,p+12+2*i)[0] for i in range(w*h)]; p+=ln
    ln,x,y,w,h=struct.unpack_from('<IHHHH',b,p)
    if w==0 or h==0 or w>1024 or h>512 or ln!=12+w*h*2: return None
    data=b[p+12:p+ln]; bpp=fl&3
    pw={0:w*4,1:w*2,2:w,3:w*2//3}[bpp]
    im=Image.new('RGBA',(pw,h))
    px=im.load()
    for yy in range(h):
        for xx in range(pw):
            if bpp==0: v=(data[yy*w*2+xx//2]>>((xx&1)*4))&15; c=c15(pal[v]) if pal else (v*17,)*3+(255,)
            elif bpp==1: v=data[yy*w*2+xx]; c=c15(pal[v]) if pal and v<len(pal) else (v,)*3+(255,)
            else: c=c15(struct.unpack_from('<H',data,(yy*pw+xx)*2)[0])
            px[xx,yy]=c
    return im, p+ln-o, (fl,pw,h,x,y)
if __name__=='__main__':
    fn=sys.argv[1]; out=sys.argv[2]; os.makedirs(out,exist_ok=True)
    b=open(fn,'rb').read(); o=0; n=0
    lim=int(sys.argv[3]) if len(sys.argv)>3 else 100000
    while o<len(b)-20 and n<lim:
        r=parse(b,o) if b[o]==0x10 else None
        if r:
            im,ln,info=r; im.save(f'{out}/{o:08x}_{info[1]}x{info[2]}.png'); n+=1; o+=ln; o=(o+3)&~3
        else: o+=4
    print(fn,n)
