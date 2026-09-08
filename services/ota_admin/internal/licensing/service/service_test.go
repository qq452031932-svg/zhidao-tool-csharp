package service

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/base32"
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

type harness struct {
	t     *testing.T
	ctx   context.Context
	store *licensestore.Store
	clock *fakeClock
	svc   *Service
}

func newHarness(t *testing.T) *harness {
	t.Helper()
	ctx := context.Background()
	st, err := licensestore.Open(ctx, t.TempDir()+`\license.db`)
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
	return &harness{t: t, ctx: ctx, store: st, clock: clock, svc: svc}
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
	if _, err := h.svc.Activate(h.ctx, "request-b", card.Plaintext, softwareB.ID, "machine-a"); err != nil {
		t.Fatal(err)
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
	// The second renewal happens after the first renewed expiry, so it must
	// extend from the server's current time rather than the stale expiry.
	h.clock.now = state.ExpiresAt.Add(40 * 24 * time.Hour)
	if err := h.svc.RenewCard(h.ctx, active.Card.ID, 90); err != nil {
		t.Fatal(err)
	}
	summaries, err := h.store.SearchCards(h.ctx, model.CardFilter{Prefix8: active.Card.Prefix8})
	if err != nil || len(summaries) != 1 {
		t.Fatalf("SearchCards returned %d rows, error %v", len(summaries), err)
	}
	want := h.clock.now.Add(90 * 24 * time.Hour)
	if summaries[0].ExpiresAt == nil || !summaries[0].ExpiresAt.Equal(want) {
		t.Fatalf("activated renewal expiry = %v, want %v", summaries[0].ExpiresAt, want)
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
	key := bytes.Repeat([]byte{0x31}, 32)
	lookupKey := bytes.Repeat([]byte{0x42}, 32)
	protector, err := newCardProtector(key, lookupKey, bytes.NewReader(bytes.Repeat([]byte{0x53}, 64)))
	if err != nil {
		t.Fatal(err)
	}
	sealed, err := protector.seal(fixture)
	if err != nil {
		t.Fatal(err)
	}
	if bytes.Contains(sealed.ciphertext, []byte(fixture)) || bytes.Contains(sealed.ciphertext, []byte(strings.ReplaceAll(fixture, "-", ""))) {
		t.Fatal("ciphertext contains plaintext card material")
	}
	if got := protector.digest("abcdefgh jklmnpqr stuvwxyz 234567ab"); got != sealed.digest {
		t.Fatal("normalized card forms produced different keyed digests")
	}
	opened, err := protector.open(sealed.ciphertext)
	if err != nil {
		t.Fatal(err)
	}
	if opened != fixture {
		t.Fatal("decrypted card did not equal the canonical fixture")
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
