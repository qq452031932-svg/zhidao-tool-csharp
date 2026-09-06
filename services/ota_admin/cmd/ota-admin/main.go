package main

import (
	"context"
	"crypto/rand"
	"errors"
	"flag"
	"fmt"
	"io"
	"os"
	"os/signal"
	"path/filepath"
	"syscall"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/compat"
	"fanfh.cn/superpan/ota-admin/internal/config"
	"fanfh.cn/superpan/ota-admin/internal/store"
)

func main() {
	os.Exit(run(os.Args[1:], os.Stdout, os.Stderr))
}

func run(arguments []string, stdout, stderr io.Writer) int {
	if len(arguments) == 0 {
		writeUsage(stderr)
		return 2
	}
	switch arguments[0] {
	case "compat-probe":
		return runCompatProbe(arguments[1:], stdout, stderr)
	case "init-admin":
		return runInitAdmin(arguments[1:], stdout, stderr)
	case "serve":
		return runServe(arguments[1:], stdout, stderr)
	default:
		writeUsage(stderr)
		return 2
	}
}

func runCompatProbe(arguments []string, stdout, stderr io.Writer) int {
	flags := flag.NewFlagSet("compat-probe", flag.ContinueOnError)
	flags.SetOutput(stderr)
	serve := flags.Bool("serve", false, "keep the loopback health endpoint running")
	listenAddress := flags.String("listen", "127.0.0.1:18091", "loopback health listen address")
	workDir := flags.String("work-dir", "", "probe working directory")
	if err := flags.Parse(arguments); err != nil {
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

func runInitAdmin(arguments []string, stdout, stderr io.Writer) int {
	flags := flag.NewFlagSet("init-admin", flag.ContinueOnError)
	flags.SetOutput(stderr)
	configPath := flags.String("config", "", "root-owned service configuration file")
	ifMissing := flags.Bool("if-missing", false, "succeed without output when an administrator already exists")
	if err := flags.Parse(arguments); err != nil {
		return 2
	}
	if *configPath == "" || flags.NArg() != 0 {
		fmt.Fprintln(stderr, "usage: ota-admin init-admin [--if-missing] --config PATH")
		return 2
	}
	cfg, err := config.Load(*configPath)
	if err != nil {
		fmt.Fprintf(stderr, "init-admin: invalid configuration: %v\n", err)
		return 1
	}
	database, err := store.Open(context.Background(), cfg.DatabasePath)
	if err != nil {
		fmt.Fprintf(stderr, "init-admin: open state store: %v\n", err)
		return 1
	}
	defer database.Close()
	if err := initializeAdmin(database, stdout, rand.Reader, *ifMissing); err != nil {
		fmt.Fprintf(stderr, "init-admin: %v\n", err)
		return 1
	}
	return 0
}

func initializeAdmin(database *store.Store, output io.Writer, random io.Reader, ifMissing bool) error {
	err := database.InitializeAdminWithGeneratedPassword(context.Background(), output, random)
	if ifMissing && errors.Is(err, store.ErrAdminAlreadyInitialized) {
		return nil
	}
	return err
}

func writeUsage(writer io.Writer) {
	fmt.Fprintln(writer, "usage: ota-admin <compat-probe|init-admin|serve> [options]")
}
