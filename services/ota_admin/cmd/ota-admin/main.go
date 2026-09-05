package main

import (
	"context"
	"flag"
	"fmt"
	"io"
	"os"
	"os/signal"
	"path/filepath"
	"syscall"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/compat"
)

func main() {
	os.Exit(run(os.Args[1:], os.Stdout, os.Stderr))
}

func run(arguments []string, stdout, stderr io.Writer) int {
	if len(arguments) == 0 || arguments[0] != "compat-probe" {
		fmt.Fprintln(stderr, "usage: ota-admin compat-probe [--serve] [--listen 127.0.0.1:PORT] [--work-dir PATH]")
		return 2
	}

	flags := flag.NewFlagSet("compat-probe", flag.ContinueOnError)
	flags.SetOutput(stderr)
	serve := flags.Bool("serve", false, "keep the loopback health endpoint running")
	listenAddress := flags.String("listen", "127.0.0.1:18091", "loopback health listen address")
	workDir := flags.String("work-dir", "", "probe working directory")
	if err := flags.Parse(arguments[1:]); err != nil {
		return 2
	}

	cleanup := func() {}
	if *workDir == "" {
		temporary, err := os.MkdirTemp("", "superpan-ota-admin-probe-")
		if err != nil {
			_ = compat.WriteReport(stdout, compat.Report{FailureCodes: []string{"workspace"}})
			return 1
		}
		*workDir = temporary
		cleanup = func() { _ = os.RemoveAll(temporary) }
	}
	defer cleanup()
	*workDir = filepath.Clean(*workDir)

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	report := compat.Run(ctx, *workDir)
	if !report.OK {
		_ = compat.WriteReport(stdout, report)
		return 1
	}

	var healthServer interface {
		Shutdown(context.Context) error
	}
	if *serve {
		server, _, err := compat.StartHealthServer(report, *listenAddress)
		if err != nil {
			report.OK = false
			report.FailureCodes = append(report.FailureCodes, "serve")
			_ = compat.WriteReport(stdout, report)
			return 1
		}
		healthServer = server
	}

	if err := compat.WriteReport(stdout, report); err != nil {
		return 1
	}
	if !*serve {
		return 0
	}

	<-ctx.Done()
	shutdownContext, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if err := healthServer.Shutdown(shutdownContext); err != nil {
		return 1
	}
	return 0
}
