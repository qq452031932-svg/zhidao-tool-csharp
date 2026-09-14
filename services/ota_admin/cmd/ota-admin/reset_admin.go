package main

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"errors"
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/config"
	"fanfh.cn/superpan/ota-admin/internal/security"
	"fanfh.cn/superpan/ota-admin/internal/store"
)

func runResetAdmin(arguments []string, stdout, stderr io.Writer) int {
	flags := flag.NewFlagSet("reset-admin", flag.ContinueOnError)
	flags.SetOutput(stderr)
	configPath := flags.String("config", "", "root-owned service configuration file")
	outputPath := flags.String("output", "", "new root-only password output file")
	if err := flags.Parse(arguments); err != nil {
		return 2
	}
	if *configPath == "" || *outputPath == "" || flags.NArg() != 0 {
		fmt.Fprintln(stderr, "usage: ota-admin reset-admin --config PATH --output PATH")
		return 2
	}
	cfg, err := config.Load(*configPath)
	if err != nil || validateExactProductionOTAConfig(cfg) != nil || cfg.ValidateServe() != nil {
		fmt.Fprintln(stderr, "reset-admin: invalid production configuration")
		return 1
	}
	passwordBytes := make([]byte, 24)
	if _, err := io.ReadFull(rand.Reader, passwordBytes); err != nil {
		fmt.Fprintln(stderr, "reset-admin: password generation failed")
		return 1
	}
	password := base64.RawURLEncoding.EncodeToString(passwordBytes)
	clear(passwordBytes)
	if err := writeNewPasswordFile(*outputPath, password); err != nil {
		fmt.Fprintln(stderr, "reset-admin: password output failed")
		return 1
	}
	database, err := store.Open(context.Background(), cfg.DatabasePath)
	if err == nil {
		err = resetAdminPassword(context.Background(), database, password, rand.Reader, time.Now)
		closeErr := database.Close()
		if err == nil {
			err = closeErr
		}
	}
	if err != nil {
		_ = os.Remove(*outputPath)
		fmt.Fprintln(stderr, "reset-admin: password reset failed")
		return 1
	}
	fmt.Fprintf(stdout, "ota-admin: administrator password reset; credential stored at %s\n", *outputPath)
	return 0
}

func resetAdminPassword(ctx context.Context, database *store.Store, password string, random io.Reader, now func() time.Time) (err error) {
	if database == nil || random == nil || now == nil {
		return errors.New("reset administrator dependencies are incomplete")
	}
	encodedHash, err := security.HashPassword(password, random)
	if err != nil {
		return err
	}
	currentID, err := randomHex(random, 32)
	if err != nil {
		return err
	}
	nextID, err := randomHex(random, 32)
	if err != nil {
		return err
	}
	if nextID == currentID {
		digest := sha256.Sum256([]byte("replacement-session:" + currentID))
		nextID = fmt.Sprintf("%x", digest[:])
	}
	at := now().UTC()
	session := func(id string) store.Session {
		return store.Session{IDHash: id, State: store.SessionActive, CreatedAt: at, LastSeenAt: at, IdleExpiresAt: at.Add(time.Hour), AbsoluteExpiresAt: at.Add(time.Hour)}
	}
	if err := database.CreateSession(ctx, session(currentID)); err != nil {
		return err
	}
	defer func() {
		_ = database.TransitionSession(context.Background(), currentID, store.SessionRevoked)
	}()
	if err := database.ChangePasswordAndRotateSession(ctx, encodedHash, currentID, session(nextID)); err != nil {
		return err
	}
	return database.TransitionSession(ctx, nextID, store.SessionRevoked)
}

func writeNewPasswordFile(path, password string) error {
	cleanPath := filepath.Clean(path)
	if !filepath.IsAbs(cleanPath) || cleanPath != path {
		return errors.New("password output path must be absolute and canonical")
	}
	file, err := os.OpenFile(cleanPath, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o600)
	if err != nil {
		return err
	}
	if _, err = io.WriteString(file, password+"\n"); err == nil {
		err = file.Sync()
	}
	closeErr := file.Close()
	if err == nil {
		err = closeErr
	}
	if err != nil {
		_ = os.Remove(cleanPath)
	}
	return err
}
