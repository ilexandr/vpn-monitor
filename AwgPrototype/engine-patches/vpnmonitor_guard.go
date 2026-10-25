package main

import (
	"context"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"github.com/amnezia-vpn/amneziawg-go/v3/device"
	"github.com/amnezia-vpn/amneziawg-go/v3/tun"
	"github.com/amnezia-vpn/amneziawg-windows/v3/conf"
	"github.com/amnezia-vpn/amneziawg-windows/v3/tunnel/winipcfg"
	"golang.org/x/sys/windows"
	"log"
	"net"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"
	"unsafe"
)

type guardRoute struct {
	luid        winipcfg.LUID
	destination net.IPNet
	gateway     net.IP
}

func (r guardRoute) remove() { _ = r.luid.DeleteRoute(r.destination, r.gateway) }
func physicalGateway() (*winipcfg.MibIPforwardRow2, error) {
	rows, err := winipcfg.GetIPForwardTable2(windows.AF_INET)
	if err != nil {
		return nil, err
	}
	var selected *winipcfg.MibIPforwardRow2
	var cost uint64 = ^uint64(0)
	for i := range rows {
		r := &rows[i]
		if r.DestinationPrefix.PrefixLength != 0 || r.NextHop.IP().IsUnspecified() {
			continue
		}
		iface, e := r.InterfaceLUID.Interface()
		if e != nil || iface.OperStatus != winipcfg.IfOperStatusUp || iface.InterfaceAndOperStatusFlags&winipcfg.IAOSFHardwareInterface == 0 {
			continue
		}
		ipif, e := r.InterfaceLUID.IPInterface(windows.AF_INET)
		if e != nil {
			continue
		}
		c := uint64(r.Metric) + uint64(ipif.Metric)
		if c < cost {
			cost = c
			selected = r
		}
	}
	if selected == nil {
		return nil, fmt.Errorf("no physical IPv4 gateway")
	}
	return selected, nil
}
func resolveGuardEndpoint(ctx context.Context, endpoint string) (string, error) {
	host, port, err := net.SplitHostPort(endpoint)
	if err != nil {
		return "", err
	}
	ip := net.ParseIP(host)
	if ip == nil {
		lookup, cancel := context.WithTimeout(ctx, 3*time.Second)
		defer cancel()
		addresses, e := net.DefaultResolver.LookupIPAddr(lookup, host)
		if e != nil {
			return "", e
		}
		for _, a := range addresses {
			if a.IP.To4() != nil {
				ip = a.IP
				break
			}
		}
		if ip == nil && len(addresses) > 0 {
			ip = addresses[0].IP
		}
	}
	if ip == nil {
		return "", fmt.Errorf("endpoint resolution failed")
	}
	return net.JoinHostPort(ip.String(), port), nil
}
func maintainEndpointRoutes(endpoints []string, enabled bool, owned map[string]guardRoute) bool {
	if !enabled {
		for k, r := range owned {
			r.remove()
			delete(owned, k)
		}
		return false
	}
	gateway, err := physicalGateway()
	if err != nil {
		return false
	}
	changed := false
	wanted := map[string]bool{}
	rows, err := winipcfg.GetIPForwardTable2(windows.AF_INET)
	if err != nil {
		return false
	}
	for _, endpoint := range endpoints {
		resolved, e := resolveGuardEndpoint(context.Background(), endpoint)
		if e != nil {
			continue
		}
		host, _, _ := net.SplitHostPort(resolved)
		ip := net.ParseIP(host)
		if ip == nil || ip.To4() == nil {
			continue
		}
		ip = ip.To4()
		key := ip.String()
		wanted[key] = true
		if old, ok := owned[key]; ok {
			if old.luid == gateway.InterfaceLUID && old.gateway.Equal(gateway.NextHop.IP()) {
				present := false
				for _, r := range rows {
					if r.InterfaceLUID == old.luid && r.DestinationPrefix.PrefixLength == 32 && r.DestinationPrefix.Prefix.IP().Equal(ip) && r.NextHop.IP().Equal(old.gateway) {
						present = true
						break
					}
				}
				if present {
					continue
				}
			}
			old.remove()
			delete(owned, key)
		}
		// Explicit routes owned by an administrator or another product take precedence.
		explicit := false
		for _, r := range rows {
			if r.DestinationPrefix.PrefixLength == 32 && r.DestinationPrefix.Prefix.IP().Equal(ip) {
				explicit = true
				break
			}
		}
		if explicit {
			continue
		}
		destination := net.IPNet{IP: ip, Mask: net.CIDRMask(32, 32)}
		if gateway.InterfaceLUID.AddRoute(destination, gateway.NextHop.IP(), 5) == nil {
			owned[key] = guardRoute{gateway.InterfaceLUID, destination, gateway.NextHop.IP()}
			changed = true
			guardAudit("endpoint route pinned to physical uplink")
		}
	}
	for k, r := range owned {
		if !wanted[k] {
			r.remove()
			delete(owned, k)
		}
	}
	return changed
}
func readGuardPolicy(config *conf.Config) guardPolicy {
	p := defaultGuardPolicy()
	root, e := conf.RootDirectory(false)
	if e != nil {
		return p
	}
	if b, e := os.ReadFile(filepath.Join(root, "connection-policy.json")); e == nil {
		candidate := p
		if len(b) > 16384 || json.Unmarshal(b, &candidate) != nil || !candidate.valid() {
			p.Enabled = false
			return p
		}
		p = candidate
	}
	if len(config.Peers) != 1 {
		p.BackupEndpoint = ""
		p.ReturnToPrimary = false
	}
	if p.BackupEndpoint != "" {
		h, port, e := net.SplitHostPort(p.BackupEndpoint)
		n, _ := strconv.Atoi(port)
		if e != nil || net.ParseIP(h) == nil || n < 1 || n > 65535 {
			p.BackupEndpoint = ""
		}
	}
	return p
}
func guardProbeAddress(config *conf.Config, p guardPolicy) (net.IP, net.IP) {
	var source net.IP
	for _, a := range config.Interface.Addresses {
		if a.IP.To4() != nil {
			source = a.IP.To4()
			break
		}
	}
	target := net.ParseIP(p.ProbeHost)
	if p.ProbeHost == "" {
		for _, peer := range config.Peers {
			for _, a := range peer.AllowedIPs {
				n := a.IPNet()
				ones, bits := n.Mask.Size()
				if bits == 32 && ones >= 8 && ones <= 30 && n.IP.IsPrivate() {
					b := append(net.IP(nil), n.IP.Mask(n.Mask).To4()...)
					b[3]++
					if !b.Equal(source) {
						target = b
						break
					}
				}
			}
			if target != nil {
				break
			}
		}
	}
	if source == nil || target == nil || target.Equal(source) {
		return nil, nil
	}
	allowed := false
	for _, peer := range config.Peers {
		for _, a := range peer.AllowedIPs {
			n := a.IPNet()
			if n.Contains(target) {
				allowed = true
			}
		}
	}
	if !allowed {
		return nil, nil
	}
	return source, target
}
func guardAudit(message string) {
	log.Println("Connection guard:", message)
	root, e := conf.RootDirectory(false)
	if e != nil {
		return
	}
	path := filepath.Join(root, "connection-guard.log")
	if info, e := os.Stat(path); e == nil && info.Size() > 256*1024 {
		_ = os.Remove(path + ".1")
		_ = os.Rename(path, path+".1")
	}
	file, e := os.OpenFile(path, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0600)
	if e != nil {
		return
	}
	defer file.Close()
	_, _ = fmt.Fprintf(file, "%s %s\n", time.Now().Format(time.RFC3339), message)
}
func guardTraffic(dev *device.Device) (int64, uint64) {
	data, err := dev.IpcGet()
	if err != nil {
		return 0, 0
	}
	var latest int64
	var rx uint64
	for _, line := range strings.Split(data, "\n") {
		if strings.HasPrefix(line, "last_handshake_time_sec=") {
			n, _ := strconv.ParseInt(strings.TrimPrefix(line, "last_handshake_time_sec="), 10, 64)
			if n > latest {
				latest = n
			}
		}
		if strings.HasPrefix(line, "rx_bytes=") {
			n, _ := strconv.ParseUint(strings.TrimPrefix(line, "rx_bytes="), 10, 64)
			rx += n
		}
	}
	return latest, rx
}
func guardPing(source net.IP, target net.IP) bool {
	ip := target.To4()
	if ip == nil || source.To4() == nil {
		return false
	}
	library := windows.NewLazySystemDLL("iphlpapi.dll")
	create := library.NewProc("IcmpCreateFile")
	send := library.NewProc("IcmpSendEcho2Ex")
	closeHandle := library.NewProc("IcmpCloseHandle")
	if create.Find() != nil || send.Find() != nil || closeHandle.Find() != nil {
		return false
	}
	handle, _, _ := create.Call()
	if handle == ^uintptr(0) || handle == 0 {
		return false
	}
	defer closeHandle.Call(handle)
	data := []byte("vpnmon")
	reply := make([]byte, 128)
	n, _, _ := send.Call(handle, 0, 0, 0, uintptr(binary.LittleEndian.Uint32(source.To4())), uintptr(binary.LittleEndian.Uint32(ip)), uintptr(unsafe.Pointer(&data[0])), uintptr(len(data)), 0, uintptr(unsafe.Pointer(&reply[0])), uintptr(len(reply)), 1000)
	return n > 0 && binary.LittleEndian.Uint32(reply[4:8]) == 0
}
func writeGuardStatus(state string, tcp bool, ping bool, healthy bool, handshake int64, started time.Time, endpoint string) {
	root, e := conf.RootDirectory(false)
	if e != nil {
		return
	}
	value := struct {
		State         string
		TcpReachable  bool
		IcmpReachable bool
		Healthy       bool
		HandshakeUnix int64
		StartedAt     time.Time
		CheckedAt     time.Time
		ProcessId     int
		Endpoint      string
	}{state, tcp, ping, healthy, handshake, started, time.Now(), os.Getpid(), endpoint}
	b, e := json.Marshal(value)
	if e != nil {
		return
	}
	path := filepath.Join(filepath.Dir(root), "AwgStatus", "connection.json")
	if os.WriteFile(path+".new", b, 0600) == nil {
		_ = os.Rename(path+".new", path)
	}
}

// Restore only missing entries. Flushing a live Wintun interface can invalidate
// its Windows address rows and make the upstream watcher stop the service.
func repairVpnMonitorInterface(config *conf.Config, nativeTun *tun.NativeTun) {
	luid := winipcfg.LUID(nativeTun.LUID())
	for _, address := range config.Interface.Addresses {
		if _, err := luid.IPAddress(address.IP); err != nil {
			if err = luid.AddIPAddress(address.IPNet()); err != nil && err != windows.ERROR_OBJECT_ALREADY_EXISTS {
				guardAudit("could not restore a missing interface address")
			}
		}
	}
	if config.Interface.TableOff {
		return
	}
	for _, peer := range config.Peers {
		for _, allowed := range peer.AllowedIPs {
			destination := allowed.IPNet()
			destination.IP = destination.IP.Mask(destination.Mask)
			nextHop := net.IPv6zero
			if destination.IP.To4() != nil {
				nextHop = net.IPv4zero
			}
			if _, err := luid.Route(destination, nextHop); err != nil {
				if err = luid.AddRoute(destination, nextHop, 0); err != nil && err != windows.ERROR_OBJECT_ALREADY_EXISTS {
					guardAudit("could not restore a missing interface route")
				}
			}
		}
	}
}
func startVpnMonitorGuard(dev *device.Device, config *conf.Config, owned map[string]guardRoute, repairInterface func()) func() {
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	started := time.Now()
	go func() {
		defer close(done)
		defer func() {
			for _, r := range owned {
				r.remove()
			}
		}()
		decision := guardDecision{}
		var lastAction time.Time
		var lastReceiveAt time.Time
		var previousRx uint64
		var previousHandshake int64
		lastState := ""
		primary := ""
		if len(config.Peers) == 1 {
			primary = config.Peers[0].Endpoint.String()
		}
		for {
			p := readGuardPolicy(config)
			endpoints := []string{}
			for _, peer := range config.Peers {
				endpoints = append(endpoints, peer.Endpoint.String())
			}
			if p.BackupEndpoint != "" {
				endpoints = append(endpoints, p.BackupEndpoint)
			}
			maintainEndpointRoutes(endpoints, p.PreferPhysical, owned)
			source, target := guardProbeAddress(config, p)
			tcp := false
			if target != nil {
				dial := net.Dialer{Timeout: 3 * time.Second, LocalAddr: &net.TCPAddr{IP: source}}
				c, e := dial.DialContext(ctx, "tcp4", net.JoinHostPort(target.String(), strconv.Itoa(p.ProbePort)))
				if e == nil {
					tcp = true
					c.Close()
				}
			}
			if ctx.Err() != nil {
				return
			}
			now := time.Now()
			ping := false
			if !tcp && target != nil {
				ping = guardPing(source, target)
			}
			handshake, rx := guardTraffic(dev)
			if rx > previousRx || (handshake > previousHandshake && now.Unix()-handshake >= -5 && now.Unix()-handshake < 30) {
				lastReceiveAt = now
			}
			previousRx, previousHandshake = rx, handshake
			fresh := !lastReceiveAt.IsZero() && now.Sub(lastReceiveAt) < time.Duration(2*p.IntervalSeconds)*time.Second && (lastAction.IsZero() || lastReceiveAt.After(lastAction))
			// A proxy or a second VPN may satisfy TCP/ICMP without our tunnel.
			// Require fresh receive/handshake evidence from this AWG device itself.
			healthy := fresh
			state := "Unknown"
			activeEndpoint := primary
			if decision.backup {
				activeEndpoint = p.BackupEndpoint
			}
			if target != nil && now.Sub(started) >= 30*time.Second {
				state = decision.observe(now, healthy, p)
			} else if !p.Enabled {
				state = "Disabled"
			} else if healthy {
				state = "Connected"
			}
			if state == "Reconnect" {
				if decision.backup {
					activeEndpoint = p.BackupEndpoint
				} else {
					activeEndpoint = primary
				}
				guardAudit("confirmed outage or primary retry; reconnecting shared tunnel")
				writeGuardStatus("Reconnecting", tcp, ping, healthy, handshake, started, activeEndpoint)
				dev.Down()
				if ctx.Err() != nil {
					return
				}
				if len(config.Peers) == 1 && activeEndpoint != "" {
					resolved, e := resolveGuardEndpoint(ctx, activeEndpoint)
					if e == nil {
						request := "public_key=" + hex.EncodeToString(config.Peers[0].PublicKey[:]) + "\nendpoint=" + resolved + "\n"
						e = dev.IpcSet(request)
					}
					if e != nil {
						guardAudit("endpoint update failed")
					}
				}
				if ctx.Err() != nil {
					return
				}
				if repairInterface != nil {
					repairInterface()
				}
				if ctx.Err() != nil {
					return
				}
				if e := dev.Up(); e != nil {
					guardAudit("reconnect failed")
				}
				lastAction = now
			}
			writeGuardStatus(state, tcp, ping, healthy, handshake, started, activeEndpoint)
			if state != lastState {
				guardAudit(state)
				lastState = state
			}
			interval := p.IntervalSeconds
			if now.Sub(started) < 60*time.Second && interval > 10 {
				interval = 10
			}
			timer := time.NewTimer(time.Duration(interval) * time.Second)
			select {
			case <-ctx.Done():
				timer.Stop()
				return
			case <-timer.C:
			}
		}
	}()
	return func() { cancel(); <-done; writeGuardStatus("Stopped", false, false, false, 0, started, "") }
}
