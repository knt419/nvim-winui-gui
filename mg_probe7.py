#!/usr/bin/env python3
"""Probe v7: confirm CURRENT mode (ext_linegrid only, NO ext_multigrid) field layout.
Dumps raw tuples for grid_resize / grid_line / grid_clear / cursor_position so we know
exactly what the working single-grid client receives — must not break it."""
import socket, subprocess, sys, time, os, json
import msgpack

NVIM = r"C:\Program Files\Neovim\bin\nvim.exe"
PORT = 47931
OUT = "mg_probe7.log"

def main():
    open(OUT, "w").close()
    proc = subprocess.Popen([NVIM, "--listen", f"127.0.0.1:{PORT}", "--headless"],
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    s = None
    for _ in range(40):
        try:
            s = socket.create_connection(("127.0.0.1", PORT), timeout=2); break
        except OSError:
            if proc.poll() is not None: print("nvim died"); return
            time.sleep(0.1)
    if s is None: print("no connect"); return
    s.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 1 << 20)
    up = msgpack.Unpacker(raw=True, strict_map_key=False)

    def send(o): s.sendall(msgpack.packb(o))
    def recv(timeout=3.0):
        s.settimeout(timeout); out=[]; buf=b""
        try:
            while True:
                d=s.recv(1<<20)
                if not d: break
                up.feed(d)
                while True:
                    try: out.append(up.unpack())
                    except msgpack.exceptions.ExtraData: break
                    except msgpack.exceptions.OutOfData: break
        except socket.timeout: pass
        return out

    rid=0
    def call(m,*a):
        nonlocal rid; rid+=1; send([0,rid,m,list(a)]); 
        for m2 in recv(3.0):
            if isinstance(m2,list) and len(m2)==4 and m2[0]==1 and m2[1]==rid: return m2[3]
    call("nvim_ui_attach", 80, 30, {"rgb":True,"ext_linegrid":True})  # NO multigrid
    time.sleep(0.5)
    seen={}
    for _ in range(6):
        for m in recv(1.2):
            if not (isinstance(m,list) and len(m)==3 and m[0]==2 and m[1]=="redraw"): continue
            for ev in m[2]:
                if isinstance(ev,list) and len(ev)>=1:
                    nm = ev[0] if isinstance(ev[0],str) else (ev[2] if len(ev)>2 and isinstance(ev[2],str) else None)
                    if nm in ("grid_resize","grid_line","grid_clear","cursor_position"):
                        seen.setdefault(nm, []).append(json.dumps(ev)[:160])
    with open(OUT,"a") as f:
        for k,v in seen.items():
            f.write(f"== {k} ({len(v)}) ==\n"); 
            for line in v[:4]: f.write("  "+line+"\n")
    proc.kill()
    print(open(OUT).read())

if __name__=="__main__": main()
