package main

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"github.com/amnezia-vpn/amneziawg-windows/v3/conf"
	"net"
	"os"
	"path/filepath"
	"strconv"
	"time"
	"unicode"
	"unicode/utf8"
)

type guardProfileContext struct {
	Version     int
	ProfileId   string
	ProfileName string
	Fingerprint string
}

var activeGuardProfile guardProfileContext // Immutable after entry to the SCM service.
func validGuardHex(text string, length int) bool {
	if len(text) != length {
		return false
	}
	for _, c := range text {
		if !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f') {
			return false
		}
	}
	return true
}
func readGuardJSON(path string, target any, limit int64) bool {
	info, e := os.Stat(path)
	if e != nil || info.Size() > limit {
		return false
	}
	data, e := os.ReadFile(path)
	return e == nil && int64(len(data)) <= limit && json.Unmarshal(data, target) == nil
}
func loadGuardProfile(directory, config string) guardProfileContext {
	sum := sha256.Sum256([]byte(config))
	fingerprint := hex.EncodeToString(sum[:])
	unknown := guardProfileContext{Version: 1, Fingerprint: fingerprint}
	var value guardProfileContext
	if !readGuardJSON(filepath.Join(directory, "active-context.json"), &value, 4096) || value.Version != 1 || value.Fingerprint != fingerprint || !validGuardHex(value.ProfileId, 32) || utf8.RuneCountInString(value.ProfileName) > 80 || value.ProfileName == "" {
		return unknown
	}
	for _, c := range value.ProfileName {
		if unicode.IsControl(c) {
			return unknown
		}
	}
	return value
}

type guardResume struct {
	Version         int
	ProfileId       string
	Fingerprint     string
	PrimaryEndpoint string
	BackupEndpoint  string
	Role            string
	VerifiedAt      time.Time
}

func guardResumePath(directory, id string) string {
	if !validGuardHex(id, 32) {
		return ""
	}
	return filepath.Join(directory, "connection-last-"+id+".json")
}
func validGuardResume(value guardResume, profile guardProfileContext, primary string, p guardPolicy, now time.Time) bool {
	return profile.Version == 1 && p.Enabled && p.RememberWorkingEndpoint && validGuardHex(profile.ProfileId, 32) && validGuardHex(profile.Fingerprint, 64) && value.Version == 1 && value.ProfileId == profile.ProfileId && value.Fingerprint == profile.Fingerprint && value.PrimaryEndpoint == primary && value.BackupEndpoint == p.BackupEndpoint && (value.Role == "primary" || value.Role == "backup" && p.BackupEndpoint != "") && !value.VerifiedAt.IsZero() && !value.VerifiedAt.After(now.Add(5*time.Second))
}
func guardStartupConfig(config *conf.Config, p guardPolicy, profile guardProfileContext, value guardResume, now time.Time) (*conf.Config, bool) {
	if len(config.Peers) != 1 || !validGuardResume(value, profile, config.Peers[0].Endpoint.String(), p, now) || value.Role != "backup" {
		return config, false
	}
	host, port, e := net.SplitHostPort(p.BackupEndpoint)
	number, err := strconv.Atoi(port)
	if e != nil || err != nil || net.ParseIP(host) == nil || number < 1 || number > 65535 {
		return config, false
	}
	next := *config
	next.Peers = append([]conf.Peer(nil), config.Peers...)
	next.Peers[0].Endpoint = conf.Endpoint{Host: host, Port: uint16(number)}
	return &next, true
}
func loadGuardResume(path string) guardResume {
	var value guardResume
	if !readGuardJSON(path, &value, 8192) {
		return guardResume{}
	}
	return value
}
func guardStartupUAPI(config *conf.Config) (string, bool, error) {
	p := readGuardPolicy(config)
	var saved guardResume
	if root, e := conf.RootDirectory(false); e == nil {
		saved = loadGuardResume(guardResumePath(root, activeGuardProfile.ProfileId))
	}
	chosen, backup := guardStartupConfig(config, p, activeGuardProfile, saved, time.Now())
	text, e := chosen.ToUAPI()
	if backup && e != nil {
		recordGuardEvent("StartupFallback", p.BackupEndpoint, "ResumeRejected", "")
		text, e = config.ToUAPI()
		return text, false, e
	}
	if backup && e == nil {
		recordGuardEvent("Starting", p.BackupEndpoint, "ResumeBackup", "")
	}
	return text, backup, e
}
func saveGuardResume(directory string, value guardResume) bool {
	path := guardResumePath(directory, value.ProfileId)
	if path == "" {
		return false
	}
	data, e := json.Marshal(value)
	if e != nil {
		return false
	}
	if os.WriteFile(path+".new", data, 0600) != nil {
		return false
	}
	return os.Rename(path+".new", path) == nil
}
func guardResumeCandidate(profile guardProfileContext, primary string, p guardPolicy, g *guardDecision, healthy bool, now time.Time, last guardResume) (guardResume, bool) {
	if !healthy || !p.Enabled || !p.RememberWorkingEndpoint || g.manual.phase != "" || !validGuardHex(profile.ProfileId, 32) || !validGuardHex(profile.Fingerprint, 64) {
		return guardResume{}, false
	}
	role := "primary"
	if g.backup {
		role = "backup"
		if p.BackupEndpoint == "" {
			return guardResume{}, false
		}
	}
	if last.Role == role && last.BackupEndpoint == p.BackupEndpoint && now.Sub(last.VerifiedAt) < time.Hour {
		return guardResume{}, false
	}
	return guardResume{1, profile.ProfileId, profile.Fingerprint, primary, p.BackupEndpoint, role, now}, true
}
func rememberGuardEndpoint(profile guardProfileContext, primary string, p guardPolicy, g *guardDecision, healthy bool, now time.Time, last *guardResume) {
	value, ok := guardResumeCandidate(profile, primary, p, g, healthy, now, *last)
	if !ok {
		return
	}
	if root, e := conf.RootDirectory(false); e == nil && saveGuardResume(root, value) {
		*last = value
	}
}
