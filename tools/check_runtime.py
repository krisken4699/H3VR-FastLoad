"""Pre-deploy check: every mscorlib method / type our DLL calls must exist in the GAME's mscorlib (Unity 5.6 Mono,
.NET 3.5 profile). We compile against net46, so the compiler can pick overloads or types the game does not have
(MissingMethodException / TypeLoadException at runtime): generic Marshal overloads, System.Action, state-machine
attributes, FieldInfo ==, ...

python check_runtime.py <our.il> <game_mscorlib.il>
Exits 1 and lists the problems if anything is missing.
"""
import re
import sys


def nparams(sig):
    """Parameter count of an IL parameter list (generic arguments' commas not counted)."""
    sig = sig.strip()
    while '<' in sig:
        new = re.sub(r'<[^<>]*>', '', sig)
        if new == sig:
            break
        sig = new
    return 0 if not sig else sig.count(',') + 1

ours = open(sys.argv[1], encoding='utf-8', errors='ignore').read()
game = open(sys.argv[2], encoding='utf-8', errors='ignore').read()

# Game: type -> {method name: set(generic arities)}. ilspy IL: ".class ... Full.Name", then ".method ..." with the
# name and generic parameters on the following line.
types = {}
lines = game.splitlines()
pending = {}
i = 0
while i < len(lines):
    st = lines[i].strip()
    if st.startswith('.class'):
        nm = re.search(r'([\w.`]+)\s*(?:<.*>)?\s*$', st)
        name = nm.group(1) if nm else st.split()[-1]
        types.setdefault(name, {})
    elif st.startswith('.method'):
        head = st
        j = i
        while '(' not in head and j + 1 < len(lines):
            j += 1
            head += ' ' + lines[j].strip()
        while ')' not in head[head.index('('):] and j + 1 < len(lines):
            j += 1
            head += ' ' + lines[j].strip()
        mm = re.search(r'([\w.]+)\s*(<[^()]*>)?\s*\(([^)]*)\)', head)
        if mm:
            pending = (mm.group(1), 0 if not mm.group(2) else mm.group(2).count(',') + 1, nparams(mm.group(3)))
        i = j
    else:
        # "} // end of method Type::Name" says which type the method belongs to (nested classes included).
        em = re.match(r'\}\s*//\s*end of method\s+([\w`]+)::([\w.]+)', st)
        if em and pending:
            tshort, meth = em.group(1), em.group(2)
            arity = pending[1] if pending[0] == meth else 0
            npar = pending[2] if pending[0] == meth else -1
            types.setdefault('#' + tshort, {}).setdefault(meth, set()).add((arity, npar))
            pending = {}
    i += 1

# Nested type names in ilspy output are short; build a short-name index too.
short = {}
for t in types:
    if not t.startswith('#'):
        short.setdefault(t.split('.')[-1], []).append(t)

problems = set()
# Overloads count too (e.g. lock compiles to Monitor.Enter(object, bool&), which the game does not have).
for m in re.finditer(r'\[mscorlib\]([\w.`/]+)::([\w.]+)(<[^>(]*>)?\(([^)]*)\)', ours):
    t, meth, gen, sig = m.group(1), m.group(2), m.group(3), m.group(4)
    t = t.split('/')[-1]
    sh = t.split('.')[-1]
    if sh not in short:
        problems.add('type missing: ' + t)
        continue
    methods = types.get('#' + sh, {})
    arity = 0 if not gen else gen.count(',') + 1
    n = nparams(sig)
    ok = meth in methods and any(a == arity and (p == n or p < 0) for a, p in methods[meth])
    if not ok:
        problems.add('method missing: ' + t + '::' + meth + (gen or '') + '(' + sig + ')')
for m in re.finditer(r'\[mscorlib\]([\w.`]+)', ours):
    t = m.group(1)
    if t.split('.')[-1] not in short and not any(x == t for x in types):
        problems.add('type missing: ' + t)

for bad in ('IteratorStateMachineAttribute', 'AsyncStateMachineAttribute'):
    if bad in ours:
        problems.add('uses ' + bad + ' (coroutine / async): the game cannot load it')

if problems:
    print('RUNTIME CHECK FAILED:')
    for p in sorted(problems):
        print('  ' + p)
    sys.exit(1)
print('runtime check ok')
