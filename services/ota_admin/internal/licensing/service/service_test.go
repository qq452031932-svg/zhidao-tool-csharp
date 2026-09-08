package service

import (
	"bytes"
	"context"
	"crypto/rand"
	"crypto/sha256"
	"database/sql"
	"encoding/base32"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"regexp"
	"strings"
	"testing"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/licensing/model"
	licensestore "fanfh.cn/superpan/ota-admin/internal/licensing/store"
)

var cardPattern = regexp.MustCompile(`^[A-Z2-7]{8}(?:-[A-Z2-7]{8}){3}$`)

type fakeClock struct{ now time.Time }

func (c *fakeClock) Now() time.Time { return c.now }

type delayedValidationStore struct {
	*licensestore.Store
	entered chan struct{}
	proceed chan struct{}
}

type delayedActivationStore struct {
	*licensestore.Store
	entered chan struct{}
	proceed chan struct{}
}

func (s *delayedActivationStore) Activate(ctx context.Context, mutation model.ActivationMutation) (model.ActivationState, error) {
	close(s.entered)
	<-s.proceed
	return s.Store.Activate(ctx, mutation)
}

func (s *delayedValidationStore) ValidateAndRecordLease(ctx context.Context, lease model.Lease) (model.ActivationState, error) {
	close(s.entered)
	<-s.proceed
	return s.Store.ValidateAndRecordLease(ctx, lease)
}

type harness struct {
	t      *testing.T
	ctx    context.Context
	store  *licensestore.Store
	clock  *fakeClock
	svc    *Service
	dbPath string
}

func newHarness(t *testing.T) *harness {
	t.Helper()
	ctx := context.Background()
	dbPath := t.TempDir() + `\license.db`
	st, err := licensestore.Open(ctx, dbPath)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		if err := st.Close(); err != nil {
			t.Errorf("Close: %v", err)
		}
	})
	clock := &fakeClock{now: time.Date(2026, 9, 8, 10, 0, 0, 0, time.UTC)}
	svc, err := New(st, clock, rand.Reader, bytes.Repeat([]byte{0x11}, 32), bytes.Repeat([]byte{0x22}, 32))
	if err != nil {
		t.Fatal(err)
	}
	return &harness{t: t, ctx: ctx, store: st, clock: clock, svc: svc, dbPath: dbPath}
}

func (h *harness) software(name string) model.Software {
	h.t.Helper()
	software, err := h.store.CreateSoftware(h.ctx, name)
	if err != nil {
		h.t.Fatal(err)
	}
	return software
}

func (h *harness) packageWith(software ...model.Software) model.Package {
	h.t.Helper()
	ids := make([]string, len(software))
	for i := range software {
		ids[i] = software[i].ID
	}
	pkg, err := h.store.CreatePackage(h.ctx, "package-"+software[0].ID, ids)
	if err != nil {
		h.t.Fatal(err)
	}
	return pkg
}

func (h *harness) generate(packageID string, days int) GeneratedCard {
	h.t.Helper()
	generated, err := h.svc.GenerateCards(h.ctx, packageID, days, 1, nil, nil)
	if err != nil {
		h.t.Fatal(err)
	}
	if len(generated) != 1 {
		h.t.Fatalf("generated count = %d, want 1", len(generated))
	}
	return generated[0]
}

func TestGenerateCardsAcceptsOnlyNamedDurations(t *testing.T) {
	h := newHarness(t)
	software := h.software("Durations")
	pkg := h.packageWith(software)

	for _, days := range []int{30, 90, 180, 365} {
		t.Run(fmt.Sprintf("%d_days", days), func(t *testing.T) {
			cards, err := h.svc.GenerateCards(h.ctx, pkg.ID, days, 1, nil, nil)
			if err != nil {
				t.Fatalf("GenerateCards(%d): %v", days, err)
			}
			if cards[0].Card.BaseDays != days || cards[0].Card.UnactivatedTotalDays != days {
				t.Fatalf("duration fields = %d/%d, want %d", cards[0].Card.BaseDays, cards[0].Card.UnactivatedTotalDays, days)
			}
			if cards[0].Card.ActivatedAt != nil || cards[0].Card.ExpiresAt != nil {
				t.Fatal("generation started the card lifetime")
			}
		})
	}

	for _, days := range []int{-1, 0, 1, 29, 31, 366} {
		if _, err := h.svc.GenerateCards(h.ctx, pkg.ID, days, 1, nil, nil); !errors.Is(err, ErrInvalidRequest) {
			t.Errorf("GenerateCards(%d) error = %v, want ErrInvalidRequest", days, err)
		}
	}
}

func TestGeneratedCardsHaveFourBase32GroupsAndAtLeast128Bits(t *testing.T) {
	h := newHarness(t)
	software := h.software("Entropy")
	pkg := h.packageWith(software)
	cards, err := h.svc.GenerateCards(h.ctx, pkg.ID, 30, 64, nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	seen := make(map[string]struct{}, len(cards))
	for _, card := range cards {
		if !cardPattern.MatchString(card.Plaintext) {
			t.Fatal("generated card does not use the four-group uppercase Base32 format")
		}
		payload, err := base32.StdEncoding.WithPadding(base32.NoPadding).DecodeString(strings.ReplaceAll(card.Plaintext, "-", ""))
		if err != nil {
			t.Fatal(err)
		}
		bits := len(payload) * 8
		if bits < 128 {
			t.Fatalf("generated entropy = %d bits, want at least 128", bits)
		}
		if _, duplicate := seen[card.Plaintext]; duplicate {
			t.Fatal("generated cards were not unique")
		}
		seen[card.Plaintext] = struct{}{}
	}
}

func TestFirstUseStartsAtServerClockAndReusesLifetimeOnSameMachine(t *testing.T) {
	h := newHarness(t)
	softwareA := h.software("Product A")
	softwareB := h.software("Product B")
	pkg := h.packageWith(softwareA, softwareB)
	card := h.generate(pkg.ID, 30)

	first, err := h.svc.Activate(h.ctx, "request-1", card.Plaintext, softwareA.ID, "machine-a")
	if err != nil {
		t.Fatal(err)
	}
	wantExpiry := h.clock.now.Add(30 * 24 * time.Hour)
	if !first.ActivatedAt.Equal(h.clock.now) || !first.ExpiresAt.Equal(wantExpiry) {
		t.Fatalf("first lifetime = %v to %v, want injected server-clock lifetime", first.ActivatedAt, first.ExpiresAt)
	}

	h.clock.now = h.clock.now.Add(48 * time.Hour)
	again, err := h.svc.Activate(h.ctx, "request-2", card.Plaintext, softwareB.ID, "machine-a")
	if err != nil {
		t.Fatal(err)
	}
	if !again.ActivatedAt.Equal(first.ActivatedAt) || !again.ExpiresAt.Equal(first.ExpiresAt) {
		t.Fatal("same-machine cross-software activation changed the original lifetime")
	}
	validated, err := h.svc.Validate(h.ctx, strings.ToLower(strings.ReplaceAll(card.Plaintext, "-", " ")), softwareA.ID, "machine-a")
	if err != nil {
		t.Fatal(err)
	}
	if !validated.ActivatedAt.Equal(first.ActivatedAt) || !validated.ExpiresAt.Equal(first.ExpiresAt) {
		t.Fatal("validation did not preserve the activation lifetime")
	}
}

func TestActivationAndValidationReturnPackageNameForSignedClaims(t *testing.T) {
	h := newHarness(t)
	software := h.software("Claims Product")
	pkg := h.packageWith(software)
	card := h.generate(pkg.ID, 30)

	activated, err := h.svc.Activate(h.ctx, "claims-activation", card.Plaintext, software.ID, "machine-claims")
	if err != nil {
		t.Fatal(err)
	}
	if activated.PackageName != pkg.Name {
		t.Fatalf("activation package name = %q, want %q", activated.PackageName, pkg.Name)
	}
	validated, err := h.svc.Validate(h.ctx, card.Plaintext, software.ID, "machine-claims")
	if err != nil {
		t.Fatal(err)
	}
	if validated.PackageName != pkg.Name {
		t.Fatalf("validation package name = %q, want %q", validated.PackageName, pkg.Name)
	}
}

func TestReactivationClampsLeaseAndRecordsCurrentObservationTime(t *testing.T) {
	h := newHarness(t)
	softwareA := h.software("Lease Clamp A")
	softwareB := h.software("Lease Clamp B")
	pkg := h.packageWith(softwareA, softwareB)
	card := h.generate(pkg.ID, 30)
	first, err := h.svc.Activate(h.ctx, "request-a", card.Plaintext, softwareA.ID, "machine-a")
	if err != nil {
		t.Fatal(err)
	}
	h.clock.now = first.ExpiresAt.Add(-10 * time.Minute)
	second, err := h.svc.Activate(h.ctx, "request-b", card.Plaintext, softwareB.ID, "machine-a")
	if err != nil {
		t.Fatal(err)
	}
	if !second.LeaseUntil.Equal(first.ExpiresAt) {
		t.Fatalf("reactivation lease_until = %v, want authoritative expiry %v", second.LeaseUntil, first.ExpiresAt)
	}
	raw := h.rawDB()
	defer raw.Close()
	var lastSeen int64
	if err := raw.QueryRow(`SELECT last_seen_at FROM license_leases WHERE card_id=? AND software_id=?`, card.Card.ID, softwareB.ID).Scan(&lastSeen); err != nil {
		t.Fatal(err)
	}
	if lastSeen != h.clock.now.Unix() {
		t.Fatalf("reactivation last_seen_at = %v, want current observation %v", time.Unix(lastSeen, 0).UTC(), h.clock.now)
	}
}

func TestGeneratedCardCreatedAtUsesInjectedClock(t *testing.T) {
	h := newHarness(t)
	software := h.software("Generated Time")
	pkg := h.packageWith(software)
	h.clock.now = time.Date(2031, 4, 5, 6, 7, 8, 987654321, time.UTC)
	card := h.generate(pkg.ID, 30)
	want := h.clock.now.Truncate(time.Second)
	if !card.Card.CreatedAt.Equal(want) {
		t.Fatalf("generated created_at = %v, want injected clock %v", card.Card.CreatedAt, want)
	}
	if stored := h.searchCard(card.Card.Prefix8); !stored.CreatedAt.Equal(want) {
		t.Fatalf("stored created_at = %v, want injected clock %v", stored.CreatedAt, want)
	}
}

func TestAnotherMachineIsRejectedWithoutSecretLeakage(t *testing.T) {
	h := newHarness(t)
	software := h.software("Machine Binding")
	pkg := h.packageWith(software)
	card := h.generate(pkg.ID, 30)
	if _, err := h.svc.Activate(h.ctx, "request-1", card.Plaintext, software.ID, "machine-a"); err != nil {
		t.Fatal(err)
	}
	_, err := h.svc.Activate(h.ctx, "request-2", card.Plaintext, software.ID, "machine-b")
	if !errors.Is(err, ErrMachineMismatch) {
		t.Fatalf("second-machine error = %v, want ErrMachineMismatch", err)
	}
	if strings.Contains(err.Error(), card.Plaintext) || strings.Contains(err.Error(), strings.ReplaceAll(card.Plaintext, "-", "")) {
		t.Fatal("error leaked card plaintext")
	}
}

func TestActivationRechecksMembershipAndEnabledStateInsideCommit(t *testing.T) {
	for _, test := range []struct {
		name    string
		mutate  func(*harness, model.Package, model.Software, model.Software) error
		wantErr error
	}{
		{
			name: "membership removed",
			mutate: func(h *harness, pkg model.Package, target, other model.Software) error {
				return h.store.ReplacePackageMembers(h.ctx, pkg.ID, []string{other.ID})
			},
			wantErr: ErrAppNotAllowed,
		},
		{
			name: "software disabled",
			mutate: func(h *harness, _ model.Package, target, _ model.Software) error {
				return h.store.SetSoftwareEnabled(h.ctx, target.ID, false)
			},
			wantErr: ErrAppDisabled,
		},
	} {
		t.Run(test.name, func(t *testing.T) {
			h := newHarness(t)
			target := h.software("Interleaved Target")
			other := h.software("Interleaved Other")
			pkg := h.packageWith(target, other)
			card := h.generate(pkg.ID, 30)
			delayed := &delayedActivationStore{Store: h.store, entered: make(chan struct{}), proceed: make(chan struct{})}
			defer closeIfOpen(delayed.proceed)
			svc := h.serviceForStore(delayed)
			done := make(chan error, 1)
			go func() {
				_, err := svc.Activate(h.ctx, "interleaved-request", card.Plaintext, target.ID, "machine-a")
				done <- err
			}()
			<-delayed.entered
			if err := test.mutate(h, pkg, target, other); err != nil {
				t.Fatal(err)
			}
			close(delayed.proceed)
			if err := <-done; !errors.Is(err, test.wantErr) {
				t.Fatalf("interleaved activation error = %v, want %v", err, test.wantErr)
			}
			summary := h.searchCard(card.Card.Prefix8)
			if summary.ActivatedAt != nil || summary.ExpiresAt != nil || summary.MachineHash != nil {
				t.Fatal("denied interleaved activation mutated card lifetime or binding")
			}
			raw := h.rawDB()
			defer raw.Close()
			for _, table := range []string{"license_activation_requests", "license_activation_bindings", "license_leases"} {
				var count int
				query := "SELECT count(*) FROM " + table + " WHERE card_id=?"
				if err := raw.QueryRow(query, card.Card.ID).Scan(&count); err != nil {
					t.Fatal(err)
				}
				if count != 0 {
					t.Fatalf("denied interleaved activation persisted %d rows in %s", count, table)
				}
			}
		})
	}
}

func TestActivationUsesRenewedAuthoritativeUnusedDaysAfterPreflight(t *testing.T) {
	h := newHarness(t)
	software := h.software("Renew During Activation")
	pkg := h.packageWith(software)
	card := h.generate(pkg.ID, 30)
	delayed := &delayedActivationStore{Store: h.store, entered: make(chan struct{}), proceed: make(chan struct{})}
	defer closeIfOpen(delayed.proceed)
	svc := h.serviceForStore(delayed)
	done := make(chan struct {
		state model.ActivationState
		err   error
	}, 1)
	go func() {
		state, err := svc.Activate(h.ctx, "renewed-request", card.Plaintext, software.ID, "machine-a")
		done <- struct {
			state model.ActivationState
			err   error
		}{state: state, err: err}
	}()
	<-delayed.entered
	if err := h.svc.RenewCard(h.ctx, card.Card.ID, 90); err != nil {
		t.Fatal(err)
	}
	close(delayed.proceed)
	result := <-done
	if result.err != nil {
		t.Fatal(result.err)
	}
	wantExpiry := h.clock.now.Add(120 * 24 * time.Hour)
	if !result.state.ActivatedAt.Equal(h.clock.now) || !result.state.ExpiresAt.Equal(wantExpiry) {
		t.Fatalf("activation after interleaved renewal = %v to %v, want expiry %v", result.state.ActivatedAt, result.state.ExpiresAt, wantExpiry)
	}
}

func TestPackageAuthorizationAndSoftwareEnabledStateAreDynamic(t *testing.T) {
	h := newHarness(t)
	softwareA := h.software("Dynamic A")
	softwareB := h.software("Dynamic B")
	pkg := h.packageWith(softwareA)
	card := h.generate(pkg.ID, 90)
	if _, err := h.svc.Activate(h.ctx, "request-a", card.Plaintext, softwareA.ID, "machine-a"); err != nil {
		t.Fatal(err)
	}
	if _, err := h.svc.Activate(h.ctx, "request-b-denied", card.Plaintext, softwareB.ID, "machine-a"); !errors.Is(err, ErrAppNotAllowed) {
		t.Fatalf("not-yet-added software error = %v, want ErrAppNotAllowed", err)
	}
	if err := h.store.ReplacePackageMembers(h.ctx, pkg.ID, []string{softwareA.ID, softwareB.ID}); err != nil {
		t.Fatal(err)
	}
	validatedB, err := h.svc.Validate(h.ctx, card.Plaintext, softwareB.ID, "machine-a")
	if err != nil {
		t.Fatal(err)
	}
	if !validatedB.ActivatedAt.Equal(h.clock.now) || !validatedB.ExpiresAt.Equal(h.clock.now.Add(90*24*time.Hour)) {
		t.Fatal("cross-product validation changed the original lifetime")
	}
	raw := h.rawDB()
	defer raw.Close()
	var requests, bindings, leases int
	if err := raw.QueryRow(`SELECT count(*) FROM license_activation_requests WHERE card_id=?`, card.Card.ID).Scan(&requests); err != nil {
		t.Fatal(err)
	}
	if err := raw.QueryRow(`SELECT count(*) FROM license_activation_bindings WHERE card_id=? AND software_id=?`, card.Card.ID, softwareB.ID).Scan(&bindings); err != nil {
		t.Fatal(err)
	}
	if err := raw.QueryRow(`SELECT count(*) FROM license_leases WHERE card_id=? AND software_id=?`, card.Card.ID, softwareB.ID).Scan(&leases); err != nil {
		t.Fatal(err)
	}
	if requests != 1 || bindings != 1 || leases != 1 {
		t.Fatalf("cross-product requests/bindings/leases = %d/%d/%d, want 1/1/1", requests, bindings, leases)
	}
	if err := h.store.ReplacePackageMembers(h.ctx, pkg.ID, []string{softwareB.ID}); err != nil {
		t.Fatal(err)
	}
	if _, err := h.svc.Validate(h.ctx, card.Plaintext, softwareA.ID, "machine-a"); !errors.Is(err, ErrAppNotAllowed) {
		t.Fatalf("removed software error = %v, want ErrAppNotAllowed", err)
	}
	if err := h.store.SetSoftwareEnabled(h.ctx, softwareB.ID, false); err != nil {
		t.Fatal(err)
	}
	if _, err := h.svc.Validate(h.ctx, card.Plaintext, softwareB.ID, "machine-a"); !errors.Is(err, ErrAppDisabled) {
		t.Fatalf("disabled software error = %v, want ErrAppDisabled", err)
	}
}

func TestCrossProductValidationCannotRecreateStateAfterConcurrentUnbind(t *testing.T) {
	h := newHarness(t)
	softwareA := h.software("Unbind A")
	softwareB := h.software("Unbind B")
	pkg := h.packageWith(softwareA, softwareB)
	card := h.generate(pkg.ID, 30)
	if _, err := h.svc.Activate(h.ctx, "request-a", card.Plaintext, softwareA.ID, "machine-a"); err != nil {
		t.Fatal(err)
	}

	delayed := &delayedValidationStore{Store: h.store, entered: make(chan struct{}), proceed: make(chan struct{})}
	defer func() {
		select {
		case <-delayed.proceed:
		default:
			close(delayed.proceed)
		}
	}()
	svc, err := New(delayed, h.clock, rand.Reader, bytes.Repeat([]byte{0x11}, 32), bytes.Repeat([]byte{0x22}, 32))
	if err != nil {
		t.Fatal(err)
	}
	validationDone := make(chan error, 1)
	go func() {
		_, err := svc.Validate(h.ctx, card.Plaintext, softwareB.ID, "machine-a")
		validationDone <- err
	}()
	<-delayed.entered
	if err := h.svc.UnbindCard(h.ctx, card.Card.ID); err != nil {
		t.Fatal(err)
	}
	close(delayed.proceed)
	if err := <-validationDone; !errors.Is(err, ErrNotFound) {
		t.Fatalf("validation racing after unbind error = %v, want ErrNotFound", err)
	}

	raw := h.rawDB()
	defer raw.Close()
	for _, table := range []string{"license_activation_bindings", "license_leases"} {
		var count int
		if err := raw.QueryRow(`SELECT count(*) FROM `+table+` WHERE card_id=?`, card.Card.ID).Scan(&count); err != nil {
			t.Fatal(err)
		}
		if count != 0 {
			t.Errorf("%s rows after concurrent unbind = %d, want 0", table, count)
		}
	}
}

func TestRenewalRulesForUnusedAndActivatedCards(t *testing.T) {
	h := newHarness(t)
	software := h.software("Renewal")
	pkg := h.packageWith(software)
	unused := h.generate(pkg.ID, 30)
	if err := h.svc.RenewCard(h.ctx, unused.Card.ID, 90); err != nil {
		t.Fatal(err)
	}
	activated, err := h.svc.Activate(h.ctx, "request-unused", unused.Plaintext, software.ID, "machine-a")
	if err != nil {
		t.Fatal(err)
	}
	if want := h.clock.now.Add(120 * 24 * time.Hour); !activated.ExpiresAt.Equal(want) {
		t.Fatalf("unused renewal expiry = %v, want %v", activated.ExpiresAt, want)
	}

	active := h.generate(pkg.ID, 30)
	state, err := h.svc.Activate(h.ctx, "request-active", active.Plaintext, software.ID, "machine-b")
	if err != nil {
		t.Fatal(err)
	}
	if err := h.svc.RenewCard(h.ctx, active.Card.ID, 30); err != nil {
		t.Fatal(err)
	}
	summaries, err := h.store.SearchCards(h.ctx, model.CardFilter{Prefix8: active.Card.Prefix8})
	if err != nil || len(summaries) != 1 {
		t.Fatalf("SearchCards returned %d rows, error %v", len(summaries), err)
	}
	wantFutureExpiry := state.ExpiresAt.Add(30 * 24 * time.Hour)
	if summaries[0].ExpiresAt == nil || !summaries[0].ExpiresAt.Equal(wantFutureExpiry) {
		t.Fatalf("future-expiry renewal = %v, want %v", summaries[0].ExpiresAt, wantFutureExpiry)
	}
	// The second renewal happens after the first renewed expiry, so it must
	// extend from the server's current time rather than the stale expiry.
	h.clock.now = state.ExpiresAt.Add(40 * 24 * time.Hour)
	if err := h.svc.RenewCard(h.ctx, active.Card.ID, 90); err != nil {
		t.Fatal(err)
	}
	summaries, err = h.store.SearchCards(h.ctx, model.CardFilter{Prefix8: active.Card.Prefix8})
	if err != nil || len(summaries) != 1 {
		t.Fatalf("SearchCards returned %d rows, error %v", len(summaries), err)
	}
	want := h.clock.now.Add(90 * 24 * time.Hour)
	if summaries[0].ExpiresAt == nil || !summaries[0].ExpiresAt.Equal(want) {
		t.Fatalf("activated renewal expiry = %v, want %v", summaries[0].ExpiresAt, want)
	}

	for _, days := range []int{180, 365} {
		candidate := h.generate(pkg.ID, 30)
		if err := h.svc.RenewCard(h.ctx, candidate.Card.ID, days); err != nil {
			t.Fatalf("accepted RenewCard(%d) error = %v", days, err)
		}
		rows, err := h.store.SearchCards(h.ctx, model.CardFilter{Prefix8: candidate.Card.Prefix8})
		if err != nil || len(rows) != 1 {
			t.Fatalf("SearchCards after RenewCard(%d) returned %d rows, error %v", days, len(rows), err)
		}
		if rows[0].UnactivatedTotalDays != 30+days {
			t.Fatalf("RenewCard(%d) unused total = %d, want %d", days, rows[0].UnactivatedTotalDays, 30+days)
		}
	}

	for _, days := range []int{-1, 0, 1, 31, 364} {
		if err := h.svc.RenewCard(h.ctx, active.Card.ID, days); !errors.Is(err, ErrInvalidRequest) {
			t.Errorf("RenewCard(%d) error = %v, want ErrInvalidRequest", days, err)
		}
	}
}

func TestUnbindPreservesLifetimeAndDeleteIsPermanent(t *testing.T) {
	h := newHarness(t)
	software := h.software("Administration")
	pkg := h.packageWith(software)
	card := h.generate(pkg.ID, 180)
	first, err := h.svc.Activate(h.ctx, "request-1", card.Plaintext, software.ID, "machine-a")
	if err != nil {
		t.Fatal(err)
	}
	if err := h.svc.UnbindCard(h.ctx, card.Card.ID); err != nil {
		t.Fatal(err)
	}
	rebound, err := h.svc.Activate(h.ctx, "request-2", card.Plaintext, software.ID, "machine-b")
	if err != nil {
		t.Fatal(err)
	}
	if !rebound.ActivatedAt.Equal(first.ActivatedAt) || !rebound.ExpiresAt.Equal(first.ExpiresAt) {
		t.Fatal("unbind changed the card lifetime")
	}
	if err := h.svc.DeleteCard(h.ctx, card.Card.ID); err != nil {
		t.Fatal(err)
	}
	if _, err := h.svc.Validate(h.ctx, card.Plaintext, software.ID, "machine-b"); !errors.Is(err, ErrNotFound) {
		t.Fatalf("deleted-card error = %v, want ErrNotFound", err)
	}
}

func TestExpiredCardIsRejectedByActivationAndValidation(t *testing.T) {
	h := newHarness(t)
	software := h.software("Expiry")
	pkg := h.packageWith(software)
	card := h.generate(pkg.ID, 30)
	state, err := h.svc.Activate(h.ctx, "request-1", card.Plaintext, software.ID, "machine-a")
	if err != nil {
		t.Fatal(err)
	}
	h.clock.now = state.ExpiresAt
	if _, err := h.svc.Validate(h.ctx, card.Plaintext, software.ID, "machine-a"); !errors.Is(err, ErrExpired) {
		t.Fatalf("validation expiry error = %v, want ErrExpired", err)
	}
	if _, err := h.svc.Activate(h.ctx, "request-2", card.Plaintext, software.ID, "machine-a"); !errors.Is(err, ErrExpired) {
		t.Fatalf("activation expiry error = %v, want ErrExpired", err)
	}
}

func TestDefaultAndPerCardNotes(t *testing.T) {
	h := newHarness(t)
	software := h.software("Notes")
	pkg := h.packageWith(software)
	defaultNote := "default batch note"
	override := "per-card note"
	cards, err := h.svc.GenerateCards(h.ctx, pkg.ID, 365, 3, &defaultNote, []*string{nil, &override, nil})
	if err != nil {
		t.Fatal(err)
	}
	summaries, err := h.store.SearchCards(h.ctx, model.CardFilter{PackageID: pkg.ID})
	if err != nil {
		t.Fatal(err)
	}
	notes := make(map[string]string, len(summaries))
	for _, summary := range summaries {
		if summary.Note != nil {
			notes[summary.ID] = *summary.Note
		}
	}
	for i, want := range []string{defaultNote, override, defaultNote} {
		if got := notes[cards[i].Card.ID]; got != want {
			t.Errorf("card %d note = %q, want %q", i, got, want)
		}
	}
}

func TestCardProtectionEncryptsAndUsesNormalizedKeyedDigest(t *testing.T) {
	fixture := "ABCDEFGH-JKLMNPQR-STUVWXYZ-234567AB"
	cardID := "11111111-1111-4111-8111-111111111111"
	key := bytes.Repeat([]byte{0x31}, 32)
	lookupKey := bytes.Repeat([]byte{0x42}, 32)
	protector, err := newCardProtector(key, lookupKey, bytes.NewReader(bytes.Repeat([]byte{0x53}, 64)))
	if err != nil {
		t.Fatal(err)
	}
	sealed, err := protector.seal(cardID, fixture)
	if err != nil {
		t.Fatal(err)
	}
	if bytes.Contains(sealed.ciphertext, []byte(fixture)) || bytes.Contains(sealed.ciphertext, []byte(strings.ReplaceAll(fixture, "-", ""))) {
		t.Fatal("ciphertext contains plaintext card material")
	}
	if got := protector.digest("abcdefgh jklmnpqr stuvwxyz 234567ab"); got != sealed.digest {
		t.Fatal("normalized card forms produced different keyed digests")
	}
	opened, err := protector.open(cardID, sealed.ciphertext, sealed.digest)
	if err != nil {
		t.Fatal(err)
	}
	if opened != fixture {
		t.Fatal("decrypted card did not equal the canonical fixture")
	}
}

func TestCardProtectionRejectsCiphertextTransplantTamperAndDigestMismatch(t *testing.T) {
	fixture := "ABCDEFGH-JKLMNPQR-STUVWXYZ-234567AB"
	cardIDA := "11111111-1111-4111-8111-111111111111"
	cardIDB := "22222222-2222-4222-8222-222222222222"
	protector, err := newCardProtector(bytes.Repeat([]byte{0x31}, 32), bytes.Repeat([]byte{0x42}, 32), bytes.NewReader(bytes.Repeat([]byte{0x53}, 128)))
	if err != nil {
		t.Fatal(err)
	}
	sealed, err := protector.seal(cardIDA, fixture)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := protector.open(cardIDB, sealed.ciphertext, sealed.digest); err == nil {
		t.Fatal("ciphertext transplant to another card id succeeded")
	}
	tampered := append([]byte(nil), sealed.ciphertext...)
	tampered[len(tampered)-1] ^= 0x01
	if _, err := protector.open(cardIDA, tampered, sealed.digest); err == nil {
		t.Fatal("tampered ciphertext succeeded")
	}
	wrongDigest := strings.Repeat("0", len(sealed.digest))
	if _, err := protector.open(cardIDA, sealed.ciphertext, wrongDigest); err == nil {
		t.Fatal("decryption with mismatched keyed digest succeeded")
	}
}

func TestGenerateCardsPersistsOnlyEncryptedCardMaterial(t *testing.T) {
	h := newHarness(t)
	software := h.software("At Rest")
	pkg := h.packageWith(software)
	card := h.generate(pkg.ID, 30)
	raw := h.rawDB()
	defer raw.Close()

	var ciphertext []byte
	var digest string
	if err := raw.QueryRow(`SELECT ciphertext,full_digest FROM license_cards WHERE id=?`, card.Card.ID).Scan(&ciphertext, &digest); err != nil {
		t.Fatal(err)
	}
	compact := strings.ReplaceAll(card.Plaintext, "-", "")
	if len(ciphertext) == 0 || bytes.Equal(ciphertext, []byte(card.Plaintext)) || bytes.Contains(ciphertext, []byte(card.Plaintext)) || bytes.Contains(ciphertext, []byte(compact)) {
		t.Fatal("persisted ciphertext exposed plaintext card material")
	}
	if digest == card.Plaintext || digest == compact || digest != h.svc.protector.digest(card.Plaintext) {
		t.Fatal("persisted full-card digest was not the keyed normalized digest")
	}
	rawDigest := sha256.Sum256([]byte(compact))
	if digest == hex.EncodeToString(rawDigest[:]) {
		t.Fatal("persisted full-card digest used an unkeyed hash")
	}
	opened, err := h.svc.protector.open(card.Card.ID, ciphertext, digest)
	if err != nil {
		t.Fatal(err)
	}
	if opened != card.Plaintext {
		t.Fatal("persisted AES-GCM ciphertext did not round-trip")
	}

	rows, err := raw.Query(`PRAGMA table_info(license_cards)`)
	if err != nil {
		t.Fatal(err)
	}
	defer rows.Close()
	allowedColumns := map[string]bool{
		"id": true, "package_id": true, "ciphertext": true, "full_digest": true,
		"prefix8": true, "last4": true, "base_days": true, "unactivated_total_days": true,
		"note": true, "activated_at": true, "expires_at": true, "machine_hash": true, "created_at": true,
	}
	seenColumns := make(map[string]bool, len(allowedColumns))
	for rows.Next() {
		var cid, notNull, primaryKey int
		var name, columnType string
		var defaultValue any
		if err := rows.Scan(&cid, &name, &columnType, &notNull, &defaultValue, &primaryKey); err != nil {
			t.Fatal(err)
		}
		name = strings.ToLower(name)
		if !allowedColumns[name] {
			t.Fatalf("license_cards contains unexpected secret-bearing column %q", name)
		}
		seenColumns[name] = true
	}
	if err := rows.Err(); err != nil {
		t.Fatal(err)
	}
	if len(seenColumns) != len(allowedColumns) {
		t.Fatalf("license_cards column count = %d, want %d approved metadata/ciphertext columns", len(seenColumns), len(allowedColumns))
	}
}

func TestNormalListOmitsPlaintextAndErrorsAreRedacted(t *testing.T) {
	h := newHarness(t)
	software := h.software("Redaction")
	pkg := h.packageWith(software)
	card := h.generate(pkg.ID, 30)
	summaries, err := h.store.SearchCards(h.ctx, model.CardFilter{PackageID: pkg.ID})
	if err != nil {
		t.Fatal(err)
	}
	encoded, err := json.Marshal(summaries)
	if err != nil {
		t.Fatal(err)
	}
	if bytes.Contains(encoded, []byte(card.Plaintext)) || bytes.Contains(encoded, []byte(strings.ReplaceAll(card.Plaintext, "-", ""))) {
		t.Fatal("normal card list exposed plaintext")
	}

	fixture := "22222222-22222222-22222222-22222222"
	_, err = h.svc.Activate(h.ctx, "request-missing", fixture, software.ID, "machine-a")
	if !errors.Is(err, ErrNotFound) {
		t.Fatalf("missing-card error = %v, want ErrNotFound", err)
	}
	if strings.Contains(err.Error(), fixture) || strings.Contains(err.Error(), strings.ReplaceAll(fixture, "-", "")) {
		t.Fatal("missing-card error exposed plaintext")
	}
}

func TestServiceResourceLimitsRejectBeforeExpensiveWork(t *testing.T) {
	h := newHarness(t)
	software := h.software("Resource Limits")
	pkg := h.packageWith(software)
	card := h.generate(pkg.ID, 30)

	maxInt := int(^uint(0) >> 1)
	if _, err := h.svc.GenerateCards(h.ctx, pkg.ID, 30, maxInt, nil, nil); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("huge count error = %v, want ErrInvalidRequest", err)
	}
	exactNote := strings.Repeat("é", MaxNoteUTF8Bytes/2)
	if len(exactNote) != MaxNoteUTF8Bytes {
		t.Fatal("test note is not at the UTF-8 byte boundary")
	}
	if _, err := h.svc.GenerateCards(h.ctx, pkg.ID, 30, 1, &exactNote, nil); err != nil {
		t.Fatalf("boundary default note error = %v", err)
	}
	overNote := exactNote + "x"
	if _, err := h.svc.GenerateCards(h.ctx, pkg.ID, 30, 1, &overNote, nil); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("oversized default note error = %v, want ErrInvalidRequest", err)
	}
	if _, err := h.svc.GenerateCards(h.ctx, pkg.ID, 30, 1, nil, []*string{&overNote}); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("oversized per-card note error = %v, want ErrInvalidRequest", err)
	}

	boundaryCard := card.Plaintext + strings.Repeat(" ", MaxRawCardInputBytes-len(card.Plaintext))
	if _, err := h.svc.Validate(h.ctx, boundaryCard, software.ID, "machine-a"); !errors.Is(err, ErrNotFound) {
		t.Fatalf("boundary raw-card input error = %v, want normal unactivated ErrNotFound", err)
	}
	overCard := boundaryCard + " "
	if _, err := h.svc.Validate(h.ctx, overCard, software.ID, "machine-a"); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("oversized raw-card input error = %v, want ErrInvalidRequest", err)
	}

	boundaryRequest := strings.Repeat("r", model.MaxIdentifierLength)
	boundaryMachine := strings.Repeat("m", model.MaxIdentifierLength)
	if _, err := h.svc.Activate(h.ctx, boundaryRequest, card.Plaintext, software.ID, boundaryMachine); err != nil {
		t.Fatalf("boundary identifier activation error = %v", err)
	}
	overIdentifier := strings.Repeat("界", model.MaxIdentifierLength/3+1)
	if len(overIdentifier) <= model.MaxIdentifierLength {
		t.Fatal("test identifier is not over the UTF-8 byte limit")
	}
	if _, err := h.svc.Activate(h.ctx, overIdentifier, card.Plaintext, software.ID, boundaryMachine); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("oversized request id error = %v, want ErrInvalidRequest", err)
	}
	if _, err := h.svc.Validate(h.ctx, card.Plaintext, overIdentifier, boundaryMachine); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("oversized software id error = %v, want ErrInvalidRequest", err)
	}
	if _, err := h.svc.Validate(h.ctx, card.Plaintext, software.ID, overIdentifier); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("oversized machine hash error = %v, want ErrInvalidRequest", err)
	}
}

func TestGenerateCountBoundaryIsAccepted(t *testing.T) {
	h := newHarness(t)
	software := h.software("Count Boundary")
	pkg := h.packageWith(software)
	cards, err := h.svc.GenerateCards(h.ctx, pkg.ID, 30, MaxGenerateCount, nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	if len(cards) != MaxGenerateCount {
		t.Fatalf("generated count = %d, want %d", len(cards), MaxGenerateCount)
	}
}

func TestNewAndServiceInputsRejectInvalidRequests(t *testing.T) {
	key := bytes.Repeat([]byte{0x11}, 32)
	lookupKey := bytes.Repeat([]byte{0x22}, 32)
	clock := &fakeClock{now: time.Now()}
	if _, err := New(nil, clock, rand.Reader, key, lookupKey); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("nil store error = %v", err)
	}
	h := newHarness(t)
	if _, err := New(h.store, clock, rand.Reader, key[:31], lookupKey); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("short encryption key error = %v", err)
	}
	if _, err := New(h.store, clock, rand.Reader, key, key); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("identical keys error = %v", err)
	}

	software := h.software("Invalid Requests")
	pkg := h.packageWith(software)
	card := h.generate(pkg.ID, 30)
	if _, err := h.svc.GenerateCards(h.ctx, pkg.ID, 30, 0, nil, nil); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("zero count error = %v", err)
	}
	if _, err := h.svc.GenerateCards(h.ctx, pkg.ID, 30, 2, nil, []*string{nil}); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("note count mismatch error = %v", err)
	}
	if _, err := h.svc.Activate(h.ctx, "", card.Plaintext, software.ID, "machine-a"); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("empty request id error = %v", err)
	}
	if _, err := h.svc.Validate(h.ctx, "not a card", software.ID, "machine-a"); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("malformed card error = %v", err)
	}
	h.clock.now = time.Time{}
	if err := h.svc.RenewCard(h.ctx, card.Card.ID, 30); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("zero server clock renewal error = %v, want ErrInvalidRequest", err)
	}
}

func (h *harness) rawDB() *sql.DB {
	h.t.Helper()
	db, err := sql.Open("sqlite", h.dbPath)
	if err != nil {
		h.t.Fatal(err)
	}
	return db
}

func (h *harness) serviceForStore(store Store) *Service {
	h.t.Helper()
	svc, err := New(store, h.clock, rand.Reader, bytes.Repeat([]byte{0x11}, 32), bytes.Repeat([]byte{0x22}, 32))
	if err != nil {
		h.t.Fatal(err)
	}
	return svc
}

func (h *harness) searchCard(prefix8 string) model.CardSummary {
	h.t.Helper()
	cards, err := h.store.SearchCards(h.ctx, model.CardFilter{Prefix8: prefix8})
	if err != nil {
		h.t.Fatal(err)
	}
	if len(cards) != 1 {
		h.t.Fatalf("SearchCards returned %d cards, want 1", len(cards))
	}
	return cards[0]
}

func closeIfOpen(channel chan struct{}) {
	select {
	case <-channel:
	default:
		close(channel)
	}
}
