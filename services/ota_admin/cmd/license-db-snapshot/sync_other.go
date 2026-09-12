//go:build !linux

package main

func syncDirectory(string) error { return nil }
