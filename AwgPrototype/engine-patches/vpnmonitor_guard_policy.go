package main

import (
	"time"
)

type guardPolicy struct {
	Enabled             bool
	PreferPhysical      bool
	ProbeHost           string
	ProbePort           int
	IntervalSeconds     int
	FailureSeconds      int
	CooldownSeconds     int
	BackupEndpoint      string
	ReturnToPrimary     bool
	PrimaryRetrySeconds int
}

func defaultGuardPolicy() guardPolicy {
	return guardPolicy{Enabled: true, PreferPhysical: true, ProbePort: 80, IntervalSeconds: 10, FailureSeconds: 30, CooldownSeconds: 60, PrimaryRetrySeconds: 300}
}
func (p guardPolicy) valid() bool {
	return p.ProbePort >= 1 && p.ProbePort <= 65535 && p.IntervalSeconds >= 5 && p.IntervalSeconds <= 300 &&
		p.FailureSeconds >= 15 && p.FailureSeconds <= 600 && p.CooldownSeconds >= 30 && p.CooldownSeconds <= 3600 && p.PrimaryRetrySeconds >= 120 && p.PrimaryRetrySeconds <= 86400
}

type guardDecision struct {
	failureSince time.Time
	lastAction   time.Time
	attempts     []time.Time
	backup       bool
	manual       guardManualTrial
	trial        bool
}

type guardManualTrial struct {
	phase     string
	result    string
	requestID string
	deadline  time.Time
}

func (g *guardDecision) canManual(now time.Time, healthy bool, p guardPolicy) bool {
	// Reserve two operations for test+return, and one for recovery if the primary fails afterwards.
	for _, t := range g.attempts {
		if now.Sub(t) < 10*time.Minute {
			return false
		}
	}
	if g.manual.phase != "" || !p.Enabled || p.BackupEndpoint == "" || !healthy || g.backup || now.Sub(g.lastAction) < time.Duration(p.CooldownSeconds)*time.Second {
		return false
	}
	return true
}
func (g *guardDecision) beginManual(now time.Time, healthy bool, p guardPolicy, id string) bool {
	if !g.canManual(now, healthy, p) {
		return false
	}
	g.attempts = []time.Time{now}
	g.lastAction = now
	g.failureSince = time.Time{}
	g.backup = true
	duration := time.Duration(2*p.IntervalSeconds+15) * time.Second
	if duration < 30*time.Second {
		duration = 30 * time.Second
	}
	if duration > 90*time.Second {
		duration = 90 * time.Second
	}
	g.manual = guardManualTrial{phase: "Testing", requestID: id, deadline: now.Add(duration)}
	return true
}
func (g *guardDecision) manualObserve(now time.Time, healthy bool, p guardPolicy) (string, bool) {
	m := &g.manual
	switch m.phase {
	case "Testing":
		if !healthy && now.Before(m.deadline) {
			return "TestingBackup", true
		}
		m.result = "Failed"
		if healthy {
			m.result = "Passed"
		}
		m.phase = "Returning"
		duration := time.Duration(p.FailureSeconds+2*p.IntervalSeconds+15) * time.Second
		if duration > 90*time.Second {
			duration = 90 * time.Second
		}
		m.deadline = now.Add(duration)
		g.backup = false
		g.lastAction = now
		g.attempts = append(g.attempts, now)
		g.failureSince = time.Time{}
		return "Reconnect", true
	case "Returning":
		if healthy {
			m.phase = ""
			return "Connected", true
		}
		if !now.Before(m.deadline) {
			m.phase = ""
			m.result = "ReturnFailed"
			g.trial = true
			g.failureSince = now.Add(-time.Duration(p.FailureSeconds) * time.Second)
			return g.observe(now, false, p), true
		}
		return "VerifyingPrimary", true
	}
	return "", false
}
func (g *guardDecision) manualStatus() string {
	if g.manual.phase != "" {
		return g.manual.phase
	}
	return g.manual.result
}

// One controller per SCM service, independent of user sessions. At most three
// disruptive operations in a rolling ten-minute window, including failback trials.
func (g *guardDecision) observe(now time.Time, healthy bool, p guardPolicy) string {
	if !p.Enabled {
		g.failureSince = time.Time{}
		return "Disabled"
	}
	if healthy {
		g.failureSince = time.Time{}
		g.trial = false
	} else if g.failureSince.IsZero() {
		g.failureSince = now
	}
	kept := g.attempts[:0]
	for _, t := range g.attempts {
		if now.Sub(t) < 10*time.Minute {
			kept = append(kept, t)
		}
	}
	g.attempts = kept
	if !healthy && now.Sub(g.failureSince) < time.Duration(p.FailureSeconds)*time.Second {
		return "Confirming"
	}
	due := !healthy || (g.backup && p.ReturnToPrimary && now.Sub(g.lastAction) >= time.Duration(p.PrimaryRetrySeconds)*time.Second)
	if !due {
		if g.backup {
			return "Backup"
		}
		return "Connected"
	}
	if len(g.attempts) >= 3 {
		return "Backoff"
	}
	// A failed primary trial may return to the last working backup without waiting
	// through an extra cooldown; the normal failure confirmation still applies.
	if !g.trial && now.Sub(g.lastAction) < time.Duration(p.CooldownSeconds)*time.Second {
		return "Backoff"
	}
	if healthy && g.backup {
		g.backup = false
		g.trial = true
	} else if p.BackupEndpoint != "" {
		g.backup = !g.backup
		g.trial = false
	}
	g.lastAction = now
	g.attempts = append(g.attempts, now)
	g.failureSince = time.Time{}
	return "Reconnect"
}
