#!/usr/bin/env python3
"""Probe v8: FLOATING WINDOW. Attach at known size, open a centered floating window via
nvim_open_win(relative='editor'), capture every grid_resize/win_pos/grid_line/msg_set_pos/
win_viewport_margins with FULL payload to see exactly what nvim sends for the float and how
the outer frame (grid 1) is updated underneath it."""
import socket, subprocess, sys, time, os

try:
    import msgpack
except ImportError:
    print("need msgpack"); sys.exit(1)

PORT = 47962
OUT = "mg_probe8.log"
W, H = 80, 30   # attach width(cols)=80, height(rows)=30

def main():
    nvim = r"C:\Program Files\Neovim\bin\nvim.exe"
    proc = subprocess.Popen([nvim, "--listen", f"127.0.0.1:{PORT}", "--headless"],
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    s = None
    for _ in range(30):
        try:
            s = socket.create_connection(("127.0.0.1", PORT), timeout=2); break
        except OSError:
            if proc.poll() is not None:
                print("nvim exited:", proc.stderr.read().decode(errors="replace")[:400]); sys.exit(1)
            time.sleep(0.3)
    assert s, "connect failed"
    s.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 1 << 20)
    s.settimeout(0.5)   # so recv_msgs' time window is actually honored
    up = msgpack.Unpacker(raw=True, strict_map_key=False)

    def send(o): s.sendall(msgpack.packb(o))
    pending = []   # notifications swallowed while waiting for an RPC response
    def recv_msgs(n=999, timeout=1.5):
        out=[]; end=time.time()+timeout
        while len(out)<n and time.time()<end:
            try: data=s.recv(65536)
            except socket.timeout: break
            if not data: break
            up.feed(data)
            while True:
                try: m=up.unpack()
                except (msgpack.exceptions.ExtraData, StopIteration, msgpack.exceptions.OutOfData): break
                out.append(m)
                # trace every message to file for forensics
                with open(OUT+".trace","a") as tf:
                    if isinstance(m,list) and len(m)>=3 and m[0]==2 and m[1] in (b"redraw","redraw"):
                        names=[e[0].decode() if isinstance(e,list) and e else "?" for e in m[2]]
                        tf.write(f"T+{time.time()-T0:7.3f} redraw n={len(m[2])} events={names}\n")
                    elif isinstance(m,list) and len(m)>=4 and m[0] in (1,2):
                        tf.write(f"T+{time.time()-T0:7.3f} rpc resp id={m[1]} type={m[0]}\n")
        return out
    def call(method,*args,debug=False):
        rid=1; send([0,rid,method,list(args)])
        while True:
            batch = recv_msgs(999,timeout=3.0)
            for m in batch:
                if debug and isinstance(m,list) and len(m)>=4 and m[0] in (1,2) and m[1]==rid:
                    print(f"  RAW {method} resp: {repr(m)}")
                if isinstance(m,list) and len(m)>=4 and m[0]==2 and m[1]==rid:
                    pending.extend(batch);   # keep the rest (redraws that arrived with the response)
                    print(f"RPC ERROR {method}: {m[3]}"); return None
                if isinstance(m,list) and len(m)>=4 and m[0]==1 and m[1]==rid:
                    pending.extend(batch);   # keep the rest
                    return m[3]
            pending.extend([])

    def dump(tag, maxlines=300):
        msgs = list(pending); pending.clear()
        msgs += recv_msgs(999, timeout=1.5)
        with open(OUT,"a") as f:
            f.write(f"\n########## {tag} : {len(msgs)} msgs ##########\n")
            n=0
            for m in msgs:
                if not (isinstance(m,list) and len(m)>=3 and m[0]==2 and m[1] in (b"redraw","redraw")): continue
                for ev in m[2]:
                    if not isinstance(ev,list) or len(ev)<1: continue
                    name = ev[0].decode() if isinstance(ev[0],bytes) else str(ev[0])
                    f.write(f"{name} :: {repr(ev)[:300]}\n"); n+=1
                    if n>maxlines: break

    T0 = time.time()
    send([0,999,"nvim_ui_attach",[W,H,{"rgb":True,"ext_linegrid":True,"ext_multigrid":True}]])
    time.sleep(1.5)
    open(OUT,"w").close(); open(OUT+".trace","w").close()
    dump("ATTACH initial (single window)")

    # Open a centered floating window: 20 cols x 8 rows, relative to editor center.
    buf = call("nvim_create_buf", True, False)
    for i in range(8):
        call("nvim_buf_set_lines", buf, i, i+1, False, [f"FLOAT LINE {i}"])
    opts = {"relative":"editor","width":20,"height":8,"row":11.0,"col":30.0}
    winid = call("nvim_open_win", buf, True, opts, debug=True)
    print("open_win returned:", repr(winid))
    time.sleep(0.5)
    # headless nvim batches redraws and only flushes on input; force a flush:
    call("nvim_command", "redraw!")
    time.sleep(1.0)
    dump("AFTER open floating window")

    proc.terminate()
    print("WROTE", OUT, os.path.getsize(OUT), "bytes")

if __name__=="__main__":
    main()
