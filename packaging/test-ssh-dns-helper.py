"""Isolated transaction fault tests. Never executes system commands or touches /etc."""
import copy
import importlib.util
from pathlib import Path
import stat
import tempfile
import time
import unittest
from types import SimpleNamespace
from unittest.mock import patch

path = Path(__file__).parents[1] / "v2rayN/ServiceLib/Sample/ssh-dns-helper.py"
spec = importlib.util.spec_from_file_location("dns_helper", path)
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)


class DnsScopes(unittest.TestCase):
    def test_existing_resolver_global_alias_is_not_a_second_upstream(self):
        helper.check_dns_scopes("Global: 127.0.0.1\nLink 2 (eth0): 127.0.0.1\n", "eth0", ["127.0.0.1"], True)

    def test_empty_global_scope_accepts_fresh_deployment(self):
        helper.check_dns_scopes("Global:\nLink 2 (eth0): 100.100.2.136 100.100.2.138\n",
                                "eth0", ["100.100.2.136", "100.100.2.138"], False)

    def test_unmanaged_loopback_and_external_global_dns_still_fail_closed(self):
        cases = [("127.0.0.1", ["127.0.0.1"], False),
                 ("100.100.2.136", ["100.100.2.136"], False),
                 ("100.100.2.136", ["127.0.0.1"], True),
                 ("127.0.0.1 1.1.1.1", ["127.0.0.1"], True),
                 ("::1", ["127.0.0.1"], True)]
        for global_dns, link_dns, existing in cases:
            with self.subTest(global_dns=global_dns, existing=existing), self.assertRaisesRegex(helper.Failure, "E_SPLIT_DNS_UNSUPPORTED"):
                helper.check_dns_scopes(f"Global: {global_dns}\nLink 2 (eth0): {' '.join(link_dns)}", "eth0", link_dns, existing)

    def test_other_link_is_not_allowed_by_the_global_alias_exception(self):
        for other in ("127.0.0.1", "1.1.1.1"):
            with self.subTest(other=other), self.assertRaisesRegex(helper.Failure, "E_SPLIT_DNS_UNSUPPORTED"):
                helper.check_dns_scopes(f"Global: 127.0.0.1\nLink 2 (eth0): 127.0.0.1\nLink 3 (eth1): {other}",
                                        "eth0", ["127.0.0.1"], True)

    def test_wrapped_extra_upstream_is_not_silently_ignored(self):
        with self.assertRaisesRegex(helper.Failure, "E_SPLIT_DNS_UNSUPPORTED"):
            helper.check_dns_scopes("Global: 127.0.0.1\n        1.1.1.1\nLink 2 (eth0): 127.0.0.1", "eth0", ["127.0.0.1"], True)

    def test_scope_snapshot_change_or_malformed_output_is_rejected(self):
        with self.assertRaisesRegex(helper.Failure, "E_CONFIGURATION_DRIFT"):
            helper.check_dns_scopes("Global:\nLink 2 (eth0): 1.1.1.1", "eth0", ["127.0.0.1"], True)
        with self.assertRaisesRegex(helper.Failure, "E_RESOLVED_FORMAT"):
            helper.check_dns_scopes("Unexpected output\nLink 2 (eth0): 127.0.0.1", "eth0", ["127.0.0.1"], True)


class LegacySnapshot(unittest.TestCase):
    def setUp(self):
        self.data = (Path(__file__).parent / "fixtures/ssh-dns-legacy-unbound.conf").read_bytes()
        self.assertEqual(helper.digest(self.data), helper.LEGACY_CONFIG_SHA256)

    def read(self, path, uid=501, gid=50, mode=0o644, data=None):
        info = SimpleNamespace(st_uid=uid, st_gid=gid, st_mode=stat.S_IFREG | mode)
        with patch.object(helper, "safe_parent"), patch.object(Path, "exists", return_value=True), \
                patch.object(Path, "stat", return_value=info), patch.object(Path, "read_bytes", return_value=self.data if data is None else data):
            return helper.snapshot(path)

    def test_pinned_legacy_archive_metadata_is_accepted_without_changing_the_file(self):
        self.assertEqual(self.read(helper.LEGACY_CONFIG)["hash"], helper.LEGACY_CONFIG_SHA256)
        self.assertEqual(self.read(helper.LEGACY_CONFIG, uid=0, gid=0)["hash"], helper.LEGACY_CONFIG_SHA256)

    def test_exception_does_not_accept_other_files_owners_permissions_or_content(self):
        for path, changes in ((Path("/etc/systemd/system/ssh-egress-dns.service"), {}),
                              (Path("/root/ssh-dns-backup-fixture/unbound.conf"), {}),
                              (helper.LEGACY_CONFIG, {"uid": 1000}),
                              (helper.LEGACY_CONFIG, {"gid": 1000}),
                              (helper.LEGACY_CONFIG, {"mode": 0o664}),
                              (helper.LEGACY_CONFIG, {"mode": 0o600}),
                              (helper.LEGACY_CONFIG, {"data": self.data + b"# drift\n"})):
            with self.subTest(path=path, changes=changes), self.assertRaisesRegex(helper.Failure, "E_FILE_OWNER"):
                self.read(path, **changes)

    def test_archive_exception_still_requires_root_owned_parents(self):
        with patch.object(helper, "safe_parent", side_effect=helper.Failure("E_PATH_OWNER")), \
                self.assertRaisesRegex(helper.Failure, "E_PATH_OWNER"):
            helper.snapshot(helper.LEGACY_CONFIG)


class Transactions(unittest.TestCase):
    def setUp(self):
        self.saved = []
        self.calls = []
        self.state = {"id": "a" * 32, "phase": "awaiting_verification", "deadline": int(time.time()) + 600,
                      "service": helper.UNIT, "files": {}, "resolverStartIntent": True,
                      "original": {"interface": "eth0", "dns": ["192.0.2.53"], "domains": [],
                                   "defaultRoute": ["yes"], "exit": "203.0.113.1",
                                   "hostId": "b" * 64, "sourceFiles": {}}}
        self.patches = [
            patch.object(helper, "run", side_effect=lambda args, **kw: self.calls.append(args) or ""),
            patch.object(helper, "save", side_effect=lambda s: self.saved.append(copy.deepcopy(s))),
            patch.object(helper, "link_value", side_effect=lambda action, link: ["127.0.0.1"] if action == "dns" else []),
        ]
        for item in self.patches:
            item.start()

    def tearDown(self):
        for item in reversed(self.patches):
            item.stop()

    def test_rollback_restores_dns_before_stopping_service_and_retains_backup(self):
        helper.rollback(self.state)
        self.assertIn(["resolvectl", "dns", "eth0", "192.0.2.53"], self.calls)
        stop = next(i for i, cmd in enumerate(self.calls) if "disable" in cmd and helper.UNIT in cmd)
        self.assertLess(next(i for i, cmd in enumerate(self.calls) if cmd[0] == "getent"), stop)
        self.assertEqual(self.saved[-1]["phase"], "rolled_back")
        self.assertEqual(self.saved[-1]["original"]["dns"], ["192.0.2.53"])
        self.assertFalse(any("restart" in cmd or "reconfigure" in cmd or "apply" in cmd for cmd in self.calls))

    def test_drift_prevents_all_mutations(self):
        self.state["files"]["/etc/netplan/fixture.yaml"] = {"before": None, "applied": "managed"}
        with patch.object(helper, "snapshot", return_value={"hash": "someone-else"}):
            with self.assertRaisesRegex(helper.Failure, "E_CONFIGURATION_DRIFT"):
                helper.rollback(self.state)
        self.assertEqual(self.calls, [])

    def test_failure_restoring_dns_never_stops_replacement(self):
        with patch.object(helper, "run", side_effect=helper.Failure("E_COMMAND_RESOLVECTL")):
            with self.assertRaises(helper.Failure):
                helper.rollback(self.state)
        self.assertEqual(self.saved, [])
        self.assertEqual(self.state["phase"], "awaiting_verification")

    def test_expired_or_unverified_transaction_cannot_commit(self):
        self.state["deadline"] = 1
        with self.assertRaisesRegex(helper.Failure, "E_TRANSACTION_EXPIRED"):
            helper.commit(self.state, {})
        self.state["deadline"] = int(time.time()) + 600
        with self.assertRaisesRegex(helper.Failure, "E_VERIFICATION_REQUIRED"):
            helper.commit(self.state, {"verifiedExit": "203.0.113.1", "verifiedDns": ["203.0.113.2"]})
        self.assertEqual(self.saved, [])

    def test_commit_persists_before_disarming(self):
        helper.commit(self.state, {"verifiedExit": "203.0.113.1", "verifiedDns": ["203.0.113.1"]})
        self.assertEqual(self.saved[0]["phase"], "committed")
        self.assertTrue(any(helper.TIMER + ".timer" in cmd for cmd in self.calls))

    def test_second_write_failure_leaves_both_restore_intents(self):
        with patch.object(helper, "snapshot", return_value=None), patch.object(helper, "atomic", side_effect=[None, OSError("disk")]):
            helper.write_managed(self.state, Path("/etc/first"), b"one")
            with self.assertRaises(OSError):
                helper.write_managed(self.state, Path("/etc/second"), b"two")
        self.assertEqual(set(self.saved[-1]["files"]), {"/etc/first", "/etc/second"})
        self.assertEqual(self.saved[-1]["original"]["dns"], ["192.0.2.53"])

    def test_dns_only_comparison_rejects_route_or_address_change(self):
        original = "[Network]\nDHCP=ipv4\nAddress=192.0.2.1/24\nDNS=192.0.2.53\n"
        replacement = original.replace("DNS=192.0.2.53", "DNS=127.0.0.1\nDomains=~.")
        self.assertEqual(helper.network_without_dns(original), helper.network_without_dns(replacement))
        self.assertNotEqual(helper.network_without_dns(original), helper.network_without_dns(replacement.replace(".1/24", ".2/24")))

    def test_duplicate_rollback_is_noop(self):
        self.state["phase"] = "rolled_back"
        helper.rollback(self.state)
        self.assertEqual(self.calls, [])

    def test_other_transaction_cannot_activate(self):
        with patch.object(helper, "read_state", return_value=self.state):
            with self.assertRaisesRegex(helper.Failure, "E_TRANSACTION_ID"):
                helper.dispatch({"version": 1, "action": "activate", "transactionId": "c" * 32})
        self.assertEqual(self.calls, [])

    def test_candidate_rejects_arbitrary_url_before_network(self):
        for name in ("localhost", "http://169.254.169.254/", "-o/etc/passwd", "example.com"):
            with self.assertRaisesRegex(helper.Failure, "E_QUERY_NAME"):
                helper.candidate({"name": name})
        self.assertEqual(self.calls, [])

    def test_adoption_rejects_untrusted_transaction_path(self):
        with self.assertRaisesRegex(helper.Failure, "E_TRANSACTION_ID"):
            helper.adopt({"transactionId": "../../etc/untrusted"})
        self.assertEqual(self.saved, [])
        self.assertEqual(self.calls, [])

    def test_legacy_config_drift_prevents_rollback(self):
        self.state["legacyConfig"] = "trusted-config"
        with patch.object(helper, "snapshot", return_value={"hash": "changed-config"}):
            with self.assertRaisesRegex(helper.Failure, "E_CONFIGURATION_DRIFT"):
                helper.rollback(self.state)
        self.assertEqual(self.calls, [])

    def test_preparation_failure_never_stops_an_unowned_service(self):
        self.state.pop("resolverStartIntent")
        self.state["phase"] = "preparing"
        helper.rollback(self.state)
        self.assertFalse(any(cmd[:2] in (["systemctl", "stop"], ["systemctl", "disable"]) and helper.UNIT in cmd
                             for cmd in self.calls))
        self.assertEqual(self.saved[-1]["phase"], "rolled_back")

    def test_validated_manual_adoption_records_original_backup_without_restart(self):
        backup = Path("/root/ssh-dns-backup-fixture")
        current = copy.deepcopy(self.state["original"])
        current.update(phase="legacy", token="checked", networkFile="/run/systemd/network/10-netplan-eth0.network")
        config_hash = "e65eefc210431fb133e9a43436a69b35d406fac69f153d85d1a4e5468fa86f94"
        hashes = {
            "/opt/ssh-egress-dns/config/unbound.conf": config_hash,
            "/etc/systemd/system/ssh-egress-dns.service": "1837df5e878f5c6f9ecbc9d65e660b2ef2b6ab42f51c5ed563a1a70b829f1520",
            "/etc/netplan/90-ssh-egress-dns.yaml": "8efc34644c11274eaa3f75a8a58b959b3a608e50bd457268c6d7a44b3c0f4658",
            "/etc/systemd/network/10-netplan-eth0.network.d/90-ssh-egress-dns.conf": "34bb81a9629edcc09f260146f5757f9e003c94ee33089d161c442a0749b4c39e",
        }
        def glob(path, pattern):
            return [backup] if str(path) == "/root" else [backup / "netplan/50-cloud-init.yaml"]
        with patch.object(helper, "inspect", return_value=current), patch.object(helper, "safe_parent"), \
                patch.object(Path, "glob", glob), patch.object(Path, "stat", return_value=SimpleNamespace(st_uid=0, st_mode=0o700)), \
                patch.object(Path, "read_text", lambda p: "Link 2 (eth0): 100.100.2.136 100.100.2.138" if p.name == "dns.txt" else "Link 2 (eth0):"), \
                patch.object(helper, "snapshot", side_effect=lambda p: {"hash": hashes.get(str(p), "baseline"), "data": "", "mode": 0o600}):
            state = helper.adopt({"transactionId": "a" * 32, "planToken": "checked", "expectedExit": "203.0.113.1"})
        self.assertEqual(state["phase"], "committed")
        self.assertEqual(state["original"]["dns"], ["100.100.2.136", "100.100.2.138"])
        self.assertEqual(state["legacyConfig"], config_hash)
        self.assertFalse(any(cmd[1] in ("start", "stop", "restart", "enable", "disable") for cmd in self.calls))


if __name__ == "__main__":
    unittest.main()
