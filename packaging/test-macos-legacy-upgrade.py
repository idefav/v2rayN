#!/usr/bin/env python3
"""Run published legacy updaters and require automatic startup provenance in CI only."""
import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import plistlib
import shutil
import signal
import subprocess
import tempfile
import time
import urllib.parse
import zipfile


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def extract(archive, destination):
    with zipfile.ZipFile(archive) as z:
        for entry in z.infolist():
            parts = Path(entry.filename).parts
            if len(parts) < 2:
                continue
            assert '..' not in parts and not Path(entry.filename).is_absolute()
            target = destination.joinpath(*parts[1:])
            if entry.is_dir():
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(z.read(entry))
                target.chmod((entry.external_attr >> 16) & 0o777 or 0o644)


def processes(executable):
    # ps renders non-ASCII paths according to its locale and may abbreviate them.
    # The fixture's own UID can read its exact executable through proc_pidpath.
    libproc = ctypes.CDLL('/usr/lib/libproc.dylib')
    libproc.proc_pidpath.argtypes = [ctypes.c_int, ctypes.c_void_p, ctypes.c_uint32]
    output = subprocess.check_output(['/bin/ps', '-axo', 'pid='], text=True)
    found = []
    for row in output.split():
        pid = int(row)
        path = ctypes.create_string_buffer(4096)
        if libproc.proc_pidpath(pid, path, len(path)) > 0:
            if os.path.realpath(os.fsdecode(path.value)) == os.path.realpath(executable):
                found.append(pid)
    return found


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--candidate', type=Path, required=True)
    parser.add_argument('--legacy', type=Path)
    parser.add_argument('--results', type=Path, required=True)
    args = parser.parse_args()
    assert os.environ.get('GITHUB_ACTIONS') == 'true' and os.uname().machine == 'arm64', 'CI-only real GUI fixture'
    args.results.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(args.candidate) as z:
        assert not any(Path(n).name in ('AmazTool', 'AmazTool.exe') for n in z.namelist())
        mains = [n for n in z.namelist() if Path(n).name == 'v2rayN']
        assert len(mains) == 1
        expected = hashlib.sha256(z.read(mains[0])).hexdigest().upper()
    before_proxy = subprocess.check_output(['/usr/sbin/scutil', '--proxy'])
    root = Path(tempfile.mkdtemp(prefix='旧版升级 space-'))
    executable = root / '测试.app/Contents/MacOS/v2rayN'
    install = executable.parent
    install.mkdir(parents=True)
    helper_process = None
    try:
        extract(args.legacy or args.candidate, install)
        (install / 'NotStoreConfigHere.txt').unlink(missing_ok=True)
        helper_name = 'AmazTool' if args.legacy else 'AmazToolV2'
        for name in ('v2rayN', helper_name):
            (install / name).chmod(0o755)
        with (install.parent / 'Info.plist').open('wb') as f:
            plistlib.dump({'CFBundleExecutable': 'v2rayN', 'CFBundleIdentifier': 'org.v2rayn.upgrade-fixture',
                          'CFBundleName': 'UpgradeFixture', 'CFBundlePackageType': 'APPL'}, f)
        configs = install / 'guiConfigs'
        configs.mkdir(exist_ok=True)
        (configs / 'guiNConfig.json').write_text(json.dumps({
            'SystemProxyItem': {'SysProxyType': 2}, 'TunModeItem': {'EnableTun': False},
            'IndexId': '', 'IsFirstRun': False,
        }))
        record = {'SchemaVersion': 1, 'Repository': 'idefav/v2rayN', 'Tag': 'v7.25.6-ssh-preview.999',
                  'Version': 'v7.25.6-ssh-preview.999', 'Sha256': expected, 'Executable': 'v2rayN'}
        (configs / 'pending-app-update.json').write_text(json.dumps(record))
        candidate = root / '候选 package.zip'
        shutil.copyfile(args.candidate, candidate)
        original_helper = digest(install / helper_name)
        with (args.results / 'legacy-helper.log').open('wb') as log:
            # Exactly the existing URI-escaped single-argument upgrade protocol.
            if args.legacy:
                helper_process = subprocess.Popen([str(install / 'AmazTool'), urllib.parse.quote(str(candidate), safe='')],
                                                  cwd=root, stdout=log, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL)
            else:
                env = dict(os.environ, V2RAYN_TEST_REAL_UPGRADE_DIR=str(install), V2RAYN_TEST_REAL_UPGRADE_ZIP=str(candidate))
                helper_process = subprocess.Popen(['dotnet', 'test', '--project', 'v2rayN/ServiceLib.Tests', '-c', 'Release',
                    '--', '--treenode-filter', '/*/*/AppUpgradeIntegrationTests/RealApplicationHandoffOnDisposableMacRunner'],
                    env=env, stdout=log, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL)
            deadline = time.monotonic() + 90
            installed = configs / 'installed-app-update.json'
            while time.monotonic() < deadline:
                if installed.exists() and processes(executable):
                    break
                time.sleep(.2)
            assert installed.exists(), 'Legacy helper did not automatically start and confirm the candidate app'
            assert processes(executable), 'Candidate application is not running'
            actual_record = json.loads(installed.read_text())
            (args.results / 'installed-app-update.json').write_text(json.dumps(actual_record, indent=2))
            assert actual_record == record, (actual_record, record)
            assert digest(executable) == expected
            assert digest(install / helper_name) == original_helper, 'Helper bytes did not match expected package'
            helper_process.wait(timeout=15)
            assert helper_process.returncode == 0
        (args.results / 'verified.json').write_text(json.dumps({'legacy': args.legacy.name if args.legacy else 'upgrade-v2',
            'running_sha256': digest(executable), 'legacy_helper_unchanged': True, 'installed_record': record}, indent=2))
    finally:
        for pid in processes(executable):
            os.kill(pid, signal.SIGTERM)
        time.sleep(2)
        for pid in processes(executable):
            os.kill(pid, signal.SIGKILL)
        if helper_process and helper_process.poll() is None:
            helper_process.kill()
            helper_process.wait(timeout=5)
        if (install / 'guiLogs').exists():
            shutil.copytree(install / 'guiLogs', args.results / 'app-logs', dirs_exist_ok=True)
        for log in install.glob('*.log'):
            shutil.copyfile(log, args.results / log.name)
        shutil.rmtree(root)
        assert subprocess.check_output(['/usr/sbin/scutil', '--proxy']) == before_proxy, 'System proxy changed'


if __name__ == '__main__':
    main()
