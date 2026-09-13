package main

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/candidate"
	"fanfh.cn/superpan/ota-admin/internal/config"
	"fanfh.cn/superpan/ota-admin/internal/deploy"
	"fanfh.cn/superpan/ota-admin/internal/publisher"
	"fanfh.cn/superpan/ota-admin/internal/release"
	"fanfh.cn/superpan/ota-admin/internal/store"
	"fanfh.cn/superpan/ota-admin/internal/upload"
)

type bundleReceiver interface {
	Receive(context.Context, io.Reader, int64) (*upload.Envelope, error)
}

type candidateAcceptor interface {
	AcceptNewEnvelopeForActiveSession(context.Context, string, string, *upload.Envelope) (candidate.Summary, error)
}

type bundlePublisher interface {
	PublishForActiveSession(context.Context, string, string, string) (deploy.PublishResult, error)
}

func runPublishBundleFiles(configPath, bundlePath string, stdout, stderr io.Writer) int {
	cfg, err := config.Load(configPath)
	if err != nil || validateExactProductionOTAConfig(cfg) != nil || cfg.ValidateServe() != nil {
		fmt.Fprintln(stderr, "publish-bundle: invalid production configuration")
		return 1
	}
	certificateDER, err := config.ReadTrustedFile(cfg.PublisherCertificatePath, maximumTrustedCertificateBytes)
	if err != nil || validatePublisherCertificate(certificateDER, cfg.PublisherSPKISHA256) != nil {
		fmt.Fprintln(stderr, "publish-bundle: publisher identity rejected")
		return 1
	}
	trust, err := publisher.ProductionTrust()
	if err != nil {
		fmt.Fprintln(stderr, "publish-bundle: publisher trust unavailable")
		return 1
	}
	database, err := store.Open(context.Background(), cfg.DatabasePath)
	if err != nil {
		fmt.Fprintln(stderr, "publish-bundle: open state failed")
		return 1
	}
	defer database.Close()
	receiver, err := upload.NewReceiver(upload.ReceiverOptions{StagingRoot: cfg.StagingRoot, Limits: upload.LimitsFromConfig(cfg.Limits)})
	if err != nil {
		fmt.Fprintln(stderr, "publish-bundle: initialize upload failed")
		return 1
	}
	validator := release.NewValidator(trust)
	candidates, err := candidate.NewService(candidate.Options{Store: database, Validator: validator, StagingRoot: cfg.StagingRoot, Lifetime: cfg.Limits.CandidateLifetime})
	if err != nil {
		fmt.Fprintln(stderr, "publish-bundle: initialize candidate failed")
		return 1
	}
	disk, err := deploy.NewDisk(cfg.ReleaseRoot, nil)
	if err != nil {
		fmt.Fprintln(stderr, "publish-bundle: initialize release storage failed")
		return 1
	}
	publication, err := deploy.NewPublisher(deploy.PublisherOptions{Disk: disk, Store: database, Candidates: candidates, Validator: validator})
	if err != nil || publication.Recover(context.Background()) != nil {
		fmt.Fprintln(stderr, "publish-bundle: release recovery failed")
		return 1
	}
	result, err := publishBundleWithComponents(context.Background(), bundlePath, database, receiver, candidates, publication, rand.Reader, time.Now)
	if err != nil {
		fmt.Fprintln(stderr, "publish-bundle: publication failed")
		return 1
	}
	fmt.Fprintf(stdout, "ota-admin: published version %s\n", result.Version)
	return 0
}

func publishBundleWithComponents(ctx context.Context, bundlePath string, database *store.Store, receiver bundleReceiver, candidates candidateAcceptor, publication bundlePublisher, random io.Reader, now func() time.Time) (result deploy.PublishResult, err error) {
	if database == nil || receiver == nil || candidates == nil || publication == nil || random == nil || now == nil {
		return result, errors.New("publish bundle dependencies are incomplete")
	}
	cleanPath := filepath.Clean(bundlePath)
	if !filepath.IsAbs(cleanPath) {
		return result, errors.New("bundle path must be absolute")
	}
	info, err := os.Lstat(cleanPath)
	if err != nil || !info.Mode().IsRegular() || info.Mode()&os.ModeSymlink != 0 {
		return result, errors.New("bundle must be a regular non-linked file")
	}
	resolved, err := filepath.EvalSymlinks(cleanPath)
	if err != nil || filepath.Clean(resolved) != cleanPath {
		return result, errors.New("bundle path cannot contain symbolic-link aliases")
	}
	sessionID, err := randomHex(random, 32)
	if err != nil {
		return result, err
	}
	createdAt := now().UTC()
	if err := database.CreateSession(ctx, store.Session{
		IDHash: sessionID, State: store.SessionActive, CreatedAt: createdAt, LastSeenAt: createdAt,
		IdleExpiresAt: createdAt.Add(time.Hour), AbsoluteExpiresAt: createdAt.Add(time.Hour),
	}); err != nil {
		return result, fmt.Errorf("create temporary publication session: %w", err)
	}
	defer func() {
		revokeErr := database.TransitionSession(context.Background(), sessionID, store.SessionRevoked)
		if err == nil && revokeErr != nil {
			err = fmt.Errorf("revoke temporary publication session: %w", revokeErr)
		}
	}()
	uploadOperation, err := randomUUID(random)
	if err != nil {
		return result, err
	}
	publishOperation, err := randomUUID(random)
	if err != nil {
		return result, err
	}
	file, err := os.Open(cleanPath)
	if err != nil {
		return result, fmt.Errorf("open bundle: %w", err)
	}
	envelope, receiveErr := receiver.Receive(ctx, file, info.Size())
	closeErr := file.Close()
	if receiveErr != nil || closeErr != nil {
		return result, errors.Join(receiveErr, closeErr)
	}
	summary, err := candidates.AcceptNewEnvelopeForActiveSession(ctx, sessionID, uploadOperation, envelope)
	if err != nil {
		_ = envelope.Cleanup()
		return result, err
	}
	if summary.State != string(store.CandidateValidated) {
		return result, errors.New("candidate was not validated")
	}
	return publication.PublishForActiveSession(ctx, sessionID, summary.CandidateID, publishOperation)
}

func randomHex(random io.Reader, size int) (string, error) {
	value := make([]byte, size)
	if _, err := io.ReadFull(random, value); err != nil {
		return "", errors.New("generate publication identity")
	}
	return hex.EncodeToString(value), nil
}

func randomUUID(random io.Reader) (string, error) {
	value := make([]byte, 16)
	if _, err := io.ReadFull(random, value); err != nil {
		return "", errors.New("generate publication operation")
	}
	value[6] = value[6]&0x0f | 0x40
	value[8] = value[8]&0x3f | 0x80
	return fmt.Sprintf("%08x-%04x-%04x-%04x-%012x", value[0:4], value[4:6], value[6:8], value[8:10], value[10:16]), nil
}
