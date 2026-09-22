#!/usr/bin/env python3
"""Probe v2: capture ALL multigrid POSITIONING events (win_pos / win_float_pos / msg_set_pos)
with full payloads, to confirm the coordinate model before implementing.

Logs every event name + full args for positioning/grid_resize/win_viewport events only
(grid_line is too verbose). Writes JSONL so we can parse it precisely.
"""
import socket, subprocess, sys, time, os, tempfile, json

try:
    import msgpack
except ImportError:
    print("NEED_MSGPACK"); sys.exit(2)

PORT = 47911
OUT = os.path.join(tempfile.gettempdir(), "mg_probe3_events.jsonl")

_unpacker = None

def _san(o):
    """Make any msgpack-decoded value JSON-serializable (bytes/ext types -> repr)."""
    if isinstance(o, bytes):
        return o.hex()
    if isinstance(o, (list, tuple)):
        return [_san(x) for x in o]
    if isinstance(o, dict):
        return {str(k): _san(v) for k, v in o.items()}
    try:
        json.dumps(o)
        return o
    except TypeError:
        return repr(o)

def send(sock, obj):
    sock.sendall(msgpack.packb(obj))

def recv_msg(sock):
    while True:
        try:
            return _unpacker.unpack()
        except msgpack.exceptions.ExtraData:
            continue
        except msgpack.exceptions.OutOfData:
            chunk = sock.recv(65536)
            if not chunk:
                raise ConnectionError("closed")
            _unpacker.feed(chunk)

def main():
    open(OUT, "w").close()  # truncate
    nvim = os.environ.get("NVIM_WINUI_NVIM", r"C:\Program Files\Neovim\bin\nvim.exe")
    proc = subprocess.Popen([nvim, "--listen", f"127.0.0.1:{PORT}", "--headless"],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    sock = None
    for _ in range(60):
        if proc.poll() is not None: print("died", proc.returncode); break
        try:
            sock = socket.create_connection(("127.0.0.1", PORT), timeout=0.3); break
        except OSError: time.sleep(0.25)
    if sock is None:
        proc.terminate(); sys.exit(4)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 131072)

    global _unpacker
    _unpacker = msgpack.Unpacker(raw=False)
    mid = 0

    def call(method, params):
        nonlocal mid
        mid += 1
        send(sock, [0, mid, method, params])
        end = time.time() + 5.0
        while time.time() < end:
            try:
                sock.settimeout(max(0.02, end - time.time()))
                obj = recv_msg(sock)
            except (socket.timeout, TimeoutError): continue
            if isinstance(obj, list) and len(obj) >= 4 and obj[0] == 1 and obj[1] == mid:
                return obj[3]

    def drain_logged(seconds):
        end = time.time() + seconds
        n = 0
        while time.time() < end:
            try:
                sock.settimeout(max(0.05, end - time.time()))
                obj = recv_msg(sock)
            except (socket.timeout, TimeoutError): continue
            except ConnectionError: break
            if isinstance(obj, list) and len(obj) == 3 and obj[0] == 2 and obj[1] == "redraw":
                for ev in obj[2]:
                    name = ev[0]
                    # log positioning + sizing events with full args; skip verbose grid_line/scroll
                    if name in ("win_pos", "win_float_pos", "msg_set_pos", "grid_resize",
                                "win_viewport", "flush"):
                        rec = {"ev": name, "args": _san(ev[1])}
                        with open(OUT, "a", encoding="utf-8") as f:
                            f.write(json.dumps(rec) + "\n")
                    n += 1
        return n

    call("nvim_ui_attach", [80, 240, {"ext_linegrid": True, "ext_multigrid": True}])
    drain_logged(1.5)
    print("--- :split ---")
    call("nvim_command", ["split"])
    drain_logged(1.2)
    print("--- resize 40x120 ---")
    call("nvim_ui_try_resize", [40, 120])
    drain_logged(1.5)

    proc.terminate()
    try: proc.wait(timeout=3)
    except Exception: pass

    lines = open(OUT, encoding="utf-8").read().splitlines()
    print(f"total positioning/sizing events logged: {len(lines)}")
    for ln in lines:
        r = json.loads(ln)
        if r["ev"] == "flush": continue
        print(r["ev"], r.get("args"))
    print("DONE")

if __name__ == "__main__":
    main()
