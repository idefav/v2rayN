#!/usr/bin/env python3
"""Prefill the pinned module cache; retry transport failures, never build/test failures."""
import os
import re
import subprocess
import sys
import time

ATTEMPTS = 3
ATTEMPT_TIMEOUT = 180
HTTP2_ERROR = re.compile(r"(?:stream error:|http2:)[^\n]*(?:INTERNAL_ERROR|REFUSED_STREAM|GOAWAY)", re.I)
TRANSIENT_ERROR = re.compile(
    r"unexpected EOF|:\s*EOF(?:\s|$)|connection reset|connection aborted|"
    r"i/o timeout|TLS handshake timeout|context deadline exceeded|"
    r"temporary failure in name resolution|temporary network error|"
    r"(?:^|\s)(?:429|500|502|503|504)(?:\s|$)", re.I)
PERMANENT_ERROR = re.compile(
    r"checksum mismatch|SECURITY ERROR|unknown revision|"
    r"certificate (?:has expired|signed by unknown authority)|"
    r"module declares its path|malformed module path|authentication required|"
    r"(?:^|\s)(?:401|403|404|410)(?:\s|$)", re.I)


def output_text(value):
    return value.decode("utf-8", errors="replace") if isinstance(value, bytes) else value or ""


def without_http2(environment):
    # Change only this download subprocess, preserving all other Go settings.
    result = environment.copy()
    flags = [flag for flag in result.get("GODEBUG", "").split(",")
             if flag and not flag.startswith("http2client=")]
    result["GODEBUG"] = ",".join([*flags, "http2client=0"])
    return result


def main():
    environment = os.environ.copy()
    for attempt in range(1, ATTEMPTS + 1):
        print(f"Downloading pinned Go modules (attempt {attempt}/{ATTEMPTS})", flush=True)
        timed_out = False
        try:
            result = subprocess.run(["go", "mod", "download"], env=environment,
                                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                    text=True, errors="replace", timeout=ATTEMPT_TIMEOUT)
            status, output = result.returncode, output_text(result.stdout)
        except subprocess.TimeoutExpired as error:
            status, output, timed_out = 124, output_text(error.stdout), True
            output += f"\nGo module download exceeded {ATTEMPT_TIMEOUT} seconds.\n"
        except OSError as error:
            print(f"Cannot run go mod download: {error}", file=sys.stderr)
            return 127
        sys.stdout.write(output)
        sys.stdout.flush()
        if status == 0:
            return 0
        if status < 0:  # A cancelled/killed command must not be restarted.
            return 128 - status
        if PERMANENT_ERROR.search(output):
            return status
        http2_failure = HTTP2_ERROR.search(output)
        if not (timed_out or http2_failure or TRANSIENT_ERROR.search(output)) or attempt == ATTEMPTS:
            return status
        if http2_failure:
            environment = without_http2(environment)
            print("Retrying module download over HTTP/1.1 after an HTTP/2 transport error.", flush=True)
        delay = 2 ** attempt
        print(f"Transient module download failure; retrying in {delay}s.", flush=True)
        time.sleep(delay)
    return 1


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)
