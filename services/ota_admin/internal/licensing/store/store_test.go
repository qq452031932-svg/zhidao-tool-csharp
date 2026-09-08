package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"testing"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/licensing/model"
	"github.com/google/uuid"
)

func TestOpenCreatesIsolatedLicenseSchemaAndConfiguresSQLite(t *testing.T) {
	s := openTestStore(t)
	ctx := context.Background()

	var foreignKeys int
	if err := s.db.QueryRowContext(ctx, `PRAGMA foreign_keys`).Scan(&foreignKeys); err != nil {
		t.Fatal(err)
	}
	if foreignKeys != 1 {
		t.Fatalf("foreign_keys = %d, want 1", foreignKeys)
	}
	var journalMode string
	if err := s.db.QueryRowContext(ctx, `PRAGMA journal_mode`).Scan(&journalMode); err != nil {
		t.Fatal(err)
	}
	if !strings.EqualFold(journalMode, "wal") {
		t.Fatalf("journal_mode = %q, want wal", journalMode)
	}
	var busyTimeout int
	if err := s.db.QueryRowContext(ctx, `PRAGMA busy_timeout`).Scan(&busyTimeout); err != nil {
		t.Fatal(err)
	}
	if busyTimeout < 1000 {
		t.Fatalf("busy_timeout = %d, want at least 1000", busyTimeout)
	}
	var versionDDL string
	if err := s.db.QueryRowContext(ctx, `SELECT sql FROM sqlite_master WHERE type='table' AND name='license_schema_version'`).Scan(&versionDDL); err != nil {
		t.Fatal(err)
	}
	if strings.Contains(strings.ToLower(strings.Join(strings.Fields(versionDDL), " ")), "check (version=1)") {
		t.Fatalf("license_schema_version DDL pins version 1 and cannot support a later migration: %s", versionDDL)
	}

	rows, err := s.db.QueryContext(ctx, `SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'`)
	if err != nil {
		t.Fatal(err)
	}
	defer rows.Close()
	for rows.Next() {
		var name string
		if err := rows.Scan(&name); err != nil {
			t.Fatal(err)
		}
		if !strings.HasPrefix(name, "license_") {
			t.Errorf("non-license table %q exists in license database", name)
		}
	}
	if err := rows.Err(); err != nil {
		t.Fatal(err)
	}
}

func TestAdminSchemaMigratesV1AndIdempotencyIsPersistentBoundedAndPruned(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "license-v1.db")
	raw, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := raw.ExecContext(ctx, licenseSchemaV1); err != nil {
		t.Fatal(err)
	}
	if err := raw.Close(); err != nil {
		t.Fatal(err)
	}
	s, err := Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	now := time.Date(2026, 9, 9, 12, 0, 0, 0, time.UTC)
	record := model.AdminIdempotencyRecord{Fingerprint: strings.Repeat("a", 64), Status: 201, Body: []byte(`{"ok":true}`), ContentType: "application/json", ExpiresAt: now.Add(10 * time.Hour)}
	key := uuid.NewString()
	if err := s.PutAdminIdempotency(ctx, key, record, now, 2); err != nil {
		t.Fatal(err)
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s, err = Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close()
	got, found, err := s.GetAdminIdempotency(ctx, key, now)
	if err != nil || !found || got.Status != record.Status || string(got.Body) != string(record.Body) || got.ContentType != record.ContentType {
		t.Fatalf("persistent record=%+v found=%v err=%v", got, found, err)
	}
	expiringKey := uuid.NewString()
	record.Fingerprint = strings.Repeat("b", 64)
	record.ExpiresAt = now.Add(time.Hour)
	if err := s.PutAdminIdempotency(ctx, expiringKey, record, now, 3); err != nil {
		t.Fatal(err)
	}
	pendingKey := uuid.NewString()
	record.Fingerprint = strings.Repeat("c", 64)
	record.Status = 102
	record.ExpiresAt = now.Add(time.Hour)
	if err := s.PutAdminIdempotency(ctx, pendingKey, record, now, 3); err != nil {
		t.Fatal(err)
	}
	pressure := record
	pressure.Fingerprint = strings.Repeat("d", 64)
	pressure.Status = 201
	pressure.ExpiresAt = now.Add(10 * time.Hour)
	if err := s.PutAdminIdempotency(ctx, uuid.NewString(), pressure, now, 3); !errors.Is(err, ErrAdminIdempotencyCapacity) {
		t.Fatalf("capacity error=%v, want ErrAdminIdempotencyCapacity", err)
	}
	var count int
	if err := s.db.QueryRowContext(ctx, `SELECT count(*) FROM license_admin_idempotency`).Scan(&count); err != nil || count != 3 {
		t.Fatalf("bounded count=%d err=%v", count, err)
	}
	if replay, found, err := s.GetAdminIdempotency(ctx, key, now); err != nil || !found || string(replay.Body) != string(got.Body) {
		t.Fatalf("live replay evicted or changed: found=%v record=%+v err=%v", found, replay, err)
	}
	if _, _, err := s.GetAdminIdempotency(ctx, uuid.NewString(), now.Add(2*time.Hour)); err != nil {
		t.Fatal(err)
	}
	if pending, found, err := s.GetAdminIdempotency(ctx, pendingKey, now.Add(2*time.Hour)); err != nil || !found || pending.Status != 102 {
		t.Fatalf("pending reservation was pruned: found=%v record=%+v err=%v", found, pending, err)
	}
	if err := s.db.QueryRowContext(ctx, `SELECT count(*) FROM license_admin_idempotency`).Scan(&count); err != nil || count != 2 {
		t.Fatalf("completed-expired prune count=%d err=%v", count, err)
	}
	var version int
	if err := s.db.QueryRowContext(ctx, `SELECT version FROM license_schema_version WHERE singleton_id=1`).Scan(&version); err != nil || version != licenseSchemaVersion {
		t.Fatalf("version=%d err=%v", version, err)
	}
}

func TestAdminMutationRollsBackWhenIdempotencyCompletionFails(t *testing.T) {
	s := openTestStore(t)
	ctx := context.Background()
	if _, err := s.db.ExecContext(ctx, `CREATE TRIGGER license_test_fail_admin_completion BEFORE INSERT ON license_admin_idempotency BEGIN SELECT RAISE(ABORT,'forced completion failure'); END`); err != nil {
		t.Fatal(err)
	}
	now := time.Date(2026, 9, 9, 13, 0, 0, 0, time.UTC)
	key := uuid.NewString()
	fingerprint := strings.Repeat("f", 64)
	tx, _, outcome, err := s.BeginAdminMutation(ctx, key, fingerprint, now, now.Add(time.Hour), 10, 1<<20)
	if err != nil || outcome != model.AdminIdempotencyNew {
		t.Fatalf("begin outcome=%q err=%v", outcome, err)
	}
	if _, err := tx.CreateSoftwareAt(ctx, "Rollback Product", now); err != nil {
		t.Fatal(err)
	}
	record := model.AdminIdempotencyRecord{Fingerprint: fingerprint, Status: 201, Body: []byte("encrypted"), ContentType: "application/json", ExpiresAt: now.Add(time.Hour)}
	if err := tx.Complete(ctx, key, record); err == nil {
		t.Fatal("completion failure was accepted")
	}
	var software, idempotency int
	if err := s.db.QueryRowContext(ctx, `SELECT count(*) FROM license_software WHERE name='Rollback Product'`).Scan(&software); err != nil {
		t.Fatal(err)
	}
	if err := s.db.QueryRowContext(ctx, `SELECT count(*) FROM license_admin_idempotency WHERE key=?`, key).Scan(&idempotency); err != nil {
		t.Fatal(err)
	}
	if software != 0 || idempotency != 0 {
		t.Fatalf("partial commit software=%d idempotency=%d", software, idempotency)
	}
}

func TestAdminOrdinaryCardSearchDoesNotReturnSecretColumns(t *testing.T) {
	s := openTestStore(t)
	ctx := context.Background()
	software := mustSoftware(t, s, "Safe Search Product")
	pkg, err := s.CreatePackage(ctx, "Safe Search Package", []string{software.ID})
	if err != nil {
		t.Fatal(err)
	}
	card := mustCard(t, s, pkg.ID, 30, nil)
	items, total, err := s.SearchAdminCards(ctx, model.AdminCardFilter{}, time.Now().UTC(), 0, 10)
	if err != nil || total != 1 || len(items) != 1 {
		t.Fatalf("search total=%d items=%d err=%v", total, len(items), err)
	}
	if items[0].ID != card.ID || len(items[0].Ciphertext) != 0 || items[0].FullDigest != "" {
		t.Fatalf("ordinary search returned secret columns: %+v", items[0])
	}
}

func TestSoftwareIdentifiersAreUUIDsImmutableAndSoftwareIsToggledNotDeleted(t *testing.T) {
	s := openTestStore(t)
	ctx := context.Background()

	software, err := s.CreateSoftware(ctx, "SuperPan")
	if err != nil {
		t.Fatal(err)
	}
	assertUUID(t, software.ID)
	if !software.Enabled {
		t.Fatal("new software is disabled, want enabled")
	}
	if err := s.SetSoftwareEnabled(ctx, software.ID, false); err != nil {
		t.Fatal(err)
	}
	var enabled bool
	if err := s.db.QueryRowContext(ctx, `SELECT enabled FROM license_software WHERE id=?`, software.ID).Scan(&enabled); err != nil {
		t.Fatal(err)
	}
	if enabled {
		t.Fatal("software remains enabled")
	}
	if err := s.SetSoftwareEnabled(ctx, software.ID, true); err != nil {
		t.Fatal(err)
	}
	if err := s.db.QueryRowContext(ctx, `SELECT enabled FROM license_software WHERE id=?`, software.ID).Scan(&enabled); err != nil {
		t.Fatal(err)
	}
	if !enabled {
		t.Fatal("software remains disabled after re-enable")
	}
	if _, err := s.db.ExecContext(ctx, `DELETE FROM license_software WHERE id=?`, software.ID); err == nil {
		t.Fatal("direct software deletion succeeded, want deletion rejected")
	}
	if _, err := s.db.ExecContext(ctx, `UPDATE license_software SET id=? WHERE id=?`, uuid.NewString(), software.ID); err == nil {
		t.Fatal("software identifier update succeeded, want immutable identifier")
	}
	var count int
	if err := s.db.QueryRowContext(ctx, `SELECT count(*) FROM license_software WHERE id=?`, software.ID).Scan(&count); err != nil || count != 1 {
		t.Fatalf("software row count = %d, err = %v, want retained row", count, err)
	}
}

func TestPackageNamesUseBoundedUTF8WireLength(t *testing.T) {
	s := openTestStore(t)
	software := mustSoftware(t, s, "Package Name Product")
	exact := strings.Repeat("界", 42) + "ab"
	if len(exact) != model.MaxPackageNameUTF8Bytes {
		t.Fatalf("test name bytes = %d", len(exact))
	}
	if _, err := s.CreatePackage(context.Background(), exact, []string{software.ID}); err != nil {
		t.Fatalf("CreatePackage exact boundary: %v", err)
	}
	if _, err := s.CreatePackage(context.Background(), exact+"c", []string{software.ID}); err == nil {
		t.Fatal("CreatePackage accepted an overlong UTF-8 package name")
	}
}

func TestOpenRejectsPersistedPackageNameOutsideWireInvariant(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "corrupt-package.db")
	s, err := Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	software, err := s.CreateSoftware(ctx, "Corrupt Package Product")
	if err != nil {
		t.Fatal(err)
	}
	pkg, err := s.CreatePackage(ctx, "valid", []string{software.ID})
	if err != nil {
		t.Fatal(err)
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	raw, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := raw.ExecContext(ctx, `PRAGMA ignore_check_constraints=ON`); err != nil {
		t.Fatal(err)
	}
	if _, err := raw.ExecContext(ctx, `UPDATE license_packages SET name=? WHERE id=?`, strings.Repeat("x", model.MaxPackageNameUTF8Bytes+1), pkg.ID); err != nil {
		t.Fatal(err)
	}
	if err := raw.Close(); err != nil {
		t.Fatal(err)
	}
	if reopened, err := Open(ctx, path); err == nil {
		reopened.Close()
		t.Fatal("Open accepted an existing package name outside the wire invariant")
	}
}

func TestPackageMembershipReplacementIsAtomicAndIdentifiersAreImmutable(t *testing.T) {
	s := openTestStore(t)
	ctx := context.Background()
	a := mustSoftware(t, s, "A")
	b := mustSoftware(t, s, "B")
	c := mustSoftware(t, s, "C")

	pkg, err := s.CreatePackage(ctx, "Suite", []string{a.ID, b.ID})
	if err != nil {
		t.Fatal(err)
	}
	assertUUID(t, pkg.ID)
	assertStrings(t, pkg.SoftwareIDs, []string{a.ID, b.ID})
	if err := s.ReplacePackageMembers(ctx, pkg.ID, []string{b.ID, c.ID}); err != nil {
		t.Fatal(err)
	}
	assertStrings(t, packageMembers(t, s, pkg.ID), []string{b.ID, c.ID})

	err = s.ReplacePackageMembers(ctx, pkg.ID, []string{a.ID, uuid.NewString()})
	if err == nil {
		t.Fatal("replacement with unknown software succeeded")
	}
	assertStrings(t, packageMembers(t, s, pkg.ID), []string{b.ID, c.ID})
	if _, err := s.db.ExecContext(ctx, `UPDATE license_packages SET id=? WHERE id=?`, uuid.NewString(), pkg.ID); err == nil {
		t.Fatal("package identifier update succeeded")
	}
}

func TestGenerateCardsPersistsSecretIndexesDurationsAndNullableActivation(t *testing.T) {
	s := openTestStore(t)
	ctx := context.Background()
	pkg := mustPackage(t, s)
	note := "September Batch"
	createdAt := time.Date(2026, 9, 8, 9, 8, 7, 654321000, time.UTC)
	sealed := []model.SealedCard{
		{ID: uuid.NewString(), Ciphertext: []byte("cipher-one"), FullDigest: strings.Repeat("a", 64), Prefix8: "ABCDEFGH", Last4: "WXYZ"},
		{ID: uuid.NewString(), Ciphertext: []byte("cipher-two"), FullDigest: strings.Repeat("b", 64), Prefix8: "12345678", Last4: "9876"},
	}
	cards, err := s.GenerateCards(ctx, model.GenerateCardsRequest{PackageID: pkg.ID, Count: 2, BaseDays: 90, Note: &note, CreatedAt: createdAt}, sealed)
	if err != nil {
		t.Fatal(err)
	}
	if len(cards) != 2 {
		t.Fatalf("card count = %d, want 2", len(cards))
	}
	for i, card := range cards {
		assertUUID(t, card.ID)
		if card.ID != sealed[i].ID || card.BaseDays != 90 || card.UnactivatedTotalDays != 90 {
			t.Fatalf("card %d = %+v", i, card)
		}
		if !card.CreatedAt.Equal(createdAt.Truncate(time.Second)) {
			t.Fatalf("card %d created_at = %v, want %v", i, card.CreatedAt, createdAt.Truncate(time.Second))
		}
		if card.ActivatedAt != nil || card.ExpiresAt != nil || card.MachineHash != nil {
			t.Fatalf("new card activation fields = %+v, want nil", card)
		}
	}

	var ciphertext []byte
	var digest, prefix8, last4 string
	var baseDays, unactivated int
	var activatedAt, expiresAt, machineHash any
	if err := s.db.QueryRowContext(ctx, `SELECT ciphertext,full_digest,prefix8,last4,base_days,unactivated_total_days,activated_at,expires_at,machine_hash FROM license_cards WHERE id=?`, sealed[0].ID).
		Scan(&ciphertext, &digest, &prefix8, &last4, &baseDays, &unactivated, &activatedAt, &expiresAt, &machineHash); err != nil {
		t.Fatal(err)
	}
	if string(ciphertext) != "cipher-one" || digest != sealed[0].FullDigest || prefix8 != "ABCDEFGH" || last4 != "WXYZ" || baseDays != 90 || unactivated != 90 {
		t.Fatalf("stored card fields = %q %q %q %q %d %d", ciphertext, digest, prefix8, last4, baseDays, unactivated)
	}
	if activatedAt != nil || expiresAt != nil || machineHash != nil {
		t.Fatalf("stored nullable fields = %#v %#v %#v, want nil", activatedAt, expiresAt, machineHash)
	}
	if _, err := s.db.ExecContext(ctx, `UPDATE license_cards SET id=? WHERE id=?`, uuid.NewString(), sealed[0].ID); err == nil {
		t.Fatal("card identifier update succeeded")
	}
}

func TestCardSearchUsesExactSecretIndexesAndCaseInsensitiveLiteralNoteContains(t *testing.T) {
	s := openTestStore(t)
	ctx := context.Background()
	pkg := mustPackage(t, s)
	note1, note2 := "September VIP 100%", "october"
	one := model.SealedCard{ID: uuid.NewString(), Ciphertext: []byte("c1"), FullDigest: strings.Repeat("1", 64), Prefix8: "PREFIX01", Last4: "0001"}
	two := model.SealedCard{ID: uuid.NewString(), Ciphertext: []byte("c2"), FullDigest: strings.Repeat("2", 64), Prefix8: "PREFIX02", Last4: "0002"}
	createdAt := time.Date(2026, 9, 8, 10, 0, 0, 0, time.UTC)
	if _, err := s.GenerateCards(ctx, model.GenerateCardsRequest{PackageID: pkg.ID, Count: 1, BaseDays: 30, Note: &note1, CreatedAt: createdAt}, []model.SealedCard{one}); err != nil {
		t.Fatal(err)
	}
	if _, err := s.GenerateCards(ctx, model.GenerateCardsRequest{PackageID: pkg.ID, Count: 1, BaseDays: 30, Note: &note2, CreatedAt: createdAt}, []model.SealedCard{two}); err != nil {
		t.Fatal(err)
	}

	checks := []struct {
		name   string
		filter model.CardFilter
		wantID string
	}{
		{name: "full digest", filter: model.CardFilter{FullDigest: one.FullDigest}, wantID: one.ID},
		{name: "prefix8", filter: model.CardFilter{Prefix8: one.Prefix8}, wantID: one.ID},
		{name: "last4", filter: model.CardFilter{Last4: one.Last4}, wantID: one.ID},
		{name: "ASCII case-insensitive note", filter: model.CardFilter{NoteContains: "september"}, wantID: one.ID},
		{name: "literal percent in note", filter: model.CardFilter{NoteContains: "100%"}, wantID: one.ID},
	}
	for _, check := range checks {
		t.Run(check.name, func(t *testing.T) {
			got, err := s.SearchCards(ctx, check.filter)
			if err != nil {
				t.Fatal(err)
			}
			if len(got) != 1 || got[0].ID != check.wantID {
				t.Fatalf("SearchCards() = %+v, want only %s", got, check.wantID)
			}
		})
	}
	got, err := s.SearchCards(ctx, model.CardFilter{FullDigest: strings.Repeat("1", 63)})
	if err != nil || len(got) != 0 {
		t.Fatalf("partial digest search = %+v, %v; want no fuzzy secret match", got, err)
	}
}

func TestActivateIsIdempotentAndConcurrentCallsDoNotLeakBusyErrors(t *testing.T) {
	s := openTestStore(t)
	ctx := context.Background()
	software := mustSoftware(t, s, "Concurrent Product")
	pkg, err := s.CreatePackage(ctx, "Concurrent Package", []string{software.ID})
	if err != nil {
		t.Fatal(err)
	}
	card := mustCard(t, s, pkg.ID, 30, nil)
	now := time.Date(2026, 9, 8, 10, 0, 0, 0, time.UTC)
	mutation := model.ActivationMutation{
		RequestID: uuid.NewString(), CardID: card.ID, SoftwareID: software.ID, MachineHash: strings.Repeat("a", 64),
		ObservedAt: now, ActivatedAt: now, ExpiresAt: now.Add(30 * 24 * time.Hour), LeaseUntil: now.Add(time.Hour),
	}

	const workers = 20
	var wg sync.WaitGroup
	errs := make(chan error, workers)
	states := make(chan model.ActivationState, workers)
	for i := 0; i < workers; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			state, err := s.Activate(ctx, mutation)
			if err != nil {
				errs <- err
				return
			}
			states <- state
		}()
	}
	wg.Wait()
	close(errs)
	close(states)
	for err := range errs {
		if strings.Contains(strings.ToLower(err.Error()), "busy") || strings.Contains(strings.ToLower(err.Error()), "locked") {
			t.Fatalf("SQLite contention leaked from Activate: %v", err)
		}
		t.Fatalf("Activate error: %v", err)
	}
	for state := range states {
		if state.CardID != card.ID || state.MachineHash != mutation.MachineHash || !state.ExpiresAt.Equal(mutation.ExpiresAt) {
			t.Fatalf("activation state = %+v", state)
		}
	}
	var requests, bindings, leases int
	if err := s.db.QueryRowContext(ctx, `SELECT count(*) FROM license_activation_requests WHERE request_id=? AND card_id=? AND software_id=? AND machine_hash=?`, mutation.RequestID, card.ID, software.ID, mutation.MachineHash).Scan(&requests); err != nil {
		t.Fatal(err)
	}
	if err := s.db.QueryRowContext(ctx, `SELECT count(*) FROM license_activation_bindings WHERE card_id=?`, card.ID).Scan(&bindings); err != nil {
		t.Fatal(err)
	}
	if err := s.db.QueryRowContext(ctx, `SELECT count(*) FROM license_leases WHERE card_id=?`, card.ID).Scan(&leases); err != nil {
		t.Fatal(err)
	}
	if requests != 1 || bindings != 1 || leases != 1 {
		t.Fatalf("requests/bindings/leases = %d/%d/%d, want 1/1/1", requests, bindings, leases)
	}
}

func TestFirstActivationUsesAuthoritativeUnusedDaysAndClampsLease(t *testing.T) {
	s := openTestStore(t)
	ctx := context.Background()
	software := mustSoftware(t, s, "Authoritative Activation")
	pkg, err := s.CreatePackage(ctx, "Authoritative Activation Package", []string{software.ID})
	if err != nil {
		t.Fatal(err)
	}
	card := mustCard(t, s, pkg.ID, 30, nil)
	observedAt := time.Date(2026, 9, 9, 10, 0, 0, 0, time.UTC)
	if err := s.RenewCard(ctx, card.ID, 90, observedAt); err != nil {
		t.Fatal(err)
	}
	state, err := s.Activate(ctx, model.ActivationMutation{
		RequestID:   uuid.NewString(),
		CardID:      card.ID,
		SoftwareID:  software.ID,
		MachineHash: strings.Repeat("a", 64),
		ObservedAt:  observedAt,
		ActivatedAt: observedAt.Add(-24 * time.Hour),
		ExpiresAt:   observedAt.Add(24 * time.Hour),
		LeaseUntil:  observedAt.Add(200 * 24 * time.Hour),
	})
	if err != nil {
		t.Fatal(err)
	}
	wantExpiry := observedAt.Add(120 * 24 * time.Hour)
	if !state.ActivatedAt.Equal(observedAt) || !state.ExpiresAt.Equal(wantExpiry) || !state.LeaseUntil.Equal(wantExpiry) {
		t.Fatalf("authoritative first activation = %+v, want expiry/lease %v", state, wantExpiry)
	}
	var lastSeen int64
	if err := s.db.QueryRowContext(ctx, `SELECT last_seen_at FROM license_leases WHERE card_id=? AND software_id=?`, card.ID, software.ID).Scan(&lastSeen); err != nil {
		t.Fatal(err)
	}
	if lastSeen != observedAt.Unix() {
		t.Fatalf("first activation last_seen_at = %v, want %v", time.Unix(lastSeen, 0).UTC(), observedAt)
	}
}

func TestActivateRejectsACompetingMachineAtomically(t *testing.T) {
	s := openTestStore(t)
	ctx := context.Background()
	software := mustSoftware(t, s, "Bound Product")
	pkg, err := s.CreatePackage(ctx, "Bound Package", []string{software.ID})
	if err != nil {
		t.Fatal(err)
	}
	card := mustCard(t, s, pkg.ID, 30, nil)
	now := time.Date(2026, 9, 8, 10, 0, 0, 0, time.UTC)
	base := model.ActivationMutation{RequestID: uuid.NewString(), CardID: card.ID, SoftwareID: software.ID, MachineHash: strings.Repeat("a", 64), ObservedAt: now, ActivatedAt: now, ExpiresAt: now.Add(24 * time.Hour), LeaseUntil: now.Add(time.Hour)}
	if _, err := s.Activate(ctx, base); err != nil {
		t.Fatal(err)
	}
	base.RequestID = uuid.NewString()
	base.MachineHash = strings.Repeat("b", 64)
	if _, err := s.Activate(ctx, base); !errors.Is(err, ErrMachineBound) {
		t.Fatalf("Activate competing machine error = %v, want ErrMachineBound", err)
	}
}

func TestActivationPreservesAuthoritativeLifetimeAfterRenewalAndUnbind(t *testing.T) {
	s := openTestStore(t)
	ctx := context.Background()
	softwareA := mustSoftware(t, s, "Lifetime Product A")
	softwareB := mustSoftware(t, s, "Lifetime Product B")
	pkg, err := s.CreatePackage(ctx, "Lifetime Package", []string{softwareA.ID, softwareB.ID})
	if err != nil {
		t.Fatal(err)
	}
	card := mustCard(t, s, pkg.ID, 30, nil)
	now := time.Date(2026, 9, 8, 10, 0, 0, 0, time.UTC)
	initialExpiry := now.Add(30 * 24 * time.Hour)
	initial := model.ActivationMutation{RequestID: uuid.NewString(), CardID: card.ID, SoftwareID: softwareA.ID, MachineHash: strings.Repeat("a", 64), ObservedAt: now, ActivatedAt: now, ExpiresAt: initialExpiry, LeaseUntil: now.Add(time.Hour)}
	if _, err := s.Activate(ctx, initial); err != nil {
		t.Fatal(err)
	}
	if err := s.RenewCard(ctx, card.ID, 30, now); err != nil {
		t.Fatal(err)
	}
	wantExpiry := initialExpiry.Add(30 * 24 * time.Hour)

	stale := model.ActivationMutation{RequestID: uuid.NewString(), CardID: card.ID, SoftwareID: softwareB.ID, MachineHash: initial.MachineHash, ObservedAt: now.Add(5 * 24 * time.Hour), ActivatedAt: now.Add(5 * 24 * time.Hour), ExpiresAt: now.Add(6 * 24 * time.Hour), LeaseUntil: now.Add(5*24*time.Hour + 2*time.Hour)}
	state, err := s.Activate(ctx, stale)
	if err != nil {
		t.Fatal(err)
	}
	if !state.ActivatedAt.Equal(now) || !state.ExpiresAt.Equal(wantExpiry) {
		t.Fatalf("second-product activation overwrote lifetime: %+v, want activated %v expiry %v", state, now, wantExpiry)
	}
	summary := mustSearchOne(t, s, model.CardFilter{FullDigest: card.FullDigest})
	if summary.ActivatedAt == nil || !summary.ActivatedAt.Equal(now) || summary.ExpiresAt == nil || !summary.ExpiresAt.Equal(wantExpiry) {
		t.Fatalf("stored lifetime after second product = %+v", summary)
	}
	for table, predicate := range map[string]string{
		"license_activation_bindings": `card_id=? AND software_id=?`,
		"license_activation_requests": `card_id=? AND software_id=?`,
	} {
		var activatedAt, expiresAt int64
		query := `SELECT activated_at,expires_at FROM ` + table + ` WHERE ` + predicate
		if err := s.db.QueryRowContext(ctx, query, card.ID, softwareB.ID).Scan(&activatedAt, &expiresAt); err != nil {
			t.Fatal(err)
		}
		if activatedAt != now.Unix() || expiresAt != wantExpiry.Unix() {
			t.Fatalf("%s lifetime = %d/%d, want %d/%d", table, activatedAt, expiresAt, now.Unix(), wantExpiry.Unix())
		}
	}

	if err := s.UnbindCard(ctx, card.ID); err != nil {
		t.Fatal(err)
	}
	rebind := model.ActivationMutation{RequestID: uuid.NewString(), CardID: card.ID, SoftwareID: softwareA.ID, MachineHash: strings.Repeat("b", 64), ObservedAt: now.Add(7 * 24 * time.Hour), ActivatedAt: now.Add(7 * 24 * time.Hour), ExpiresAt: now.Add(8 * 24 * time.Hour), LeaseUntil: now.Add(7*24*time.Hour + 3*time.Hour)}
	state, err = s.Activate(ctx, rebind)
	if err != nil {
		t.Fatal(err)
	}
	if !state.ActivatedAt.Equal(now) || !state.ExpiresAt.Equal(wantExpiry) {
		t.Fatalf("post-unbind activation overwrote lifetime: %+v, want activated %v expiry %v", state, now, wantExpiry)
	}
}

func TestActivationReturnsCanonicalPersistedTimeOnFirstCallAndReplay(t *testing.T) {
	s := openTestStore(t)
	ctx := context.Background()
	software := mustSoftware(t, s, "Canonical Time Product")
	pkg, err := s.CreatePackage(ctx, "Canonical Time Package", []string{software.ID})
	if err != nil {
		t.Fatal(err)
	}
	card := mustCard(t, s, pkg.ID, 30, nil)
	now := time.Date(2026, 9, 8, 10, 0, 0, 123456789, time.UTC)
	mutation := model.ActivationMutation{RequestID: uuid.NewString(), CardID: card.ID, SoftwareID: software.ID, MachineHash: strings.Repeat("c", 64), ObservedAt: now, ActivatedAt: now, ExpiresAt: now.Add(30 * 24 * time.Hour), LeaseUntil: now.Add(time.Hour)}
	first, err := s.Activate(ctx, mutation)
	if err != nil {
		t.Fatal(err)
	}
	replay, err := s.Activate(ctx, mutation)
	if err != nil {
		t.Fatal(err)
	}
	if first != replay {
		t.Fatalf("first activation = %+v, replay = %+v; want byte-stable persisted state", first, replay)
	}
	if first.ActivatedAt.Nanosecond() != 0 || first.ExpiresAt.Nanosecond() != 0 || first.LeaseUntil.Nanosecond() != 0 {
		t.Fatalf("activation state was not normalized to SQLite precision: %+v", first)
	}
}

func TestRenewCardExtendsActivatedFromMaxAndAccumulatesUnactivatedDays(t *testing.T) {
	s := openTestStore(t)
	ctx := context.Background()
	software := mustSoftware(t, s, "Renew Product")
	pkg, err := s.CreatePackage(ctx, "Renew Package", []string{software.ID})
	if err != nil {
		t.Fatal(err)
	}
	now := time.Date(2026, 9, 8, 10, 0, 0, 0, time.UTC)
	unused := mustCard(t, s, pkg.ID, 30, nil)
	if err := s.RenewCard(ctx, unused.ID, 90, now); err != nil {
		t.Fatal(err)
	}
	unusedSummary := mustSearchOne(t, s, model.CardFilter{FullDigest: unused.FullDigest})
	if unusedSummary.UnactivatedTotalDays != 120 || unusedSummary.ExpiresAt != nil {
		t.Fatalf("unused renewal = %+v", unusedSummary)
	}

	active := mustCard(t, s, pkg.ID, 30, nil)
	activationObservation := now.Add(-60 * 24 * time.Hour)
	if _, err := s.Activate(ctx, model.ActivationMutation{RequestID: uuid.NewString(), CardID: active.ID, SoftwareID: software.ID, MachineHash: strings.Repeat("c", 64), ObservedAt: activationObservation, ActivatedAt: activationObservation, ExpiresAt: now.Add(-24 * time.Hour), LeaseUntil: activationObservation.Add(time.Hour)}); err != nil {
		t.Fatal(err)
	}
	if err := s.RenewCard(ctx, active.ID, 30, now); err != nil {
		t.Fatal(err)
	}
	activeSummary := mustSearchOne(t, s, model.CardFilter{FullDigest: active.FullDigest})
	want := now.Add(30 * 24 * time.Hour)
	if activeSummary.ExpiresAt == nil || !activeSummary.ExpiresAt.Equal(want) {
		t.Fatalf("active expiry = %v, want %v", activeSummary.ExpiresAt, want)
	}

	futureActive := mustCard(t, s, pkg.ID, 30, nil)
	futureExpiry := now.Add(10 * 24 * time.Hour)
	futureObservation := now.Add(-20 * 24 * time.Hour)
	if _, err := s.Activate(ctx, model.ActivationMutation{RequestID: uuid.NewString(), CardID: futureActive.ID, SoftwareID: software.ID, MachineHash: strings.Repeat("d", 64), ObservedAt: futureObservation, ActivatedAt: futureObservation, ExpiresAt: futureExpiry, LeaseUntil: futureObservation.Add(time.Hour)}); err != nil {
		t.Fatal(err)
	}
	if err := s.RenewCard(ctx, futureActive.ID, 90, now); err != nil {
		t.Fatal(err)
	}
	futureSummary := mustSearchOne(t, s, model.CardFilter{FullDigest: futureActive.FullDigest})
	wantFuture := futureExpiry.Add(90 * 24 * time.Hour)
	if futureSummary.ExpiresAt == nil || !futureSummary.ExpiresAt.Equal(wantFuture) {
		t.Fatalf("future active expiry = %v, want %v", futureSummary.ExpiresAt, wantFuture)
	}
}

func TestRenewCardRejectsIntegerAndDurationOverflowWithoutMutation(t *testing.T) {
	s := openTestStore(t)
	ctx := context.Background()
	software := mustSoftware(t, s, "Overflow Product")
	pkg, err := s.CreatePackage(ctx, "Overflow Package", []string{software.ID})
	if err != nil {
		t.Fatal(err)
	}
	now := time.Date(2026, 9, 8, 10, 0, 0, 0, time.UTC)
	maxInt := int(^uint(0) >> 1)

	unused := mustCard(t, s, pkg.ID, maxInt, nil)
	if err := s.RenewCard(ctx, unused.ID, 1, now); err == nil {
		t.Fatal("unactivated renewal overflow succeeded")
	}
	unusedSummary := mustSearchOne(t, s, model.CardFilter{FullDigest: unused.FullDigest})
	if unusedSummary.UnactivatedTotalDays != maxInt {
		t.Fatalf("failed unactivated renewal mutated total days to %d, want %d", unusedSummary.UnactivatedTotalDays, maxInt)
	}

	active := mustCard(t, s, pkg.ID, 30, nil)
	expiry := now.Add(30 * 24 * time.Hour)
	if _, err := s.Activate(ctx, model.ActivationMutation{RequestID: uuid.NewString(), CardID: active.ID, SoftwareID: software.ID, MachineHash: strings.Repeat("f", 64), ObservedAt: now, ActivatedAt: now, ExpiresAt: expiry, LeaseUntil: now.Add(time.Hour)}); err != nil {
		t.Fatal(err)
	}
	if err := s.RenewCard(ctx, active.ID, maxInt, now); err == nil {
		t.Fatal("activated renewal duration overflow succeeded")
	}
	activeSummary := mustSearchOne(t, s, model.CardFilter{FullDigest: active.FullDigest})
	if activeSummary.ExpiresAt == nil || !activeSummary.ExpiresAt.Equal(expiry) {
		t.Fatalf("failed activated renewal mutated expiry to %v, want %v", activeSummary.ExpiresAt, expiry)
	}
}

func TestReplayNonceClaimPersistsUntilExpiry(t *testing.T) {
	s := openTestStore(t)
	ctx := context.Background()
	now := time.Date(2026, 9, 8, 10, 0, 0, 0, time.UTC)
	claimed, err := s.ClaimReplayNonce(ctx, "nonce-1", now.Add(time.Minute), now, 100)
	if err != nil || !claimed {
		t.Fatalf("first claim = %v, %v", claimed, err)
	}
	claimed, err = s.ClaimReplayNonce(ctx, "nonce-1", now.Add(2*time.Minute), now.Add(30*time.Second), 100)
	if err != nil || claimed {
		t.Fatalf("replay claim = %v, %v; want rejected", claimed, err)
	}
	claimed, err = s.ClaimReplayNonce(ctx, "nonce-1", now.Add(3*time.Minute), now.Add(61*time.Second), 100)
	if err != nil || !claimed {
		t.Fatalf("post-expiry claim = %v, %v; want accepted", claimed, err)
	}
}

func TestReplayNonceDoesNotExpireEarlyAtSubsecondBoundary(t *testing.T) {
	s := openTestStore(t)
	ctx := context.Background()
	second := time.Date(2026, 9, 8, 10, 0, 0, 0, time.UTC)
	firstNow := second.Add(100 * time.Millisecond)
	expiresAt := second.Add(900 * time.Millisecond)
	claimed, err := s.ClaimReplayNonce(ctx, "subsecond-nonce", expiresAt, firstNow, 100)
	if err != nil || !claimed {
		t.Fatalf("first claim = %v, %v", claimed, err)
	}

	claimed, err = s.ClaimReplayNonce(ctx, "subsecond-nonce", second.Add(2*time.Second), second.Add(800*time.Millisecond), 100)
	if err != nil || claimed {
		t.Fatalf("claim before precise expiry = %v, %v; want replay rejected", claimed, err)
	}
	claimed, err = s.ClaimReplayNonce(ctx, "subsecond-nonce", second.Add(3*time.Second), second.Add(time.Second), 100)
	if err != nil || !claimed {
		t.Fatalf("claim after conservative stored expiry = %v, %v; want accepted", claimed, err)
	}
}

func TestClaimReplayNoncePrunesExpiredEntries(t *testing.T) {
	s := openTestStore(t)
	ctx := context.Background()
	now := time.Date(2026, 9, 9, 5, 0, 0, 0, time.UTC)
	for index := 0; index < 20; index++ {
		claimed, err := s.ClaimReplayNonce(ctx, fmt.Sprintf("expired-%d", index), now.Add(time.Second), now, 100)
		if err != nil || !claimed {
			t.Fatalf("seed nonce %d: claimed=%v err=%v", index, claimed, err)
		}
	}
	claimed, err := s.ClaimReplayNonce(ctx, "current", now.Add(2*time.Minute), now.Add(2*time.Second), 100)
	if err != nil || !claimed {
		t.Fatalf("claim current: claimed=%v err=%v", claimed, err)
	}
	var count int
	if err := s.db.QueryRowContext(ctx, `SELECT count(*) FROM license_replay_nonces`).Scan(&count); err != nil {
		t.Fatal(err)
	}
	if count != 1 {
		t.Fatalf("replay nonce rows = %d, want only current row", count)
	}
}

func TestReplayNonceCapacityDistinguishesReplayAndFreesExpiredSpace(t *testing.T) {
	s := openTestStore(t)
	ctx := context.Background()
	now := time.Date(2026, 9, 9, 6, 0, 0, 0, time.UTC)
	claimed, err := s.ClaimReplayNonce(ctx, "first", now.Add(time.Minute), now, 1)
	if err != nil || !claimed {
		t.Fatalf("first claim = %v, %v", claimed, err)
	}
	claimed, err = s.ClaimReplayNonce(ctx, "first", now.Add(time.Minute), now, 1)
	if err != nil || claimed {
		t.Fatalf("live replay = %v, %v; want replay without capacity error", claimed, err)
	}
	claimed, err = s.ClaimReplayNonce(ctx, "second", now.Add(time.Minute), now, 1)
	if claimed || !errors.Is(err, ErrReplayNonceCapacity) {
		t.Fatalf("capacity claim = %v, %v; want ErrReplayNonceCapacity", claimed, err)
	}
	claimed, err = s.ClaimReplayNonce(ctx, "second", now.Add(2*time.Minute), now.Add(time.Minute), 1)
	if err != nil || !claimed {
		t.Fatalf("claim after expiry = %v, %v; want accepted", claimed, err)
	}
}

func TestReplayNonceConcurrentClaimsAreAtomicAndBounded(t *testing.T) {
	t.Run("same nonce", func(t *testing.T) {
		s := openTestStore(t)
		ctx := context.Background()
		now := time.Date(2026, 9, 9, 6, 30, 0, 0, time.UTC)
		start := make(chan struct{})
		results := make(chan struct {
			claimed bool
			err     error
		}, 32)
		var wg sync.WaitGroup
		for i := 0; i < 32; i++ {
			wg.Add(1)
			go func() {
				defer wg.Done()
				<-start
				claimed, err := s.ClaimReplayNonce(ctx, "same", now.Add(time.Minute), now, 64)
				results <- struct {
					claimed bool
					err     error
				}{claimed, err}
			}()
		}
		close(start)
		wg.Wait()
		close(results)
		claims := 0
		for result := range results {
			if result.err != nil {
				t.Fatalf("concurrent same-nonce error: %v", result.err)
			}
			if result.claimed {
				claims++
			}
		}
		if claims != 1 {
			t.Fatalf("successful same-nonce claims = %d, want 1", claims)
		}
	})

	t.Run("unique nonces", func(t *testing.T) {
		s := openTestStore(t)
		ctx := context.Background()
		now := time.Date(2026, 9, 9, 7, 0, 0, 0, time.UTC)
		const capacity = 10
		start := make(chan struct{})
		results := make(chan error, 40)
		var wg sync.WaitGroup
		for i := 0; i < 40; i++ {
			index := i
			wg.Add(1)
			go func() {
				defer wg.Done()
				<-start
				claimed, err := s.ClaimReplayNonce(ctx, fmt.Sprintf("unique-%02d", index), now.Add(time.Minute), now, capacity)
				if err == nil && !claimed {
					err = errors.New("unique nonce was treated as replay")
				}
				results <- err
			}()
		}
		close(start)
		wg.Wait()
		close(results)
		accepted, capacityErrors := 0, 0
		for err := range results {
			switch {
			case err == nil:
				accepted++
			case errors.Is(err, ErrReplayNonceCapacity):
				capacityErrors++
			default:
				t.Fatalf("unexpected concurrent error (must not leak busy/locked state): %v", err)
			}
		}
		if accepted != capacity || capacityErrors != 40-capacity {
			t.Fatalf("accepted/capacity errors = %d/%d, want %d/%d", accepted, capacityErrors, capacity, 40-capacity)
		}
		var rows int
		if err := s.db.QueryRowContext(ctx, `SELECT count(*) FROM license_replay_nonces`).Scan(&rows); err != nil {
			t.Fatal(err)
		}
		if rows > capacity {
			t.Fatalf("replay rows = %d, cap %d", rows, capacity)
		}
	})
}

func TestLeaseLastSeenIsCoalescedHourlyWhileLeaseUntilStillAdvances(t *testing.T) {
	s := openTestStore(t)
	ctx := context.Background()
	software := mustSoftware(t, s, "Lease Product")
	pkg, err := s.CreatePackage(ctx, "Lease Package", []string{software.ID})
	if err != nil {
		t.Fatal(err)
	}
	card := mustCard(t, s, pkg.ID, 30, nil)
	now := time.Date(2026, 9, 8, 10, 0, 0, 0, time.UTC)
	lease := model.Lease{CardID: card.ID, SoftwareID: software.ID, MachineHash: strings.Repeat("d", 64), LastSeenAt: now, LeaseUntil: now.Add(30 * time.Minute)}
	if _, err := s.Activate(ctx, model.ActivationMutation{RequestID: uuid.NewString(), CardID: card.ID, SoftwareID: software.ID, MachineHash: lease.MachineHash, ObservedAt: now, ActivatedAt: now, ExpiresAt: now.Add(30 * 24 * time.Hour), LeaseUntil: lease.LeaseUntil}); err != nil {
		t.Fatal(err)
	}
	if _, err := s.RecordLease(ctx, lease); err != nil {
		t.Fatal(err)
	}
	lease.LastSeenAt = now.Add(30 * time.Minute)
	lease.LeaseUntil = now.Add(2 * time.Hour)
	got, err := s.RecordLease(ctx, lease)
	if err != nil {
		t.Fatal(err)
	}
	if !got.LastSeenAt.Equal(now) || !got.LeaseUntil.Equal(lease.LeaseUntil) {
		t.Fatalf("coalesced lease = %+v", got)
	}
	lease.LastSeenAt = now.Add(time.Hour)
	got, err = s.RecordLease(ctx, lease)
	if err != nil {
		t.Fatal(err)
	}
	if !got.LastSeenAt.Equal(lease.LastSeenAt) {
		t.Fatalf("hourly last_seen = %v, want %v", got.LastSeenAt, lease.LastSeenAt)
	}
}

func TestLeaseRequiresCurrentActivationBindingAndCannotReappearAfterUnbind(t *testing.T) {
	s := openTestStore(t)
	ctx := context.Background()
	softwareA := mustSoftware(t, s, "Lease Bound Product A")
	softwareB := mustSoftware(t, s, "Lease Bound Product B")
	pkg, err := s.CreatePackage(ctx, "Lease Bound Package", []string{softwareA.ID, softwareB.ID})
	if err != nil {
		t.Fatal(err)
	}
	card := mustCard(t, s, pkg.ID, 30, nil)
	now := time.Date(2026, 9, 8, 10, 0, 0, 0, time.UTC)
	machine := strings.Repeat("7", 64)
	lease := model.Lease{CardID: card.ID, SoftwareID: softwareA.ID, MachineHash: machine, LastSeenAt: now, LeaseUntil: now.Add(30 * time.Minute)}
	if _, err := s.RecordLease(ctx, lease); err == nil {
		t.Fatal("lease without activation binding succeeded")
	}
	if _, err := s.Activate(ctx, model.ActivationMutation{RequestID: uuid.NewString(), CardID: card.ID, SoftwareID: softwareA.ID, MachineHash: machine, ObservedAt: now, ActivatedAt: now, ExpiresAt: now.Add(30 * 24 * time.Hour), LeaseUntil: lease.LeaseUntil}); err != nil {
		t.Fatal(err)
	}
	if _, err := s.RecordLease(ctx, lease); err != nil {
		t.Fatalf("lease for exact binding failed: %v", err)
	}
	wrongSoftware := lease
	wrongSoftware.SoftwareID = softwareB.ID
	if _, err := s.RecordLease(ctx, wrongSoftware); err == nil {
		t.Fatal("lease for an unactivated software binding succeeded")
	}
	wrongMachine := lease
	wrongMachine.MachineHash = strings.Repeat("8", 64)
	if _, err := s.RecordLease(ctx, wrongMachine); err == nil {
		t.Fatal("lease for a different machine succeeded")
	}

	stop := make(chan struct{})
	writerDone := make(chan struct{})
	go func() {
		defer close(writerDone)
		for {
			select {
			case <-stop:
				return
			default:
				_, _ = s.RecordLease(ctx, lease)
			}
		}
	}()
	if err := s.UnbindCard(ctx, card.ID); err != nil {
		close(stop)
		<-writerDone
		t.Fatal(err)
	}
	if _, err := s.RecordLease(ctx, lease); err == nil {
		close(stop)
		<-writerDone
		t.Fatal("lease recreated after unbind")
	}
	close(stop)
	<-writerDone
	var leases int
	if err := s.db.QueryRowContext(ctx, `SELECT count(*) FROM license_leases WHERE card_id=?`, card.ID).Scan(&leases); err != nil {
		t.Fatal(err)
	}
	if leases != 0 {
		t.Fatalf("leases after concurrent unbind = %d, want 0", leases)
	}
}

func TestConcurrentOpenMigratesOneDatabaseWithoutBusyLeakage(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "shared-license.db")
	const workers = 20
	start := make(chan struct{})
	stores := make(chan *Store, workers)
	errs := make(chan error, workers)
	var wg sync.WaitGroup
	for i := 0; i < workers; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			<-start
			s, err := Open(ctx, path)
			if err != nil {
				errs <- err
				return
			}
			stores <- s
		}()
	}
	close(start)
	wg.Wait()
	close(stores)
	close(errs)
	for err := range errs {
		if strings.Contains(strings.ToLower(err.Error()), "busy") || strings.Contains(strings.ToLower(err.Error()), "locked") {
			t.Errorf("SQLite contention leaked from concurrent Open: %v", err)
		} else {
			t.Errorf("concurrent Open failed: %v", err)
		}
	}
	opened := 0
	for s := range stores {
		opened++
		if err := s.Close(); err != nil {
			t.Errorf("Close: %v", err)
		}
	}
	if opened != workers {
		t.Fatalf("successful opens = %d, want %d", opened, workers)
	}
}

func TestCanceledImmediateTransactionRollsBackAndConnectionIsReusable(t *testing.T) {
	s := openTestStore(t)
	ctx, cancel := context.WithCancel(context.Background())
	id := uuid.NewString()
	err := s.immediate(ctx, func(conn *sql.Conn) error {
		if _, err := conn.ExecContext(ctx, `INSERT INTO license_software(id,name,enabled,created_at) VALUES(?,?,1,?)`, id, "Canceled Product", unixTime(time.Now())); err != nil {
			return err
		}
		cancel()
		return ctx.Err()
	})
	if !errors.Is(err, context.Canceled) {
		t.Fatalf("canceled transaction error = %v, want context.Canceled", err)
	}
	var count int
	if err := s.db.QueryRow(`SELECT count(*) FROM license_software WHERE id=?`, id).Scan(&count); err != nil {
		t.Fatal(err)
	}
	if count != 0 {
		t.Fatalf("canceled transaction retained %d rows, want rollback", count)
	}
	if _, err := s.CreateSoftware(context.Background(), "Reusable Product"); err != nil {
		t.Fatalf("store was not reusable after canceled transaction: %v", err)
	}
}

func TestUnbindClearsActivationAndDeletePermanentlyCascadesCardState(t *testing.T) {
	s := openTestStore(t)
	ctx := context.Background()
	software := mustSoftware(t, s, "Delete Product")
	pkg, err := s.CreatePackage(ctx, "Delete Package", []string{software.ID})
	if err != nil {
		t.Fatal(err)
	}
	card := mustCard(t, s, pkg.ID, 30, nil)
	now := time.Date(2026, 9, 8, 10, 0, 0, 0, time.UTC)
	mutation := model.ActivationMutation{RequestID: uuid.NewString(), CardID: card.ID, SoftwareID: software.ID, MachineHash: strings.Repeat("e", 64), ObservedAt: now, ActivatedAt: now, ExpiresAt: now.Add(30 * 24 * time.Hour), LeaseUntil: now.Add(time.Hour)}
	if _, err := s.Activate(ctx, mutation); err != nil {
		t.Fatal(err)
	}
	if err := s.UnbindCard(ctx, card.ID); err != nil {
		t.Fatal(err)
	}
	summary := mustSearchOne(t, s, model.CardFilter{FullDigest: card.FullDigest})
	if summary.ActivatedAt == nil || summary.ExpiresAt == nil || summary.MachineHash != nil {
		t.Fatalf("unbound card = %+v", summary)
	}
	if !summary.ExpiresAt.Equal(mutation.ExpiresAt) {
		t.Fatalf("unbind changed expiry to %v, want %v", summary.ExpiresAt, mutation.ExpiresAt)
	}
	assertDependentCounts(t, s, card.ID, 0, 0, 0)

	mutation.RequestID = uuid.NewString()
	mutation.MachineHash = strings.Repeat("f", 64)
	if _, err := s.Activate(ctx, mutation); err != nil {
		t.Fatal(err)
	}
	if err := s.DeleteCard(ctx, card.ID); err != nil {
		t.Fatal(err)
	}
	assertDependentCounts(t, s, card.ID, 0, 0, 0)
	got, err := s.SearchCards(ctx, model.CardFilter{FullDigest: card.FullDigest})
	if err != nil || len(got) != 0 {
		t.Fatalf("deleted card search = %+v, %v", got, err)
	}
	if err := s.RenewCard(ctx, card.ID, 30, now); !errors.Is(err, ErrNotFound) {
		t.Fatalf("renew deleted card error = %v, want ErrNotFound", err)
	}
}

func openTestStore(t *testing.T) *Store {
	t.Helper()
	s, err := Open(context.Background(), filepath.Join(t.TempDir(), "license.db"))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		if err := s.Close(); err != nil {
			t.Errorf("Close: %v", err)
		}
	})
	return s
}

func mustSoftware(t *testing.T, s *Store, name string) model.Software {
	t.Helper()
	software, err := s.CreateSoftware(context.Background(), name)
	if err != nil {
		t.Fatal(err)
	}
	return software
}

func mustPackage(t *testing.T, s *Store) model.Package {
	t.Helper()
	software := mustSoftware(t, s, "Default Product")
	pkg, err := s.CreatePackage(context.Background(), "Default Package", []string{software.ID})
	if err != nil {
		t.Fatal(err)
	}
	return pkg
}

func mustCard(t *testing.T, s *Store, packageID string, days int, note *string) model.Card {
	t.Helper()
	id := uuid.NewString()
	sealed := model.SealedCard{ID: id, Ciphertext: []byte("cipher-" + id), FullDigest: fmt.Sprintf("%064x", id[0:8]), Prefix8: strings.ToUpper(strings.ReplaceAll(id[0:8], "-", "0")), Last4: strings.ToUpper(id[len(id)-4:])}
	cards, err := s.GenerateCards(context.Background(), model.GenerateCardsRequest{PackageID: packageID, Count: 1, BaseDays: days, Note: note, CreatedAt: time.Date(2026, 9, 8, 8, 0, 0, 0, time.UTC)}, []model.SealedCard{sealed})
	if err != nil {
		t.Fatal(err)
	}
	return cards[0]
}

func mustSearchOne(t *testing.T, s *Store, filter model.CardFilter) model.CardSummary {
	t.Helper()
	got, err := s.SearchCards(context.Background(), filter)
	if err != nil {
		t.Fatal(err)
	}
	if len(got) != 1 {
		t.Fatalf("SearchCards() returned %d rows, want 1", len(got))
	}
	return got[0]
}

func packageMembers(t *testing.T, s *Store, packageID string) []string {
	t.Helper()
	rows, err := s.db.Query(`SELECT software_id FROM license_package_members WHERE package_id=? ORDER BY software_id`, packageID)
	if err != nil {
		t.Fatal(err)
	}
	defer rows.Close()
	var ids []string
	for rows.Next() {
		var id string
		if err := rows.Scan(&id); err != nil {
			t.Fatal(err)
		}
		ids = append(ids, id)
	}
	return ids
}

func assertDependentCounts(t *testing.T, s *Store, cardID string, wantBindings, wantRequests, wantLeases int) {
	t.Helper()
	for table, want := range map[string]int{"license_activation_bindings": wantBindings, "license_activation_requests": wantRequests, "license_leases": wantLeases} {
		var got int
		if err := s.db.QueryRow(`SELECT count(*) FROM `+table+` WHERE card_id=?`, cardID).Scan(&got); err != nil {
			t.Fatal(err)
		}
		if got != want {
			t.Errorf("%s count = %d, want %d", table, got, want)
		}
	}
}

func assertUUID(t *testing.T, value string) {
	t.Helper()
	if _, err := uuid.Parse(value); err != nil {
		t.Fatalf("%q is not a UUID: %v", value, err)
	}
}

func assertStrings(t *testing.T, got, want []string) {
	t.Helper()
	got = append([]string(nil), got...)
	want = append([]string(nil), want...)
	sort.Strings(got)
	sort.Strings(want)
	if fmt.Sprint(got) != fmt.Sprint(want) {
		t.Fatalf("strings = %v, want %v", got, want)
	}
}
