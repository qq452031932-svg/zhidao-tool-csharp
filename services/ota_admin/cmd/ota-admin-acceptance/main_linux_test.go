//go:build linux && ota_admin_acceptance

package main

import "testing"

func TestSystemdExecutionRejectsServiceContexts(t *testing.T) {
	tests := []struct {
		name        string
		environment bool
		parentPID   int
		parentComm  string
		initComm    string
		cgroup      string
		want        bool
	}{
		{name: "systemd environment", environment: true, parentPID: 42, want: true},
		{name: "direct child of pid one", parentPID: 1, want: true},
		{name: "systemd parent", parentPID: 42, parentComm: "systemd\n", want: true},
		{name: "centos seven system slice", parentPID: 42, initComm: "systemd\n", cgroup: "1:name=systemd:/system.slice/superpan.service\n", want: true},
		{name: "unified service cgroup", parentPID: 42, initComm: "systemd\n", cgroup: "0::/system.slice/superpan.service\n", want: true},
		{name: "interactive systemd host", parentPID: 42, parentComm: "bash\n", initComm: "systemd\n", cgroup: "1:name=systemd:/user.slice/user-1000.slice/session-9.scope\n", want: false},
		{name: "container without systemd", parentPID: 42, parentComm: "sh\n", initComm: "tini\n", cgroup: "0::/docker/abc\n", want: false},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			if got := systemdExecution(test.environment, test.parentPID, test.parentComm, test.initComm, test.cgroup); got != test.want {
				t.Fatalf("systemdExecution() = %v, want %v", got, test.want)
			}
		})
	}
}
