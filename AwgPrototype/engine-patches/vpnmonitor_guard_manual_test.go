package main

import (
	"strings"
	"testing"
	"time"
)

func TestGuardManualPassAndReturn(t *testing.T) {
	p := defaultGuardPolicy()
	p.BackupEndpoint = "192.0.2.2:51820"
	now := time.Now()
	g := guardDecision{}
	if !g.beginManual(now, true, p, strings.Repeat("a", 32)) || !g.backup {
		t.Fatal("manual test was not started")
	}
	if state, _ := g.manualObserve(now.Add(5*time.Second), false, p); state != "TestingBackup" {
		t.Fatal("old health reused")
	}
	if state, _ := g.manualObserve(now.Add(10*time.Second), true, p); state != "Reconnect" || g.backup || g.manual.result != "Passed" {
		t.Fatal("successful backup did not return")
	}
	if state, _ := g.manualObserve(now.Add(15*time.Second), false, p); state != "VerifyingPrimary" {
		t.Fatal("primary return not verified")
	}
	if state, _ := g.manualObserve(now.Add(20*time.Second), true, p); state != "Connected" || g.manualStatus() != "Passed" || len(g.attempts) != 2 {
		t.Fatal("primary result incorrect")
	}
	if g.beginManual(now.Add(time.Minute), true, p, "new") {
		t.Fatal("manual test bypassed rolling budget")
	}
}
func TestGuardManualTimeoutAndRescue(t *testing.T) {
	p := defaultGuardPolicy()
	p.BackupEndpoint = "192.0.2.2:51820"
	now := time.Now()
	g := guardDecision{}
	g.beginManual(now, true, p, "id")
	if state, _ := g.manualObserve(now.Add(100*time.Second), false, p); state != "Reconnect" || g.backup || g.manual.result != "Failed" {
		t.Fatal("failed test did not restore primary")
	}
	if state, _ := g.manualObserve(now.Add(105*time.Second), true, p); state != "Connected" || g.manualStatus() != "Failed" {
		t.Fatal("failed backup incorrectly marked passed")
	}
	g = guardDecision{}
	g.beginManual(now, true, p, "id")
	g.manualObserve(now.Add(10*time.Second), true, p)
	if state, _ := g.manualObserve(now.Add(150*time.Second), false, p); state != "Reconnect" || !g.backup || g.manualStatus() != "ReturnFailed" || len(g.attempts) != 3 {
		t.Fatal("failed primary return did not invoke bounded recovery")
	}
}
func TestGuardManualAdmission(t *testing.T) {
	now := time.Now()
	p := defaultGuardPolicy()
	p.BackupEndpoint = "192.0.2.2:51820"
	for _, g := range []guardDecision{{backup: true}, {attempts: []time.Time{now.Add(-time.Minute)}}, {manual: guardManualTrial{phase: "Testing"}}} {
		if g.beginManual(now, true, p, "id") {
			t.Fatal("unsafe manual admission")
		}
	}
	g := guardDecision{}
	if g.beginManual(now, false, p, "id") {
		t.Fatal("unhealthy primary accepted")
	}
	p.Enabled = false
	if g.beginManual(now, true, p, "id") {
		t.Fatal("disabled controller accepted test")
	}
	r := guardRequest{strings.Repeat("a", 32), 42, now, "TestBackup"}
	if !validGuardRequest(r, now, 42) || validGuardRequest(r, now, 43) || validGuardRequest(r, now.Add(time.Minute), 42) {
		t.Fatal("stale or wrong-process request accepted")
	}
	r.Id = "../invalid"
	if validGuardRequest(r, now, 42) {
		t.Fatal("invalid id accepted")
	}
	var events []guardEvent
	for i := 0; i < 250; i++ {
		events = appendGuardEvent(events, guardEvent{ProcessId: i})
	}
	if len(events) != 200 || events[0].ProcessId != 50 {
		t.Fatal("event history unbounded")
	}
}
