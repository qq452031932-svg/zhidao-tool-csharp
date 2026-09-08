package store

import (
	"context"
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
	if _, err := s.db.ExecContext(ctx, `UPDATE license_software SET id=? WHERE id=?`, uuid.NewString(), software.ID); err == nil {
		t.Fatal("software identifier update succeeded, want immutable identifier")
	}
	var count int
	if err := s.db.QueryRowContext(ctx, `SELECT count(*) FROM license_software WHERE id=?`, software.ID).Scan(&count); err != nil || count != 1 {
		t.Fatalf("software row count = %d, err = %v, want retained row", count, err)
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
	sealed := []model.SealedCard{
		{ID: uuid.NewString(), Ciphertext: []byte("cipher-one"), FullDigest: strings.Repeat("a", 64), Prefix8: "ABCDEFGH", Last4: "WXYZ"},
		{ID: uuid.NewString(), Ciphertext: []byte("cipher-two"), FullDigest: strings.Repeat("b", 64), Prefix8: "12345678", Last4: "9876"},
	}
	cards, err := s.GenerateCards(ctx, model.GenerateCardsRequest{PackageID: pkg.ID, Count: 2, BaseDays: 90, Note: &note}, sealed)
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
	if _, err := s.GenerateCards(ctx, model.GenerateCardsRequest{PackageID: pkg.ID, Count: 1, BaseDays: 30, Note: &note1}, []model.SealedCard{one}); err != nil {
		t.Fatal(err)
	}
	if _, err := s.GenerateCards(ctx, model.GenerateCardsRequest{PackageID: pkg.ID, Count: 1, BaseDays: 30, Note: &note2}, []model.SealedCard{two}); err != nil {
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
		ActivatedAt: now, ExpiresAt: now.Add(30 * 24 * time.Hour), LeaseUntil: now.Add(time.Hour),
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
	base := model.ActivationMutation{RequestID: uuid.NewString(), CardID: card.ID, SoftwareID: software.ID, MachineHash: strings.Repeat("a", 64), ActivatedAt: now, ExpiresAt: now.Add(24 * time.Hour), LeaseUntil: now.Add(time.Hour)}
	if _, err := s.Activate(ctx, base); err != nil {
		t.Fatal(err)
	}
	base.RequestID = uuid.NewString()
	base.MachineHash = strings.Repeat("b", 64)
	if _, err := s.Activate(ctx, base); !errors.Is(err, ErrMachineBound) {
		t.Fatalf("Activate competing machine error = %v, want ErrMachineBound", err)
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
	expired := now.Add(-24 * time.Hour)
	if _, err := s.Activate(ctx, model.ActivationMutation{RequestID: uuid.NewString(), CardID: active.ID, SoftwareID: software.ID, MachineHash: strings.Repeat("c", 64), ActivatedAt: now.Add(-60 * 24 * time.Hour), ExpiresAt: expired, LeaseUntil: now.Add(time.Hour)}); err != nil {
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
}

func TestReplayNonceClaimPersistsUntilExpiry(t *testing.T) {
	s := openTestStore(t)
	ctx := context.Background()
	now := time.Date(2026, 9, 8, 10, 0, 0, 0, time.UTC)
	claimed, err := s.ClaimReplayNonce(ctx, "nonce-1", now.Add(time.Minute), now)
	if err != nil || !claimed {
		t.Fatalf("first claim = %v, %v", claimed, err)
	}
	claimed, err = s.ClaimReplayNonce(ctx, "nonce-1", now.Add(2*time.Minute), now.Add(30*time.Second))
	if err != nil || claimed {
		t.Fatalf("replay claim = %v, %v; want rejected", claimed, err)
	}
	claimed, err = s.ClaimReplayNonce(ctx, "nonce-1", now.Add(3*time.Minute), now.Add(61*time.Second))
	if err != nil || !claimed {
		t.Fatalf("post-expiry claim = %v, %v; want accepted", claimed, err)
	}
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
	mutation := model.ActivationMutation{RequestID: uuid.NewString(), CardID: card.ID, SoftwareID: software.ID, MachineHash: strings.Repeat("e", 64), ActivatedAt: now, ExpiresAt: now.Add(30 * 24 * time.Hour), LeaseUntil: now.Add(time.Hour)}
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
	cards, err := s.GenerateCards(context.Background(), model.GenerateCardsRequest{PackageID: packageID, Count: 1, BaseDays: days, Note: note}, []model.SealedCard{sealed})
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
