"""Leave only a root-owned TIME_WAIT socket on a random loopback port.

Used by the opt-in macOS CI probe test. No routes, DNS or proxy settings change.
"""
import signal
import socket

signal.alarm(5)
with socket.socket() as listener:
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind(("127.0.0.1", 0))
    port = listener.getsockname()[1]
    listener.listen(1)
    with socket.create_connection(("127.0.0.1", port), timeout=2) as client:
        with listener.accept()[0] as accepted:
            accepted.shutdown(socket.SHUT_WR)
            assert client.recv(1) == b""
            client.close()
print(port, flush=True)
