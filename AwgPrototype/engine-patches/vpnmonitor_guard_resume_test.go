package main

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"github.com/amnezia-vpn/amneziawg-windows/v3/conf"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestGuardResumeBoundaries(t *testing.T) {
	now := time.Now()
	profile := guardProfileContext{1, strings.Repeat("a", 32), "Office", strings.Repeat("b", 64)}
	p := defaultGuardPolicy()
	p.BackupEndpoint = "192.0.2.2:51820"
	config := &conf.Config{Peers: []conf.Peer{{Endpoint: conf.Endpoint{Host: "unresolved-primary.invalid", Port: 51820}}}}
	value := guardResume{1, profile.ProfileId, profile.Fingerprint, config.Peers[0].Endpoint.String(), p.BackupEndpoint, "backup", now}
	next, backup := guardStartupConfig(config, p, profile, value, now)
	if !backup || next.Peers[0].Endpoint.String() != p.BackupEndpoint || config.Peers[0].Endpoint.Host != "unresolved-primary.invalid" {
		t.Fatal("resume must clone endpoint without resolving/changing primary")
	}
	p.ReturnToPrimary = true
	g := guardDecision{backup: true, lastAction: now}
	if g.observe(now.Add(time.Second), true, p) != "Backup" || g.observe(now.Add(time.Duration(p.PrimaryRetrySeconds)*time.Second), true, p) != "Reconnect" || g.backup {
		t.Fatal("resumed backup must follow configured failback timer")
	}
	for _, change := range []func(*guardResume){func(v *guardResume) { v.ProfileId = strings.Repeat("c", 32) }, func(v *guardResume) { v.Fingerprint = strings.Repeat("d", 64) }, func(v *guardResume) { v.PrimaryEndpoint = "changed:51820" }, func(v *guardResume) { v.BackupEndpoint = "192.0.2.3:51820" }, func(v *guardResume) { v.Role = "unknown" }, func(v *guardResume) { v.Version = 2 }, func(v *guardResume) { v.VerifiedAt = now.Add(time.Minute) }} {
		altered := value
		change(&altered)
		if validGuardResume(altered, profile, config.Peers[0].Endpoint.String(), p, now) {
			t.Fatal("stale/foreign/invalid snapshot accepted")
		}
	}
	p.RememberWorkingEndpoint = false
	if _, ok := guardStartupConfig(config, p, profile, value, now); ok {
		t.Fatal("opt-out ignored")
	}
	p.RememberWorkingEndpoint = true
	p.Enabled = false
	if _, ok := guardStartupConfig(config, p, profile, value, now); ok {
		t.Fatal("disabled policy resumed backup")
	}
	p.Enabled = true
	config.Peers = append(config.Peers, config.Peers[0])
	if _, ok := guardStartupConfig(config, p, profile, value, now); ok {
		t.Fatal("multi-peer profile resumed backup")
	}
}
func TestGuardProfileStore(t *testing.T) {
	root := t.TempDir()
	text := "private configuration test fixture"
	sum := sha256.Sum256([]byte(text))
	profile := guardProfileContext{1, strings.Repeat("a", 32), "Офис", hex.EncodeToString(sum[:])}
	bytes, _ := json.Marshal(profile)
	os.WriteFile(filepath.Join(root, "active-context.json"), bytes, 0600)
	if got := loadGuardProfile(root, text); got.ProfileId != profile.ProfileId {
		t.Fatal("profile not bound")
	}
	if got := loadGuardProfile(root, text+"changed"); got.ProfileId != "" {
		t.Fatal("wrong profile revision accepted")
	}
	value := guardResume{1, profile.ProfileId, profile.Fingerprint, "192.0.2.1:51820", "192.0.2.2:51820", "backup", time.Now()}
	if !saveGuardResume(root, value) {
		t.Fatal("snapshot not saved")
	}
	var saved guardResume
	if !readGuardJSON(guardResumePath(root, profile.ProfileId), &saved, 8192) || saved.Role != "backup" {
		t.Fatal("saved snapshot unreadable")
	}
	if guardResumePath(root, "../escape") != "" {
		t.Fatal("unsafe path accepted")
	}
	os.WriteFile(filepath.Join(root, "bad.json"), []byte(strings.Repeat("x", 9000)), 0600)
	if readGuardJSON(filepath.Join(root, "bad.json"), &saved, 8192) {
		t.Fatal("oversized record accepted")
	}
	for i := 0; i < 250; i++ {
		storeGuardEvent(root, guardEvent{At: time.Now(), ProfileId: profile.ProfileId, ProfileName: profile.ProfileName, ProcessId: i})
	}
	other := strings.Repeat("c", 32)
	storeGuardEvent(root, guardEvent{At: time.Now(), ProfileId: other, ProfileName: "Other"})
	storeGuardEvent(root, guardEvent{At: time.Now()})
	var events []guardEvent
	if !readGuardJSON(filepath.Join(root, "connection-events-"+profile.ProfileId+".json"), &events, 256*1024) || len(events) != 200 || events[0].ProcessId != 50 {
		t.Fatal("per-profile history lost to another profile")
	}
	for _, e := range events {
		if e.ProfileId != profile.ProfileId {
			t.Fatal("profile histories mixed")
		}
	}
}

func TestGuardResumeProofAndManual(t *testing.T) {
	now := time.Now()
	profile := guardProfileContext{1, strings.Repeat("a", 32), "Office", strings.Repeat("b", 64)}
	p := defaultGuardPolicy()
	p.BackupEndpoint = "192.0.2.2:51820"
	g := guardDecision{backup: true}
	if _, ok := guardResumeCandidate(profile, "192.0.2.1:51820", p, &g, false, now, guardResume{}); ok {
		t.Fatal("unverified connection cached")
	}
	for _, phase := range []string{"Testing", "Returning"} {
		g.manual.phase = phase
		if _, ok := guardResumeCandidate(profile, "192.0.2.1:51820", p, &g, true, now, guardResume{}); ok {
			t.Fatal("temporary manual test replaced saved endpoint")
		}
	}
	g.manual.phase = ""
	value, ok := guardResumeCandidate(profile, "192.0.2.1:51820", p, &g, true, now, guardResume{})
	if !ok || value.Role != "backup" {
		t.Fatal("verified backup not saved")
	}
	if _, ok := guardResumeCandidate(profile, "192.0.2.1:51820", p, &g, true, now.Add(time.Minute), value); ok {
		t.Fatal("snapshot written every poll")
	}
	if _, ok := guardResumeCandidate(profile, "192.0.2.1:51820", p, &g, true, now.Add(time.Hour), value); !ok {
		t.Fatal("snapshot not refreshed")
	}
	g.backup = false
	if v, ok := guardResumeCandidate(profile, "192.0.2.1:51820", p, &g, true, now, value); !ok || v.Role != "primary" {
		t.Fatal("verified failback not saved")
	}
	root := t.TempDir()
	bytes, _ := json.Marshal(value)
	path := filepath.Join(root, "bad.json")
	os.WriteFile(path, bytes[:len(bytes)-1], 0600)
	if loadGuardResume(path).Version != 0 {
		t.Fatal("partially parsed corrupt snapshot accepted")
	}
}

func TestGuardStartupUAPIIntegration(t *testing.T) {
	root := t.TempDir()
	previousRoot, _ := conf.RootDirectory(false)
	previousProfile := activeGuardProfile
	conf.PresetRootDirectory(root)
	defer conf.PresetRootDirectory(previousRoot)
	defer func() { activeGuardProfile = previousProfile }()
	activeGuardProfile = guardProfileContext{1, strings.Repeat("a", 32), "Office", strings.Repeat("b", 64)}
	p := defaultGuardPolicy()
	p.BackupEndpoint = "192.0.2.2:51820"
	policy, _ := json.Marshal(p)
	os.WriteFile(filepath.Join(root, "connection-policy.json"), policy, 0600)
	config := &conf.Config{Peers: []conf.Peer{{Endpoint: conf.Endpoint{Host: "unresolved-primary.invalid", Port: 51820}}}}
	saveGuardResume(root, guardResume{1, activeGuardProfile.ProfileId, activeGuardProfile.Fingerprint, config.Peers[0].Endpoint.String(), p.BackupEndpoint, "backup", time.Now()})
	text, backup, e := guardStartupUAPI(config)
	if e != nil || !backup || !strings.Contains(text, "endpoint=192.0.2.2:51820") || config.Peers[0].Endpoint.Host != "unresolved-primary.invalid" {
		t.Fatal("startup did not bypass primary DNS using immutable original config")
	}
	var events []guardEvent
	if !readGuardJSON(filepath.Join(root, "connection-events-"+activeGuardProfile.ProfileId+".json"), &events, 256*1024) || len(events) != 1 || events[0].Reason != "ResumeBackup" {
		t.Fatal("startup selection not attributed to profile")
	}
}
