"""Execute the adapter's real list-edit functions in a small TEST interpreter.
Not AutoCAD: cannot validate entmod/entupd persistence, save, fonts or rendering.
This catches malformed syntax, raw DXF chunk editing and protected-pair loss.
"""
from pathlib import Path
from dataclasses import dataclass
import re,json,sys
class Sym(str):pass
@dataclass
class Pair:
    a:object
    d:object
def parse(source):
    tokens=re.findall(r';[^\n]*|"(?:\\.|[^"\\])*"|[()\']|[^\s()\']+',source)
    tokens=[x for x in tokens if not x.startswith(';')];pos=0
    def read():
        nonlocal pos
        t=tokens[pos];pos+=1
        if t=='(':
            a=[]
            while tokens[pos]!=')':a.append(read())
            pos+=1;return a
        if t==')':raise ValueError('unexpected )')
        if t=="'":return [Sym('QUOTE'),read()]
        if t.startswith('"'):
            return re.sub(r'\\(.)',lambda m:{'n':'\n','r':'\r','t':'\t'}.get(m[1],m[1]),t[1:-1])
        try:return int(t)
        except ValueError:
            try:return float(t)
            except ValueError:return Sym(t.upper())
    forms=[]
    while pos<len(tokens):forms.append(read())
    return forms
class Env(dict):
    def __init__(self,parent=None):super().__init__();self.parent=parent
    def getv(self,k):return self[k] if k in self else self.parent.getv(k) if self.parent is not None else None
    def setv(self,k,v):
        if k in self or self.parent is None:self[k]=v
        else:self.parent.setv(k,v)
        return v
def truth(v):return v is not None and v is not False and v!=[]
def car(v):return v.a if isinstance(v,Pair) else v[0] if v else None
def cdr(v):return v.d if isinstance(v,Pair) else v[1:] or None if v else None
def cons(a,d):return [a]+(d or []) if isinstance(d,list) or d is None else Pair(a,d)
class Mini:
    def __init__(self,source):
        self.env=Env();self.env[Sym('T')]=True;self.env[Sym('NIL')]=None;self.func={};self.db={};self.edits=0
        def assoc(k,l):return next((x for x in l or [] if car(x)==k),None)
        self.native={
            'CAR':car,'CDR':cdr,'CADR':lambda x:car(cdr(x)),'CADDR':lambda x:car(cdr(cdr(x))),
            'CONS':cons,'LIST':lambda *x:list(x),'ASSOC':assoc,'REVERSE':lambda x:list(reversed(x or [])),
            'NTH':lambda n,x:x[n],'MEMBER':lambda x,l: True if x in (l or []) else None,
            '=':lambda *x:all(a==b for a,b in zip(x,x[1:])), '/=':lambda a,b:a!=b,
            '>':lambda a,b:a>b,'>=':lambda a,b:a>=b,'<':lambda a,b:a<b,
            'EQUAL':lambda a,b:a==b,'NOT':lambda a:not truth(a),'NULL':lambda a:not truth(a),
            'LOGAND':lambda a,b:a&b,'STRCAT':lambda *x:''.join(x),'ITOA':str,
            'LISTP':lambda x:isinstance(x,(list,Pair)) or x is None,
            'TYPE':lambda x:Sym('INT' if isinstance(x,int) else 'REAL' if isinstance(x,float) else 'LIST' if isinstance(x,(list,Pair)) else 'STR'),
            'RTOS':lambda x,m,p:format(x,f'.{p}f'),'VL-PRIN1-TO-STRING':lambda x:json.dumps(x,ensure_ascii=False),
            'HANDENT':lambda h:h if h in self.db else None,'ENTGET':lambda e,*x:self.db.get(e),
            'TBLSEARCH':lambda table,name:[Pair(70,self.lock)],'ENTUPD':lambda e:None,
            'ERROR':lambda text:(_ for _ in ()).throw(RuntimeError(text)),'ENTMOD':self.entmod,
        };self.lock=0
        for form in parse(source):
            if isinstance(form,list) and form and form[0]==Sym('DEFUN'):self.func[form[1]]=(form[2],form[3:])
            elif isinstance(form,list) and form and form[0]==Sym('PRINC'):pass
            else:raise ValueError('unexpected top-level expression')
    def entmod(self,data):
        if self.reject:return None
        handle=cdr(next(p for p in data if car(p)==5));self.db[handle]=data;self.edits+=1;return data
    def call(self,name,args,env=None):
        name=Sym(name.upper());env=env or self.env
        if name in self.native:return self.native[name](*args)
        params,body=self.func[name];local=Env(env);split=params.index(Sym('/')) if Sym('/') in params else len(params)
        assert len(args)==split,(name,'arity',args)
        for p,v in zip(params[:split],args):local[p]=v
        for p in params[split+1:]:local[p]=None
        result=None
        for statement in body:result=self.eval(statement,local)
        return result
    def eval(self,x,env):
        if isinstance(x,Sym):return env.getv(x)
        if not isinstance(x,list):return x
        if not x:return None
        op=x[0];a=x[1:]
        if op==Sym('QUOTE'):return a[0]
        if op==Sym('PROGN'):
            result=None
            for expr in a:result=self.eval(expr,env)
            return result
        if op==Sym('SETQ'):
            result=None
            for k,v in zip(a[::2],a[1::2]):result=env.setv(k,self.eval(v,env))
            return result
        if op==Sym('IF'):
            branch=a[1] if truth(self.eval(a[0],env)) else a[2] if len(a)>2 else None
            return self.eval(branch,env)
        if op==Sym('COND'):
            for clause in a:
                value=self.eval(clause[0],env)
                if truth(value):
                    for expr in clause[1:]:value=self.eval(expr,env)
                    return value
            return None
        if op in (Sym('AND'),Sym('OR')):
            value=True if op==Sym('AND') else None
            for expr in a:
                value=self.eval(expr,env)
                if truth(value)==(op==Sym('OR')):return value
            return value
        if op==Sym('FOREACH'):
            result=None
            for value in self.eval(a[1],env) or []:
                env.setv(a[0],value)
                for expr in a[2:]:result=self.eval(expr,env)
            return result
        if op==Sym('WHILE'):
            result=None;limit=0
            while truth(self.eval(a[0],env)):
                limit+=1;assert limit<1000,'test loop bound'
                for expr in a[1:]:result=self.eval(expr,env)
            return result
        return self.call(op,[self.eval(v,env) for v in a],env)

root=Path(__file__).resolve().parent
source=(root/'source/engine/scripts/directory_write.lsp').read_text(encoding='ascii') if (root/'source').exists() else (root.parents[1]/'engine/scripts/directory_write.lsp').read_text(encoding='ascii')
checks=[]
def need(ok,label):
    assert ok,label
    checks.append(label);print('PASS',label)
need(len(Mini(source).func)==16,'完整解析全部16个生产LISP函数，顶层无意外表达式')
def fixture(embedded=True,chunks=False):
    m=Mini(source);m.reject=False
    p=['A','B','图名','Old','\\W0.8;Old' if embedded else '',True if embedded else None,'New','\\W0.8;New' if embedded else '']
    data=[Pair(0,'ATTRIB'),Pair(5,'A'),Pair(330,'B'),Pair(2,'图名'),Pair(8,'0'),Pair(70,0),Pair(10,[123.4567890123456,9.5,0.0]),Pair(11,[127.1,9.5,0.0]),Pair(40,3.50000000001),Pair(41,0.8),Pair(50,0.015),Pair(1,'Old')]
    if embedded:data += [Pair(101,'Embedded Object'),Pair(40,3.50000000001),Pair(41,30.0)]+([Pair(3,'\\W0.8;'),Pair(1,'Old')] if chunks else [Pair(1,'\\W0.8;Old')])+[Pair(71,5)]
    m.db={'A':data,'B':[Pair(0,'INSERT'),Pair(5,'B'),Pair(41,150.00001811),Pair(42,150.00001811)]}
    m.env[Sym('DCW:PATCHES')]=[p]
    return m,p
for embedded,chunks in [(False,False),(True,False),(True,True)]:
    m,p=fixture(embedded,chunks);protected=m.call('dcw:protected',['A']);block=list(m.db['B'])
    m.call('dcw:patch',[p]);need(m.call('dcw:contents',[m.db['A']])==['New',p[7],p[5]],f'真实LISP编辑逻辑更新普通/内嵌文本 embedded={embedded} chunks={chunks}')
    need(m.call('dcw:protected',['A'])==protected and m.db['B']==block,f'真实LISP列表编辑保留全部重复DXF及精确数值 embedded={embedded} chunks={chunks}')
for mode in ['wrongowner','oldchanged','locked','invisible','rejectentmod','missing']:
    m,p=fixture();before=m.db['A']
    if mode=='wrongowner':p[1]='C'
    if mode=='oldchanged':p[3]='Different'
    if mode=='locked':m.lock=4
    if mode=='invisible':m.db['A']=[Pair(70,1) if q.a==70 else q for q in m.db['A']];before=m.db['A']
    if mode=='rejectentmod':m.reject=True
    if mode=='missing':p[0]='absent'
    rejected=False
    try:m.call('dcw:patch',[p])
    except RuntimeError:rejected=True
    need(rejected and m.edits==0 and m.db['A']==before,'真实LISP拒绝且不编辑：'+mode)
for mode in ['normal','xref','overlay','image']:
    m=Mini(source);records=iter([[Pair(70,4 if mode=='xref' else 8 if mode=='overlay' else 0)]])
    m.native['TBLNEXT']=lambda *a:next(records,None)
    m.native['NAMEDOBJDICT']=lambda:'root'
    m.native['DICTSEARCH']=lambda root,key:True if mode=='image' and key=='ACAD_IMAGE_DICT' else None
    rejected=False
    try:m.call('dcw:dependencies',[])
    except RuntimeError:rejected=True
    need(rejected==(mode!='normal'),'外部资源边界执行：'+mode)
target=root/'qa/lisp' if (root/'source').exists() else root.parents[1]/'tmp/stage3-qa/lisp'
target.mkdir(parents=True,exist_ok=True)
(target/'lisp_tests.json').write_text(json.dumps({'passed':len(checks),'failed':0,'scope':'Actual LISP function AST execution in a TEST interpreter with synthetic entget/entmod; NOT AutoCAD, no DWG persistence or rendering validation','checks':checks},ensure_ascii=False,indent=2),encoding='utf8')
