#!/usr/bin/env python3
"""Robust probe: capture real ext_multigrid events from nvim to confirm the coordinate model.

Wire format (matches NvimClient.cs, verified vs nvim 0.12):
  Request client->nvim : [0, id:int, method:str, params:[...]]
  Response nvim->client: [1, rid:int, error|null, result]
  Notification nvim    : [2, method:str, args:[...]]   (e.g. "redraw")

Starts `nvim --listen 127.0.0.1:PORT --headless` (exactly like the app), attaches with
ext_multigrid+ext_linegrid, then :split and resizes. Logs EVERY message verbatim so we can
see grid_resize / win_pos / grid_line payloads and confirm the global-grid coordinate model.
"""
import socket, subprocess, sys, time, os, tempfile

try:
    import msgpack
except ImportError:
    print("NEED_MSGPACK"); sys.exit(2)

PORT = 47901
RAWLOG = os.path.join(tempfile.gettempdir(), "mg_probe2.raw.log")

_unpacker = None

def log_raw(obj):
    with open(RAWLOG, "a", encoding="utf-8") as f:
        s = repr(obj)
        f.write(s[:600] + ("\n" if len(s) <= 600 else "...(trunc)\n"))

def send(sock, obj):
    sock.sendall(msgpack.packb(obj))

def recv_msg(sock):
    while True:
        try:
            obj = _unpacker.unpack()
            log_raw(obj)
            return obj
        except msgpack.exceptions.ExtraData:
            continue
        except msgpack.exceptions.OutOfData:
            chunk = sock.recv(65536)
            if not chunk:
                raise ConnectionError("closed")
            _unpacker.feed(chunk)  # feed new bytes to the unpacker (was missing -> nothing ever decoded)

def main():
    open(RAWLOG, "w").close()  # truncate
    nvim = os.environ.get("NVIM_WINUI_NVIM", r"C:\Program Files\Neovim\bin\nvim.exe")
    print(f"nvim={nvim} exists={os.path.exists(nvim)} port={PORT}")
    errf = open(os.path.join(tempfile.gettempdir(), "mg_probe2.nvim.err"), "w", encoding="utf-8")
    proc = subprocess.Popen([nvim, "--listen", f"127.0.0.1:{PORT}", "--headless"],
                            stdout=subprocess.DEVNULL, stderr=errf)

    sock = None
    for attempt in range(60):  # up to ~15s
        if proc.poll() is not None:
            print(f"nvim DIED rc={proc.returncode}"); break
        try:
            s = socket.create_connection(("127.0.0.1", PORT), timeout=0.3)
            sock = s; break
        except OSError as e:
            time.sleep(0.25)
    if sock is None:
        print("GAVE UP connecting"); proc.terminate(); sys.exit(4)
    # KEY (from NvimClient.cs): enlarge receive buffer BEFORE traffic. Windows default ~8KB
    # collapses the TCP window on nvim's big initial flush -> RST. 128KB matches the app.
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 131072)

    global _unpacker
    _unpacker = msgpack.Unpacker(raw=False)
    mid = 0

    def call(method, params):
        nonlocal mid
        mid += 1
        send(sock, [0, mid, method, params])   # integer type tag 0, string method
        end = time.time() + 5.0
        while time.time() < end:
            try:
                sock.settimeout(max(0.02, end - time.time()))
                obj = recv_msg(sock)
            except (socket.timeout, TimeoutError):
                continue
            except ConnectionError:
                raise
            # response = [1, rid, error|null, result]  ; notification = [2, name, args]
            if isinstance(obj, list) and len(obj) >= 4 and obj[0] == 1 and obj[1] == mid:
                return obj[3]
            # else: a notification arrived while waiting -> already logged; keep looping

    def drain(seconds):
        end = time.time() + seconds
        n = 0
        while time.time() < end:
            try:
                sock.settimeout(max(0.05, end - time.time()))
                recv_msg(sock)
                n += 1
            except (socket.timeout, TimeoutError):
                continue
            except ConnectionError:
                break
        return n

    print("attaching...")
    resp = call("nvim_ui_attach", [80, 240, {"ext_linegrid": True, "ext_multigrid": True}])
    print(f"ui_attach response={resp!r}")
    drain(1.5)

    print(":split ...")
    call("nvim_command", ["split"])
    drain(1.2)

    print("resize 40x120 ...")
    call("nvim_ui_try_resize", [40, 120])
    drain(1.5)

    proc.terminate()
    try: proc.wait(timeout=3)
    except Exception: proc.kill()
    errf.close()
    print(f"raw log lines={sum(1 for _ in open(RAWLOG, encoding='utf-8'))}")
    print("DONE")

if __name__ == "__main__":
    main()
