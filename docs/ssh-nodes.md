# SSH nodes

SSH is a native profile type in both the Avalonia desktop client (macOS/Linux)
and the Windows WPF client. Select **Add SSH server**, enter the SSH endpoint and
username, then choose password or private-key-file authentication. An encrypted
private key may also require its passphrase.

The editor retrieves the server public key on the first save. Verify the displayed
SHA256 fingerprint through a trusted channel before accepting. Fetching a key
does not authenticate the server by itself. The probe stops during key exchange,
before sending any login credentials. A pasted OpenSSH-format public key can be
accepted with **Confirm pasted public key** instead.

The accepted key is pinned to the address and port. Changing either requires a
new confirmation; changing the login username does not. Key mismatches fail
closed. Use **Fetch / refresh public key** and explicitly confirm a legitimate
server key rotation. No existing pin is silently replaced.

Local ports, routing, statistics and system proxy integration use v2rayN's normal
settings. SSH always selects sing-box, including when the default core is Xray.
For a policy group or proxy chain containing SSH, select sing-box for the group;
Xray groups report an unsupported-protocol error.

SSH forwards TCP only. Enabling TUN does not add UDP-over-SSH support. UDP traffic
routed to SSH fails rather than falling back to a direct connection. Simple
proxied UDP DNS servers are generated as TCP DNS servers at the same address and
port. Proxied custom UDP/QUIC/HTTP3 DNS must be changed to TCP/TLS/HTTPS.

Private-key contents are not copied into profiles. Only the local file path is
saved. Passwords/passphrases follow existing profile-storage and backup behavior;
they are excluded from SSH editor error messages. Paths must exist on the machine
running the core. SSH Agent, SSH share URIs, SSH certificates, custom UDP channels
and automatic migration of custom JSON profiles are not supported.

## Development and verification

Requires .NET 10 and the repository's GlobalHotKeys submodule:

Full-config integration was verified with sing-box 1.14.2. The current repository
already emits newer DNS fields (such as `preferred_by`) which sing-box 1.13.11
does not accept; use the core version matching this source checkout.

```sh
git submodule update --init --recursive
dotnet build v2rayN/v2rayN.Desktop/v2rayN.Desktop.csproj
dotnet build v2rayN/v2rayN/v2rayN.csproj -p:EnableWindowsTargeting=true
dotnet run --project v2rayN/ServiceLib.Tests -- --treenode-filter '/*/*/Ssh*/*'
```

The loopback integration test is skipped unless both environment variables are
provided. It starts a disposable Python SSH/HTTP/HTTPS fixture, generates its own
keys, restricts forwarding to its HTTP ports, and stops all child processes when
finished. Install `paramiko` in a temporary virtual environment, then run:

```sh
SSH_TEST_PYTHON=/absolute/path/to/venv/bin/python \
SSH_TEST_SINGBOX=/absolute/path/to/sing-box \
dotnet run --project v2rayN/ServiceLib.Tests -- --treenode-filter '/*/*/Ssh*/*'
```

Coverage includes endpoint-bound trust, explicit acceptance/cancellation,
Ed25519/ECDSA/RSA key exchange, no authentication during probing, cancellation of
a stalled handshake, password and encrypted/unencrypted key authentication,
HTTP/HTTPS forwarding, invalid credentials and changed host keys, profile
persistence, DNS policy, and sing-box configuration checks.

Run development builds in a writable, separate output directory with
`V2RAYN_LOCAL_APPLICATION_DATA_V2=0` to keep data separate from the installed
application. Cross-compilation alone does not verify Windows/Linux runtime UI.
