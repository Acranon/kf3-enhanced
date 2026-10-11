from ocr import *
PITCH=15; H=13
def bands(m):
    n=(m.shape[0]+PITCH-1)//PITCH
    return [(k*PITCH, min(k*PITCH+H, m.shape[0])) for k in range(n) if m[k*PITCH:min(k*PITCH+H,m.shape[0])].any()]
def cells(m):
    out=[]
    for (y0,y1) in bands(m):
        band=np.zeros((H,m.shape[1]),bool); band[:y1-y0]=m[y0:y1]
        for (x0,x1) in glyphs(band,0,H):
            out.append((y0,x0,x1,band[:,x0:x1]))
    return out
if __name__=='__main__':
    shapes={}; ex={}
    for i,f in enumerate(fs[186:367]):
        for (y0,x0,x1,g) in cells(ink(f)):
            k=(g.shape,g.tobytes())
            shapes[k]=shapes.get(k,0)+1
            ex.setdefault(k,(f,y0,x0,x1))
    print('unique',len(shapes))
    ks=sorted(shapes,key=lambda k:-shapes[k])
    # sheet of unique glyphs, 3x scale, numbered
    from PIL import ImageDraw
    cols=12; cw=60; ch=60
    sheet=Image.new('RGB',(cols*cw,((len(ks)+cols-1)//cols)*ch),(30,30,40)); dr=ImageDraw.Draw(sheet)
    for n,k in enumerate(ks):
        g=np.frombuffer(k[1],dtype=bool).reshape(k[0])
        im=Image.fromarray((g*255).astype(np.uint8)).resize((g.shape[1]*3,g.shape[0]*3),Image.NEAREST)
        x=(n%cols)*cw; y=(n//cols)*ch
        sheet.paste(im,(x+18,y+4)); dr.text((x+1,y+1),str(n),fill=(255,255,0))
    sheet.save('glyphs.png'); print(sheet.size)
    import pickle; pickle.dump(ks,open('glyphkeys.pkl','wb'))
