"""Disposable process tree with a local listener; never uses a production port."""
import json
import os
import signal
import socket
import sys
import time

read_fd, write_fd = os.pipe()
child = os.fork()
if child == 0:
    os.close(read_fd)
    signal.signal(signal.SIGTERM, signal.SIG_IGN)
    signal.signal(signal.SIGALRM, lambda *_: os._exit(0))
    signal.alarm(40)
    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    listener.listen()
    os.write(write_fd, json.dumps({"child": os.getpid(), "port": listener.getsockname()[1]}).encode())
    os.close(write_fd)
    while True:
        signal.pause()

os.close(write_fd)
payload = os.read(read_fd, 1024).decode()
os.close(read_fd)
signal.signal(signal.SIGALRM, lambda *_: os._exit(0))
signal.alarm(40)
if sys.argv[1] == "ignore":
    signal.signal(signal.SIGTERM, signal.SIG_IGN)
elif sys.argv[1] == "slow":
    def slow_exit(*_):
        time.sleep(0.4)
        os._exit(0)
    signal.signal(signal.SIGTERM, slow_exit)
else:
    signal.signal(signal.SIGTERM, lambda *_: os._exit(0))
print("FIXTURE " + payload, flush=True)
while True:
    signal.pause()
