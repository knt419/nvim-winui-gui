#!/usr/bin/env python3
"""Probe v4: print win_pos / msg_set_pos args ELEMENT-BY-ELEMENT with types,
so the exact field alignment is unambiguous (the window handle is a msgpack ext)."""
import socket, subprocess, sys, time, os, tempfile

try:
    import msgpack
except ImportError:
    print("NEED_MSGPACK"); sys.exit(2)

PORT = 47913
_unpacker = None

def send(sock, obj): sock.sendall(msgpack.packb(obj))

def recv_msg(sock):
    while True:
        try: return _unpacker.unpack()
        except msgpack.exceptions.ExtraData: continue
        except msgpack.exceptions.OutOfData:
            chunk = sock.recv(65536)
            if not chunk: raise ConnectionError("closed")
            _unpacker.feed(chunk)

def main():
    nvim = os.environ.get("NVIM_WINUI_NVIM", r"C:\Program Files\Neovim\bin\nvim.exe")
    proc = subprocess.Popen([nvim, "--listen", f"127.0.0.1:{PORT}", "--headless"],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    sock = None
    for _ in range(60):
        if proc.poll() is not None: print("died", proc.returncode); break
        try: sock = socket.create_connection(("127.0.0.1", PORT), timeout=0.3); break
        except OSError: time.sleep(0.25)
    if sock is None: proc.terminate(); sys.exit(4)
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

    def drain(seconds):
        end = time.time() + seconds
        while time.time() < end:
            try:
                sock.settimeout(max(0.05, end - time.time()))
                obj = recv_msg(sock)
            except (socket.timeout, TimeoutError): continue
            except ConnectionError: break
            if isinstance(obj, list) and len(obj) == 3 and obj[0] == 2 and obj[1] == "redraw":
                for ev in obj[2]:
                    name = ev[0]
                    if name in ("win_pos", "msg_set_pos"):
                        inner = ev[1]
                        print(f"\n== {name} : len(inner)={len(inner)} ==")
                        for i, x in enumerate(inner):
                            # show type + a compact value (ext -> its data hex)
                            if isinstance(x, bytes):
                                val = f"bytes({x.hex()})"
                            elif hasattr(x, "data") and hasattr(x, "type"):  # msgpack ExtType
                                val = f"Ext(type={x.type}, data={bytes(x.data).hex()})"
                            else:
                                val = repr(x)
                            print(f"   [{i}] {type(x).__name__:10s} = {val}")

    call("nvim_ui_attach", [80, 240, {"ext_linegrid": True, "ext_multigrid": True}])
    drain(1.5)
    print("\n--- :split ---")
    call("nvim_command", ["split"])
    drain(1.2)

    proc.terminate()
    try: proc.wait(timeout=3)
    except Exception: pass
    print("\nDONE")

if __name__ == "__main__":
    main()
