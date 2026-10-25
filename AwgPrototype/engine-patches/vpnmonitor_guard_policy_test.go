package main

import (
	"github.com/amnezia-vpn/amneziawg-windows/v3/conf"
	"net"
	"testing"
	"time"
)

func TestGuardDelayCooldownBudget(t *testing.T) {
	p := defaultGuardPolicy()
	g := guardDecision{}
	now := time.Now()
	if g.observe(now, false, p) != "Confirming" || g.observe(now.Add(29*time.Second), false, p) != "Confirming" {
		t.Fatal("early reconnect")
	}
	if g.observe(now.Add(30*time.Second), false, p) != "Reconnect" {
		t.Fatal("missing reconnect")
	}
	g.observe(now.Add(40*time.Second), false, p)
	if g.observe(now.Add(70*time.Second), false, p) != "Backoff" {
		t.Fatal("cooldown ignored")
	}
	if g.observe(now.Add(90*time.Second), false, p) != "Reconnect" {
		t.Fatal("retry missing")
	}
	g.observe(now.Add(100*time.Second), false, p)
	g.observe(now.Add(150*time.Second), false, p)
	g.observe(now.Add(160*time.Second), false, p)
	if g.observe(now.Add(250*time.Second), false, p) != "Backoff" {
		t.Fatal("attempt budget ignored")
	}
	if g.observe(now.Add(750*time.Second), false, p) != "Reconnect" {
		t.Fatal("budget did not recover")
	}
}
func TestGuardBackupAndFailback(t *testing.T) {
	p := defaultGuardPolicy()
	p.BackupEndpoint = "192.0.2.2:51820"
	g := guardDecision{}
	now := time.Now()
	g.observe(now, false, p)
	if g.observe(now.Add(30*time.Second), false, p) != "Reconnect" || !g.backup {
		t.Fatal("backup not selected")
	}
	if g.observe(now.Add(40*time.Second), true, p) != "Backup" {
		t.Fatal("backup health")
	}
	if g.observe(now.Add(400*time.Second), true, p) != "Backup" {
		t.Fatal("unrequested failback")
	}
	p.ReturnToPrimary = true
	if g.observe(now.Add(401*time.Second), true, p) != "Reconnect" || g.backup {
		t.Fatal("primary trial missing")
	}
	g.observe(now.Add(410*time.Second), false, p)
	if g.observe(now.Add(440*time.Second), false, p) != "Reconnect" || !g.backup {
		t.Fatal("failed trial did not restore backup")
	}
	p.Enabled = false
	if g.observe(now.Add(450*time.Second), false, p) != "Disabled" {
		t.Fatal("disabled monitor recovered")
	}
}

func TestGuardProbeStaysInsideProfile(t *testing.T) {
	c := &conf.Config{Interface: conf.Interface{Addresses: []conf.IPCidr{{IP: net.ParseIP("10.20.0.2"), Cidr: 32}}}, Peers: []conf.Peer{{AllowedIPs: []conf.IPCidr{{IP: net.ParseIP("10.20.0.0"), Cidr: 24}}}}}
	p := defaultGuardPolicy()
	source, target := guardProbeAddress(c, p)
	if !source.Equal(net.ParseIP("10.20.0.2")) || !target.Equal(net.ParseIP("10.20.0.1")) {
		t.Fatal("wrong automatic VPN probe")
	}
	p.ProbeHost = "192.0.2.1"
	if s, _ := guardProbeAddress(c, p); s != nil {
		t.Fatal("out-of-profile probe allowed")
	}
	p.ProbeHost = "10.20.0.2"
	if s, _ := guardProbeAddress(c, p); s != nil {
		t.Fatal("client probed itself")
	}
	p.ProbeHost = "10.20.0.10"
	if _, target = guardProbeAddress(c, p); !target.Equal(net.ParseIP(p.ProbeHost)) {
		t.Fatal("valid explicit probe refused")
	}
}
func TestGuardShortLossAndDisabled(t *testing.T) {
	p := defaultGuardPolicy()
	g := guardDecision{}
	now := time.Now()
	g.observe(now, false, p)
	if g.observe(now.Add(20*time.Second), true, p) != "Connected" {
		t.Fatal("short outage not cancelled")
	}
	if g.observe(now.Add(50*time.Second), false, p) != "Confirming" {
		t.Fatal("old failure time reused")
	}
	p.Enabled = false
	if g.observe(now.Add(100*time.Second), false, p) != "Disabled" || len(g.attempts) != 0 {
		t.Fatal("disabled guard interfered")
	}
}

func TestGuardNativeIcmpLoopback(t *testing.T) {
	if !guardPing(net.ParseIP("127.0.0.1"), net.ParseIP("127.0.0.1")) {
		t.Fatal("bound native ICMP probe failed")
	}
}
