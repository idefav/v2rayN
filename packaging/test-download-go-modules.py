"""Offline regression tests for the production dependency download entry point."""
import contextlib
import importlib.util
import io
import os
from pathlib import Path
import subprocess
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("download_go_modules", Path(__file__).with_name("download-go-modules.py"))
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)


def result(status=0, output=""):
    return subprocess.CompletedProcess(["go", "mod", "download"], status, output)


class ModuleDownloadTests(unittest.TestCase):
    def run_download(self, results):
        output = io.StringIO()
        with patch.object(helper.subprocess, "run", side_effect=results) as run, \
                patch.object(helper.time, "sleep") as sleep, contextlib.redirect_stdout(output):
            status = helper.main()
        for call in run.call_args_list:
            self.assertEqual(call.args[0], ["go", "mod", "download"])
            self.assertEqual(call.kwargs["timeout"], 180)
        return status, run, sleep, output.getvalue()

    def test_success_does_not_retry_or_change_security_settings(self):
        with patch.dict(os.environ, {"GOPROXY": "https://proxy.golang.org,direct", "GOSUMDB": "sum.golang.org",
                                     "GODEBUG": "http2client=1", "GOFLAGS": "-mod=readonly"}):
            status, run, sleep, _ = self.run_download([result()])
            self.assertEqual(status, 0)
            self.assertEqual(run.call_count, 1)
            self.assertFalse(sleep.called)
            self.assertEqual(run.call_args.kwargs["env"], dict(os.environ))

    def test_exact_ci_http2_failure_retries_without_mutating_parent_environment(self):
        failure = ('go: github.com/sagernet/tailscale@v1.102.1-sing-box-1.14-mod.5: read '
                   '"https://proxy.golang.org/github.com/sagernet/tailscale/@v/v1.102.1-sing-box-1.14-mod.5.mod": '
                   'stream error: stream ID 183; INTERNAL_ERROR; received from peer\r\n')
        with patch.dict(os.environ, {"GODEBUG": "gctrace=1,http2client=1", "GOSUMDB": "sum.golang.org"}):
            original = dict(os.environ)
            status, run, sleep, output = self.run_download([result(1, failure), result()])
            self.assertEqual(status, 0)
            self.assertEqual(run.call_count, 2)
            self.assertIn(failure, output)
            self.assertIn("HTTP/1.1", output)
            self.assertEqual(run.call_args_list[0].kwargs["env"], original)
            retry_env = run.call_args_list[1].kwargs["env"]
            self.assertEqual(retry_env["GODEBUG"], "gctrace=1,http2client=0")
            self.assertEqual({k: v for k, v in retry_env.items() if k != "GODEBUG"},
                             {k: v for k, v in original.items() if k != "GODEBUG"})
            self.assertEqual(dict(os.environ), original)
            sleep.assert_called_once_with(2)

    def test_other_transient_failures_retry_without_forcing_http1(self):
        for failure in ["unexpected EOF", "read: EOF", "i/o timeout", "TLS handshake timeout",
                        "connection reset by peer", "context deadline exceeded", "429 Too Many Requests",
                        "500 Internal Server Error", "502 Bad Gateway", "503 Service Unavailable", "504 Gateway Timeout"]:
            with self.subTest(failure=failure):
                status, run, _, _ = self.run_download([result(1, failure), result()])
                self.assertEqual(status, 0)
                self.assertEqual(run.call_args_list[0].kwargs["env"], run.call_args_list[1].kwargs["env"])

    def test_retries_are_bounded_and_last_failure_is_reported(self):
        status, run, sleep, output = self.run_download([result(1, "503 first\n"), result(1, "503 second\n"), result(9, "503 final\n")])
        self.assertEqual(status, 9)
        self.assertEqual(run.call_count, 3)
        self.assertEqual([call.args[0] for call in sleep.call_args_list], [2, 4])
        self.assertIn("503 final", output)

    def test_integrity_authentication_and_persistent_errors_fail_immediately(self):
        for failure in ["checksum mismatch", "SECURITY ERROR\n503 Service Unavailable", "unknown revision v9.0.0",
                        "x509: certificate signed by unknown authority", "module declares its path as: different/path",
                        "401 Unauthorized", "403 Forbidden", "404 Not Found", "410 Gone",
                        "invalid version", "syntax error", "FAIL test regression"]:
            with self.subTest(failure=failure):
                status, run, sleep, output = self.run_download([result(1, failure)])
                self.assertEqual(status, 1)
                self.assertEqual(run.call_count, 1)
                self.assertFalse(sleep.called)
                self.assertIn(failure, output)

    def test_timeout_is_bounded_and_retries_preserve_partial_diagnostics(self):
        timeouts = [subprocess.TimeoutExpired(["go"], 180, output=b"partial download\n")] * 3
        status, run, sleep, output = self.run_download(timeouts)
        self.assertEqual(status, 124)
        self.assertEqual(run.call_count, 3)
        self.assertEqual(sleep.call_count, 2)
        self.assertIn("partial download", output)

    def test_interruption_is_not_retried(self):
        status, run, sleep, _ = self.run_download([result(-15, "503 interrupted")])
        self.assertEqual(status, 143)
        self.assertEqual(run.call_count, 1)
        self.assertFalse(sleep.called)
        with patch.object(helper.subprocess, "run", side_effect=KeyboardInterrupt) as run, \
                patch.object(helper.time, "sleep") as sleep, contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaises(KeyboardInterrupt):
                helper.main()
            self.assertEqual(run.call_count, 1)
            self.assertFalse(sleep.called)

    def test_missing_go_is_not_retried(self):
        with contextlib.redirect_stderr(io.StringIO()):
            status, run, sleep, _ = self.run_download([FileNotFoundError("go")])
        self.assertEqual(status, 127)
        self.assertEqual(run.call_count, 1)
        self.assertFalse(sleep.called)


if __name__ == "__main__":
    unittest.main()
