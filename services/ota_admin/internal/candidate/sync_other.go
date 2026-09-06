//go:build !linux

package candidate

func syncStagingRoot(string) error { return nil }
