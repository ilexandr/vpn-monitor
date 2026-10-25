package main

import (
	"encoding/json"
	"github.com/amnezia-vpn/amneziawg-windows/v3/conf"
	"os"
	"path/filepath"
	"strings"
	"time"
)

type guardRequest struct {
	Id        string
	ProcessId int
	CreatedAt time.Time
	Action    string
}

func validGuardRequest(r guardRequest, now time.Time, pid int) bool {
	if len(r.Id) != 32 || r.ProcessId != pid || r.Action != "TestBackup" || now.Sub(r.CreatedAt) > 30*time.Second || r.CreatedAt.After(now.Add(5*time.Second)) {
		return false
	}
	return strings.IndexFunc(r.Id, func(c rune) bool { return !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f') }) < 0
}
func consumeGuardRequest() *guardRequest {
	root, e := conf.RootDirectory(false)
	if e != nil {
		return nil
	}
	path := filepath.Join(root, "connection-command.json")
	info, e := os.Stat(path)
	if e != nil {
		return nil
	}
	defer os.Remove(path)
	if info.Size() > 4096 {
		return nil
	}
	data, e := os.ReadFile(path)
	if e != nil {
		return nil
	}
	var r guardRequest
	if json.Unmarshal(data, &r) != nil || !validGuardRequest(r, time.Now(), os.Getpid()) {
		return nil
	}
	return &r
}

type guardEvent struct {
	At        time.Time
	State     string
	Endpoint  string
	Reason    string
	RequestId string
	ProcessId int
}

func appendGuardEvent(events []guardEvent, event guardEvent) []guardEvent {
	events = append(events, event)
	if len(events) > 200 {
		events = events[len(events)-200:]
	}
	return events
}
func recordGuardEvent(state, endpoint, reason, requestID string) {
	root, e := conf.RootDirectory(false)
	if e != nil {
		return
	}
	path := filepath.Join(root, "connection-events.json")
	var events []guardEvent
	if info, e := os.Stat(path); e == nil && info.Size() <= 256*1024 {
		if data, e := os.ReadFile(path); e == nil {
			_ = json.Unmarshal(data, &events)
		}
	}
	events = appendGuardEvent(events, guardEvent{time.Now(), state, endpoint, reason, requestID, os.Getpid()})
	if data, e := json.Marshal(events); e == nil {
		if os.WriteFile(path+".new", data, 0600) == nil {
			_ = os.Rename(path+".new", path)
		}
	}
}

func eventRequestID(reason, id string) string {
	if strings.HasPrefix(reason, "Manual") {
		return id
	}
	return ""
}
