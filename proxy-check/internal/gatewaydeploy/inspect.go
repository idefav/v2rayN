//go:build linux

package gatewaydeploy

import (
	"context"
	"encoding/json"
	"errors"
	"net"
	"os"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"syscall"
)

func secureDirectory(path string) error {
	if path == "/" {
		return nil
	}
	if err := secureDirectory(filepath.Dir(path)); err != nil {
		return err
	}
	if err := os.Mkdir(path, 0755); err != nil {
		if !os.IsExist(err) {
			return err
		}
	} else if err = os.Chmod(path, 0755); err != nil {
		return err
	}
	s, err := os.Lstat(path)
	if err != nil {
		return err
	}
	stat, ok := s.Sys().(*syscall.Stat_t)
	if !ok || !s.IsDir() || s.Mode()&0022 != 0 || stat.Uid != 0 {
		return errors.New("E_UNSAFE_INSTALL_PATH")
	}
	return nil
}

func safeRegular(path string) error {
	s, e := os.Lstat(path)
	if e != nil {
		return e
	}
	st, ok := s.Sys().(*syscall.Stat_t)
	if !ok || !s.Mode().IsRegular() || st.Uid != 0 || s.Mode()&0022 != 0 {
		return errors.New("E_UNSAFE_FILE")
	}
	return nil
}

func portFree(port int) bool {
	l, e := net.Listen("tcp4", net.JoinHostPort("127.0.0.1", strconv.Itoa(port)))
	if e != nil {
		return false
	}
	l.Close()
	return true
}

func (m *Manager) inspect(ctx context.Context, r Request) (*Plan, error) {
	if runtime.GOARCH != "amd64" && runtime.GOARCH != "arm64" {
		return nil, errors.New("E_ARCH_UNSUPPORTED")
	}
	if _, e := os.Stat("/run/systemd/system"); e != nil {
		return nil, errors.New("E_SYSTEMD_REQUIRED")
	}
	if _, e := m.run(ctx, "ss", "-H", "-lnt"); e != nil {
		return nil, errors.New("E_SS_REQUIRED")
	}
	if r.BinarySHA256 != "" {
		helper, e := os.Executable()
		if e != nil {
			return nil, errors.New("E_HELPER_PATH")
		}
		bin := filepath.Join(filepath.Dir(helper), "udpgw-server")
		if safeRegular(bin) != nil {
			return nil, errors.New("E_UPLOAD_UNSAFE")
		}
		sha, e := fileHash(bin)
		if e != nil || sha != r.BinarySHA256 {
			return nil, errors.New("E_BINARY_CHECKSUM")
		}
		output, e := m.run(ctx, bin, "--version")
		if e != nil || !strings.Contains(string(output), Version) {
			return nil, errors.New("E_BINARY_INCOMPATIBLE")
		}
	}
	p := &Plan{Architecture: runtime.GOARCH}
	var existing Installation
	err := readJSON(filepath.Join(m.root, "installation.json"), &existing)
	if err == nil {
		if !identifier.MatchString(existing.InstallationID) || existing.Service != Service || existing.Port < 7300 || existing.Port > 7399 || !digestPattern.MatchString(existing.BinarySHA256) {
			return nil, errors.New("E_MANAGED_IDENTITY")
		}
		if e := safeRegular(m.unit); e != nil {
			return nil, errors.New("E_MANAGED_UNIT")
		}
		unit, e := os.ReadFile(m.unit)
		if e != nil || !strings.HasPrefix(string(unit), "# v2rayN UDPGW "+existing.InstallationID+"\n") {
			return nil, errors.New("E_MANAGED_UNIT")
		}
		if string(unit) != m.serviceUnit(existing) {
			return nil, errors.New("E_MANAGED_UNIT_CHANGED")
		}
		current, e := os.Readlink(filepath.Join(m.root, "current"))
		if e != nil || !validVersionPath(current) {
			return nil, errors.New("E_MANAGED_CURRENT")
		}
		digest, e := fileHash(filepath.Join(m.root, current, "udpgw-server"))
		if e != nil || digest != existing.BinarySHA256 {
			return nil, errors.New("E_MANAGED_BINARY")
		}
		p.Existing = &existing
		p.Port = existing.Port
	} else if !os.IsNotExist(err) {
		return nil, errors.New("E_MANAGED_RECORD")
	} else {
		if _, e := os.Lstat(filepath.Join(m.root, "current")); !os.IsNotExist(e) {
			return nil, errors.New("E_UNKNOWN_INSTALLATION")
		}
		if _, e := os.Lstat(m.unit); !os.IsNotExist(e) {
			return nil, errors.New("E_UNKNOWN_SERVICE")
		}
		fragment, e := m.systemctl(ctx, "show", "--property=FragmentPath", "--value", Service)
		if e == nil && strings.TrimSpace(string(fragment)) != "" {
			return nil, errors.New("E_UNKNOWN_SERVICE")
		}
		for port := 7300; port <= 7399; port++ {
			if portFree(port) {
				p.Port = port
				break
			}
		}
		if p.Port == 0 {
			return nil, errors.New("E_PORT_RANGE_BUSY")
		}
	}
	ssh, e := m.inspectSSH(ctx, r)
	if e != nil {
		return nil, e
	}
	p.SSH = *ssh
	if !tcpPermitted(p.SSH.PermitOpen, "127.0.0.1", p.Port) {
		return nil, errors.New("E_TCP_PERMIT_OPEN")
	}
	endpoint := net.JoinHostPort("127.0.0.1", strconv.Itoa(p.Port))
	p.SSH.NeedsBypass = p.SSH.ForwardProxy
	for _, v := range p.SSH.BypassEndpoints {
		if v == endpoint {
			p.SSH.NeedsBypass = false
		}
	}
	if p.SSH.NeedsBypass && !p.SSH.BypassSupported {
		return nil, errors.New("E_SSHD_UPGRADE_REQUIRED")
	}
	b, _ := json.Marshal(p)
	p.Token = hash(b)
	return p, nil
}

func validVersionPath(p string) bool {
	parts := strings.Split(p, "/")
	return len(parts) == 2 && parts[0] == "versions" && identifier.MatchString(parts[1])
}

func (m *Manager) inspectSSH(ctx context.Context, r Request) (*SSHService, error) {
	if !usernamePattern.MatchString(r.Username) {
		return nil, errors.New("E_USERNAME")
	}
	if _, e := connectionSpec(r); e != nil {
		return nil, e
	}
	// PAM may move the session into a user scope. Walk ancestors to the
	// listener's service; require its MainPID to be that very ancestor.
	unit, pid, e := m.sshAncestor(ctx)
	if e != nil {
		return nil, e
	}
	binary, e := os.Readlink("/proc/" + strconv.Itoa(pid) + "/exe")
	if e != nil || filepath.Base(binary) != "sshd" || safeRegular(binary) != nil {
		return nil, errors.New("E_SSHD_BINARY_UNKNOWN")
	}
	start, e := m.systemctl(ctx, "show", "--property=ExecStart", "--value", unit)
	if e != nil {
		return nil, errors.New("E_SSHD_COMMAND_UNKNOWN")
	}
	// systemd expands a standalone $SSHD_OPTS from the daemon's own launch
	// environment. Read that exact environment; never use the helper's env.
	environ, e := os.ReadFile("/proc/" + strconv.Itoa(pid) + "/environ")
	if e != nil {
		return nil, errors.New("E_SSHD_COMMAND_UNKNOWN")
	}
	command, e := expandSSHDOptions(string(start), environ)
	if e != nil {
		return nil, e
	}
	args, config, e := parseSSHDCommand(command, binary)
	if e != nil {
		return nil, e
	}
	if e = safeRegular(config); e != nil {
		return nil, errors.New("E_SSHD_CONFIG_UNSAFE")
	}
	sha, e := fileHash(config)
	if e != nil {
		return nil, errors.New("E_SSHD_CONFIG_UNKNOWN")
	}
	s := &SSHService{Binary: binary, Config: config, Service: unit, Args: args, ConfigSHA256: sha}
	policy, e := m.effectiveSSH(ctx, r, *s)
	if e != nil {
		return nil, e
	}
	s.ForwardProxy = policy["forwardproxy"] != "" && policy["forwardproxy"] != "none"
	s.BypassSupported = policy["forwardproxybypass"] != ""
	s.PermitOpen = strings.Fields(policy["permitopen"])
	if policy["forwardproxybypass"] != "none" {
		s.BypassEndpoints = strings.Fields(policy["forwardproxybypass"])
	}
	if policy["disableforwarding"] == "yes" || (policy["allowtcpforwarding"] != "yes" && policy["allowtcpforwarding"] != "local") {
		return nil, errors.New("E_TCP_FORWARDING_DISABLED")
	}
	return s, nil
}

// OpenSSH PermitOpen compares the requested host literally (no DNS lookup).
// Key-specific restrictions are additionally checked by the fresh SSH probe.
func tcpPermitted(entries []string, host string, port int) bool {
	for _, entry := range entries {
		if entry == "any" {
			return true
		}
		h, p, err := net.SplitHostPort(entry)
		if err == nil && (h == "*" || h == host) && (p == "*" || p == strconv.Itoa(port)) {
			return true
		}
	}
	return false
}

func expandSSHDOptions(command string, environ []byte) (string, error) {
	value := ""
	for _, entry := range strings.Split(string(environ), "\x00") {
		if strings.HasPrefix(entry, "SSHD_OPTS=") {
			value = strings.TrimPrefix(entry, "SSHD_OPTS=")
		}
	}
	if strings.ContainsAny(value, "$`'\"\\\n\r;") {
		return "", errors.New("E_SSHD_COMMAND_AMBIGUOUS")
	}
	command = strings.ReplaceAll(command, " $SSHD_OPTS ", " "+value+" ")
	command = strings.ReplaceAll(command, " ${SSHD_OPTS} ", " "+value+" ")
	return command, nil
}

func (m *Manager) sshAncestor(ctx context.Context) (string, int, error) {
	pid := os.Getpid()
	for depth := 0; depth < 40 && pid > 1; depth++ {
		base := "/proc/" + strconv.Itoa(pid)
		binary, _ := os.Readlink(base + "/exe")
		if filepath.Base(binary) == "sshd" {
			cgroup, _ := os.ReadFile(base + "/cgroup")
			for _, line := range splitLines(cgroup) {
				for _, unit := range strings.Split(line, "/") {
					if !strings.HasSuffix(unit, ".service") || strings.ContainsAny(unit, " \t\n;") {
						continue
					}
					value, err := m.systemctl(ctx, "show", "--property=MainPID", "--value", unit)
					main, parse := strconv.Atoi(strings.TrimSpace(string(value)))
					if err == nil && parse == nil && main == pid {
						return unit, pid, nil
					}
				}
			}
		}
		stat, err := os.ReadFile(base + "/stat")
		if err != nil {
			break
		}
		end := strings.LastIndexByte(string(stat), ')')
		if end < 0 {
			break
		}
		fields := strings.Fields(string(stat)[end+1:])
		if len(fields) < 2 {
			break
		}
		parent, err := strconv.Atoi(fields[1])
		if err != nil || parent == pid {
			break
		}
		pid = parent
	}
	return "", 0, errors.New("E_SSHD_SERVICE_UNKNOWN")
}

// Reject shell expansion, wrappers, command prefixes and unknown arguments.
// Such services require explicit administrator configuration, not guesswork.
func parseSSHDCommand(raw, binary string) ([]string, string, error) {
	invalid := errors.New("E_SSHD_COMMAND_AMBIGUOUS")
	if strings.Count(raw, "argv[]=") != 1 {
		return nil, "", invalid
	}
	_, tail, _ := strings.Cut(raw, "argv[]=")
	cmd, _, ok := strings.Cut(tail, " ;")
	if !ok || strings.ContainsAny(cmd, "$`'\"\\\n\r") {
		return nil, "", invalid
	}
	fields := strings.Fields(cmd)
	if len(fields) == 0 || fields[0] != binary {
		return nil, "", invalid
	}
	config := "/etc/ssh/sshd_config"
	var args []string
	for i := 1; i < len(fields); i++ {
		if option, ok := strings.CutPrefix(fields[i], "-o"); ok {
			if option == "" {
				i++
				if i == len(fields) {
					return nil, "", invalid
				}
				option = fields[i]
			}
			if !supportedSSHDStartupOption(option) {
				return nil, "", invalid
			}
			// Preserve the listener's exact overrides for every -T/-t query.
			// Dropping them would inspect a different effective SSH policy.
			args = append(args, "-o", option)
			continue
		}
		switch fields[i] {
		case "-D", "-e":
		case "-f", "-h", "-p":
			flag := fields[i]
			i++
			if i == len(fields) {
				return nil, "", invalid
			}
			value := fields[i]
			if flag == "-p" {
				n, e := strconv.Atoi(value)
				if e != nil || n < 1 || n > 65535 {
					return nil, "", invalid
				}
			} else if !filepath.IsAbs(value) || strings.ContainsAny(value, " ,;\t") {
				return nil, "", invalid
			}
			args = append(args, flag, value)
			if flag == "-f" {
				config = value
			}
		default:
			return nil, "", invalid
		}
	}
	return args, config, nil
}

func supportedSSHDStartupOption(option string) bool {
	name, value, ok := strings.Cut(option, "=")
	if !ok || value == "" || strings.ContainsAny(value, " ,;\t\r\n") {
		return false
	}
	// These options are used by the independent proxy SSH service and do not
	// override the TCP/bypass policy that the deployment may need to amend.
	// Keep arbitrary -o options (especially Include/ForwardProxyBypass) blocked.
	switch strings.ToLower(name) {
	case "allowudpforwarding":
		return value == "yes" || value == "no"
	case "pidfile":
		return value == "none" || filepath.IsAbs(value)
	default:
		return false
	}
}

func connectionSpec(r Request) (string, error) {
	f := strings.Fields(r.SSHConnection)
	if len(f) != 4 || net.ParseIP(f[0]) == nil || net.ParseIP(f[2]) == nil || !usernamePattern.MatchString(r.Username) {
		return "", errors.New("E_SSH_CONNECTION")
	}
	for _, s := range []string{f[1], f[3]} {
		n, e := strconv.Atoi(s)
		if e != nil || n < 1 || n > 65535 {
			return "", errors.New("E_SSH_CONNECTION")
		}
	}
	return "user=" + r.Username + ",host=" + f[0] + ",addr=" + f[0] + ",laddr=" + f[2] + ",lport=" + f[3], nil
}

func (m *Manager) effectiveSSH(ctx context.Context, r Request, s SSHService) (map[string]string, error) {
	spec, e := connectionSpec(r)
	if e != nil {
		return nil, e
	}
	args := append([]string{}, s.Args...)
	args = append(args, "-T", "-C", spec)
	b, e := m.run(ctx, s.Binary, args...)
	if e != nil {
		return nil, errors.New("E_SSHD_CONFIG_TEST")
	}
	policy := map[string]string{}
	for _, line := range splitLines(b) {
		k, v, ok := strings.Cut(line, " ")
		if ok {
			policy[k] = v
		}
	}
	return policy, nil
}
