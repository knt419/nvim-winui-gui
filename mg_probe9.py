#!/usr/bin/env python3
"""Probe v9: verify what nvim sends for a WINBLEND float under ext_multigrid.
1) open a float, set winblend=N via nvim_win_set_option
2) dump hl_attr_define entries carrying 'blend' and the float grid's grid_line
   so the C# client knows whether ParseHl will receive blend in rgb_attr.
3) also demonstrate the transparent (bg=NONE) float: what hl the float cells carry.
"""
import socket, subprocess, sys, time, os

try:
    import msgpack
except ImportError:
    print("need msgpack"); sys.exit(1)

PORT = 47973
OUT = "mg_probe9.log"
W, H = 80, 30

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
    s.settimeout(0.5)
    up = msgpack.Unpacker(raw=True, strict_map_key=False)

    def send(o): s.sendall(msgpack.packb(o))
    pending = []
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
        return out
    def drain():
        pending.extend(recv_msgs(999, timeout=0.4))
    def call(method,*args):
        rid=1; send([0,rid,method,list(args)])
        while True:
            batch = recv_msgs(999,timeout=3.0)
            for m in batch:
                if isinstance(m,list) and len(m)>=4 and m[0]==1 and m[1]==rid:
                    pending.extend(batch); return m[3]
                if isinstance(m,list) and len(m)>=4 and m[0]==2 and m[1]==rid:
                    pending.extend(batch); print("RPC ERR",m[3]); return None

    def redraws():
        msgs = list(pending); pending.clear()
        msgs += recv_msgs(999, timeout=1.2)
        evs=[]
        for m in msgs:
            if not (isinstance(m,list) and len(m)>=3 and m[0]==2 and m[1] in (b"redraw","redraw")): continue
            for ev in m[2]:
                if not isinstance(ev,list) or len(ev)<1: continue
                evs.append(ev)
        return evs

    send([0,999,"nvim_ui_attach",[W,H,{"rgb":True,"ext_linegrid":True,"ext_multigrid":True}]])
    drain()
    buf = call("nvim_create_buf", True, False)
    for i in range(8):
        call("nvim_buf_set_lines", buf, i, i+1, False, [f"PARENT LINE {i}"])
    # normal float
    opts = {"relative":"editor","width":20,"height":8,"row":5.0,"col":30.0,"style":"minimal"}
    fid = call("nvim_open_win", buf, True, opts)
    print("float winid:", repr(fid))
    call("nvim_win_set_option", fid, "winblend", 30)
    call("nvim_command", "redraw!")
    evs = redraws()
    with open(OUT,"w") as f:
        f.write("== after winblend=30 float ==\n")
        for ev in evs:
            name = ev[0].decode() if isinstance(ev[0],bytes) else str(ev[0])
            if name == "hl_attr_define":
                for tup in ev[1:]:
                    # find any dict containing blend
                    parts = []
                    for p in tup[1:]:
                        if isinstance(p, dict):
                            parts.append(str({k.decode() if isinstance(k,bytes) else k: v for k,v in p.items()}))
                    if any("blend" in x for x in parts):
                        f.write("  hl_attr_define blend-carrying: " + repr(tup)[:240] + "\n")
            elif name == "win_float_pos":
                f.write("  win_float_pos: " + repr(ev)[:200] + "\n")
            elif name == "grid_line" and ev[1] == 4:
                f.write("  grid_line(g4): " + repr(ev)[:200] + "\n")
    # now a transparent float: style=minimal, no border; NormalFloat may be opaque in theme.
    fopts = {"relative":"editor","width":20,"height":4,"row":14.0,"col":30.0,"style":"minimal"}
    tid = call("nvim_open_win", buf, True, fopts)
    call("nvim_win_set_option", tid, "winblend", 100)
    call("nvim_command", "redraw!")
    evs = redraws()
    with open(OUT,"a") as f:
        f.write("\n== after winblend=100 float (should be fully translucent) ==\n")
        for ev in evs:
            name = ev[0].decode() if isinstance(ev[0],bytes) else str(ev[0])
            if name == "hl_attr_define":
                for tup in ev[1:]:
                    parts=[]
                    for p in tup[1:]:
                        if isinstance(p,dict):
                            parts.append(str({k.decode() if isinstance(k,bytes) else k: v for k,v in p.items()}))
                    if any("blend" in x for x in parts):
                        f.write("  hl_attr_define blend-carrying: " + repr(tup)[:240] + "\n")
            elif name == "win_float_pos":
                f.write("  win_float_pos: " + repr(ev)[:200] + "\n")
            elif name == "grid_line" and ev[1] == 4:
                f.write("  grid_line(g4): " + repr(ev)[:160] + "\n")
    proc.terminate()
    print("WROTE", OUT, os.path.getsize(OUT), "bytes")

if __name__=="__main__":
    main()