"""Loopback-only SSH/HTTP/HTTPS integration fixture. Requires paramiko.

All keys and certificates are disposable test artifacts in the supplied temporary directory.
No connection to a real SSH server is made.
"""
import datetime
import http.server
import ipaddress
import json
import pathlib
import select
import socket
import ssl
import sys
import threading

import paramiko
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ed25519, rsa
from cryptography.x509.oid import NameOID

directory = pathlib.Path(sys.argv[1])
directory.mkdir(parents=True, exist_ok=True)
client_key = ed25519.Ed25519PrivateKey.generate()
for name, encryption in [
    ("client", serialization.NoEncryption()),
    ("client-encrypted", serialization.BestAvailableEncryption(b"fixture-passphrase")),
]:
    (directory / name).write_bytes(client_key.private_bytes(
        serialization.Encoding.PEM, serialization.PrivateFormat.OpenSSH, encryption))
    (directory / name).chmod(0o600)
allowed_key = client_key.public_key().public_bytes(
    serialization.Encoding.OpenSSH, serialization.PublicFormat.OpenSSH).decode()
auth_attempts = 0


class HttpHandler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        body = str(auth_attempts).encode() if self.path == "/auth-count" else b"ssh-proxy-ok"
        self.send_response(200)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *_):
        pass


http_server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), HttpHandler)
https = http.server.ThreadingHTTPServer(("127.0.0.1", 0), HttpHandler)
tls_key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "127.0.0.1")])
now = datetime.datetime.now(datetime.timezone.utc)
cert = (x509.CertificateBuilder().subject_name(name).issuer_name(name)
        .public_key(tls_key.public_key()).serial_number(x509.random_serial_number())
        .not_valid_before(now - datetime.timedelta(minutes=1))
        .not_valid_after(now + datetime.timedelta(days=1))
        .add_extension(x509.SubjectAlternativeName([x509.IPAddress(ipaddress.ip_address("127.0.0.1"))]), False)
        .sign(tls_key, hashes.SHA256()))
(directory / "tls.pem").write_bytes(cert.public_bytes(serialization.Encoding.PEM))
(directory / "tls-key.pem").write_bytes(tls_key.private_bytes(
    serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
tls.load_cert_chain(directory / "tls.pem", directory / "tls-key.pem")
https.socket = tls.wrap_socket(https.socket, server_side=True)
for server in (http_server, https):
    threading.Thread(target=server.serve_forever, daemon=True).start()


class SshServer(paramiko.ServerInterface):
    def __init__(self):
        self.destinations = {}

    def get_allowed_auths(self, username):
        return "publickey,password"

    def check_auth_none(self, username):
        global auth_attempts
        auth_attempts += 1
        return paramiko.AUTH_FAILED

    def check_auth_password(self, username, password):
        global auth_attempts
        auth_attempts += 1
        return paramiko.AUTH_SUCCESSFUL if (username, password) == ("test", "fixture-password") else paramiko.AUTH_FAILED

    def check_auth_publickey(self, username, key):
        global auth_attempts
        auth_attempts += 1
        return paramiko.AUTH_SUCCESSFUL if username == "test" and (
            key.get_name() + " " + key.get_base64()) == allowed_key else paramiko.AUTH_FAILED

    def check_channel_direct_tcpip_request(self, channel_id, origin, destination):
        if destination not in (("127.0.0.1", http_server.server_port), ("127.0.0.1", https.server_port)):
            return paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED
        self.destinations[channel_id] = destination
        return paramiko.OPEN_SUCCEEDED


def forward(channel, destination):
    try:
        with socket.create_connection(destination) as target:
            while True:
                ready, _, _ = select.select([channel, target], [], [], 10)
                for source in ready:
                    data = source.recv(65536)
                    if not data:
                        return
                    (target if source is channel else channel).sendall(data)
    finally:
        channel.close()


def serve_connection(connection, host_key):
    transport = paramiko.Transport(connection)
    transport.add_server_key(host_key)
    server = SshServer()
    try:
        transport.start_server(server=server)
        while transport.is_active():
            channel = transport.accept(1)
            if channel:
                threading.Thread(target=forward, args=(channel, server.destinations[channel.chanid]), daemon=True).start()
    except (paramiko.SSHException, EOFError, OSError):
        pass
    finally:
        transport.close()


def listen(host_key):
    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    listener.listen()

    def run():
        while True:
            connection, _ = listener.accept()
            threading.Thread(target=serve_connection, args=(connection, host_key), daemon=True).start()

    threading.Thread(target=run, daemon=True).start()
    return {"port": listener.getsockname()[1], "key": host_key.get_name() + " " + host_key.get_base64()}


host_ed = ed25519.Ed25519PrivateKey.generate()
(directory / "host-ed").write_bytes(host_ed.private_bytes(
    serialization.Encoding.PEM, serialization.PrivateFormat.OpenSSH, serialization.NoEncryption()))
servers = [
    listen(paramiko.Ed25519Key.from_private_key_file(str(directory / "host-ed"))),
    listen(paramiko.ECDSAKey.generate()),
    listen(paramiko.RSAKey.generate(2048)),
]
print(json.dumps({"servers": servers, "http": http_server.server_port, "https": https.server_port}), flush=True)
threading.Event().wait()
