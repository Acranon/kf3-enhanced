import pickle, numpy as np, json, collections
from ocr2 import *
ks=pickle.load(open('glyphkeys.pkl','rb'))
LAB={0:'e',1:'i',2:'r',3:'a',4:'s',5:'l',6:'h',7:'o',8:'t',9:'n',10:'d',11:'c',12:'f',13:'p',14:'.',15:'m',16:'y',17:'u',18:'w',19:'T',20:'b',21:'st',22:'S',23:'v',24:'g',25:'1',26:'kg',27:'ow',28:'A',29:'ng',30:'nt',31:'ag',32:'k',33:'em',34:'O',35:'at',36:'W',37:'0',38:'cm',39:'ct',40:'3',41:'E',42:'ot',43:'om',44:'2',45:'ht',46:'et',47:'sw',48:'G',49:'5',50:',',51:'H',52:'8',53:'6',54:'B',55:'ut',56:'V',57:'4',58:'K',59:'P',60:'D',61:'x',62:'R',63:'9',64:'L',65:'Ke',66:'Ot',67:'F',68:'Dw',69:'s,',70:"s'",71:'C',72:'e,',73:'eg',74:'am',75:'z',76:"n'",77:'ngt',78:'7',79:'n,',80:'ft',81:'Sw',82:'Ma',83:'30',84:'j',85:'sm',86:'q',87:'xt',88:'ng,',89:'U',90:'d,',91:'74',92:'&',93:'hm',94:'ug',96:'pt',97:'dg',98:'St',99:'Mo',100:"'",101:'m,',102:'k,',105:'yt',106:'94',107:'ur,',108:'Me',109:'64',110:'20',111:'50',112:'J',113:'Kn',114:'c,',115:'Z',116:'um',117:'om,',118:'h,',119:"h'",120:'y,',121:'rr',122:':',123:'og',124:"y'"}
def G(k): return np.frombuffer(k[1],dtype=bool).reshape(k[0])
T={}  # char -> template (13 x w)
for n,l in LAB.items():
    if len(l)==1: T.setdefault(l,G(ks[n]))
# learn from multis by subtracting known prefix/suffix (touching, 0 gap) -- also allow 1-col gap
changed=True
while changed:
    changed=False
    for n,l in LAB.items():
        if len(l)<2: continue
        g=G(ks[n]); unk=[c for c in l if c not in T]
        if len(set(unk))!=1: continue
        c=unk[0]
        if l.count(c)!=1: continue
        i=l.index(c)
        left=sum(T[x].shape[1] for x in l[:i]); right=sum(T[x].shape[1] for x in l[i+1:])
        piece=g[:,left:g.shape[1]-right]
        # trim empty cols
        cols=np.nonzero(piece.any(0))[0]
        if len(cols)==0: continue
        T[c]=piece[:,cols[0]:cols[-1]+1]; changed=True
print('templates',''.join(sorted(T)))
pickle.dump(T,open('templates.pkl','wb'))
