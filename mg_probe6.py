#!/usr/bin/env python3
"""Probe v6: DEFINITIVE coordinate model. Attach at a known size, capture every
grid_resize / win_pos / grid_line / win_viewport_margins event with FULL payload + types,
before and after :split. Writes to file (stdout gets truncated)."""
import socket, subprocess, sys, time, os

try:
    import msgpack
except ImportError:
    print("need msgpack"); sys.exit(1)

PORT = 47960
OUT = "mg_probe6.log"
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
    up = msgpack.Unpacker(raw=True, strict_map_key=False)

    def send(o): s.sendall(msgpack.packb(o))
    def recv_msgs(n=999, timeout=1.5):
        out=[]; end=time.time()+timeout
        while len(out)<n and time.time()<end:
            try: data=s.recv(65536)
            except socket.timeout: break
            if not data: break
            up.feed(data)
            while True:
                try: out.append(up.unpack())
                except (msgpack.exceptions.ExtraData, StopIteration, msgpack.exceptions.OutOfData): break
        return out
    def call(method,*args):
        rid=1; send([0,rid,method,list(args)])
        while True:
            for m in recv_msgs(999,timeout=3.0):
                if isinstance(m,list) and len(m)>=4 and m[0]==1 and m[1]==rid: return m[3]

    def dump(tag):
        msgs = recv_msgs(999, timeout=1.5)
        with open(OUT,"a") as f:
            f.write(f"\n########## {tag} : {len(msgs)} msgs ##########\n")
            for m in msgs:
                if not (isinstance(m,list) and len(m)>=3 and m[0]==2 and m[1] in (b"redraw","redraw")): continue
                for ev in m[2]:
                    if not isinstance(ev,list) or len(ev)<1: continue
                    name = ev[0].decode() if isinstance(ev[0],bytes) else str(ev[0])
                    if any(k in name for k in ("grid_resize","win_pos","grid_line","win_viewport_margins","msg_set_pos","win_float_pos")):
                        f.write(f"{name} :: {repr(ev)}\n")

    send([0,999,"nvim_ui_attach",[W,H,{"rgb":True,"ext_linegrid":True,"ext_multigrid":True}]])
    time.sleep(1.5)
    open(OUT,"w").close()
    dump("ATTACH initial (single window)")

    call("nvim_command","split"); time.sleep(0.8); dump("AFTER :split")
    proc.terminate()
    print("WROTE", OUT, os.path.getsize(OUT), "bytes")

if __name__=="__main__":
    main()
