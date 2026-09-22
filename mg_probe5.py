#!/usr/bin/env python3
"""Probe v5: capture the COMPLETE multigrid event stream (with :split and :vsplit) to a file,
so the C# implementation matches exactly what nvim sends. Every redraw event is dumped in full."""
import socket, struct, subprocess, sys, time, os

try:
    import msgpack
except ImportError:
    print("need msgpack"); sys.exit(1)

PORT = 47950
OUT = "mg_probe5.events.log"

def main():
    nvim = r"C:\Program Files\Neovim\bin\nvim.exe"
    proc = subprocess.Popen([nvim, "--listen", f"127.0.0.1:{PORT}", "--headless"],
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    s = None
    for attempt in range(30):
        try:
            s = socket.create_connection(("127.0.0.1", PORT), timeout=2)
            break
        except OSError:
            if proc.poll() is not None:
                print("nvim exited early:", proc.stderr.read().decode(errors="replace")[:500]); sys.exit(1)
            time.sleep(0.3)
    if s is None:
        print("connect failed after retries"); sys.exit(1)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 1 << 20)
    up = msgpack.Unpacker(raw=True, strict_map_key=False)

    def send(obj):
        s.sendall(msgpack.packb(obj))

    def recv_msgs(n=1, timeout=3.0):
        out = []
        end = time.time() + timeout
        while len(out) < n and time.time() < end:
            try:
                data = s.recv(65536)
            except socket.timeout:
                break
            if not data:
                break
            up.feed(data)
            while True:
                try:
                    out.append(up.unpack())
                except msgpack.exceptions.ExtraData:
                    break
                except (StopIteration, msgpack.exceptions.OutOfData):
                    break  # incomplete message; wait for more bytes
        return out

    def call(method, *args):
        rid = 1
        send([0, rid, method, list(args)])
        while True:
            for m in recv_msgs(1, timeout=3.0):
                if isinstance(m, list) and len(m) >= 4 and m[0] == 1 and m[1] == rid:
                    return m[3]

    def drain_and_log(tag):
        # collect all pending notifications for a short window
        msgs = recv_msgs(999, timeout=1.2)
        print(f"[{tag}] got {len(msgs)} messages")
        with open(OUT, "a") as f:
            f.write(f"\n===== {tag} : {len(msgs)} messages =====\n")
            for i, m in enumerate(msgs):
                r = repr(m)[:300]
                print(f"  [{i}] {r}")
                f.write("RAW " + r + "\n")

    # attach with multigrid + linegrid
    send([0, 999, "nvim_ui_attach", [80, 24, {"rgb": True, "ext_linegrid": True, "ext_multigrid": True}]])
    time.sleep(1.5)
    open(OUT, "w").close()
    drain_and_log("ATTACH (initial)")

    # :split -> two stacked windows
    call("nvim_command", "split")
    time.sleep(0.8)
    drain_and_log("AFTER :split")

    # :vsplit -> three windows
    call("nvim_command", "vsplit")
    time.sleep(0.8)
    drain_and_log("AFTER :vsplit")

    proc.terminate()
    print("WROTE", OUT, os.path.getsize(OUT), "bytes")

if __name__ == "__main__":
    main()
